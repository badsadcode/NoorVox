"""NoorVox Chatterbox sidecar (Resemble AI, MIT). Started only by NoorVox.Worker (ChatterboxBackend) with the chatterbox engine's
Python:  python.exe -I -u noorvox_chatterbox.py
Protocol: engines/common/noorvox_sidecar.py. Load request adds {"variant": "english" | "multilingual" | "turbo"}.
Synth request fields: {"text", "seed", "out", "refAudio" (optional: a voice profile's reference; else the built-in voice),
"emotion" (exaggeration), "pace" (cfg weight), "languageId" (multilingual: "en", "de", …)}.
Every variant watermarks its output (Resemble AI Perth); NoorVox records that in the take's provenance.
"""
import os
import sys
import time

# -I (isolated mode) keeps the user's site-packages and the current directory out; NoorVox's own shared module is added explicitly.
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "common"))
import noorvox_sidecar as nv  # noqa: E402

try:
    import numpy as np
    import soundfile as sf
    import torch
    import chatterbox
except Exception as exc:  # noqa: BLE001 - report any import failure to the worker
    nv.import_failed(exc)

CHUNK_CHARS = 400  # Chatterbox's speech-token budget per call is smaller than Qwen's; split long text at sentence ends


class Engine:
    def __init__(self):
        self.model = None
        self.model_dir = None
        self.variant = None

    def load(self, req):
        if self.model is not None:
            if os.path.normcase(req["modelDir"]) == os.path.normcase(self.model_dir):
                return {"alreadyLoaded": True}
            raise nv.EngineError("worker_busy", "A different model is already loaded; recycle the worker.")
        if not torch.cuda.is_available():
            raise nv.EngineError("gpu_required", "Chatterbox in NoorVox needs an NVIDIA GPU with CUDA; none is available to PyTorch.")
        variant = req.get("variant", "english")
        torch.cuda.reset_peak_memory_stats()
        nv.progress("reading")
        t0 = time.perf_counter()
        if variant == "english":
            from chatterbox.tts import ChatterboxTTS as cls
        elif variant == "multilingual":
            from chatterbox.mtl_tts import ChatterboxMultilingualTTS as cls
        elif variant == "turbo":
            from chatterbox.tts_turbo import ChatterboxTurboTTS as cls
        else:
            raise nv.EngineError("invalid_arguments", f"Unknown Chatterbox variant {variant!r}.")
        self.model = cls.from_local(req["modelDir"], "cuda")
        load_ms = round((time.perf_counter() - t0) * 1000)
        warm_ms = 0
        if req.get("warmup", True):  # first generation compiles kernels; do it now so the first real line isn't slow
            nv.progress("warmup")
            t0 = time.perf_counter()
            kwargs = {"language_id": "en"} if variant == "multilingual" else {}
            self.model.generate("Warming up.", **kwargs)
            torch.cuda.synchronize()
            warm_ms = round((time.perf_counter() - t0) * 1000)
        self.model_dir, self.variant = req["modelDir"], variant
        return {"loadMs": load_ms, "warmupMs": warm_ms, "vram": nv.vram(torch)}

    def synth(self, req):
        if self.model is None:
            raise nv.EngineError("model_not_installed", "No model loaded.")
        text = req["text"].strip()
        if not text:
            raise nv.EngineError("invalid_input", "Text is empty.")
        seed = int(req["seed"])
        kwargs = {}
        if req.get("refAudio"):
            kwargs["audio_prompt_path"] = req["refAudio"]
        if self.variant != "turbo":  # Turbo ignores these (its code says so); NoorVox doesn't offer them there
            kwargs["exaggeration"] = float(req.get("emotion", 0.5))
            kwargs["cfg_weight"] = float(req.get("pace", 0.5))
        if self.variant == "multilingual":
            kwargs["language_id"] = req.get("languageId") or "en"
        parts = []
        t0 = time.perf_counter()
        for i, piece in enumerate(nv.chunks(text, CHUNK_CHARS)):
            nv.seed_everything(torch, np, seed + i)  # per chunk, so a chunk's audio doesn't depend on the ones before it
            wav = self.model.generate(piece, **kwargs)
            parts.append(wav.squeeze(0).detach().cpu().numpy().astype(np.float32).reshape(-1))
            kwargs.pop("audio_prompt_path", None)  # conditionals are prepared once and kept for the following chunks
        torch.cuda.synchronize()
        elapsed_ms = round((time.perf_counter() - t0) * 1000)
        samples = nv.write_wav(np, sf, req["out"], parts, self.model.sr)
        return {"sampleRate": self.model.sr, "samples": samples, "elapsedMs": elapsed_ms, "chunks": len(parts), "seed": seed,
                "watermark": "perth", "vram": nv.vram(torch)}


if __name__ == "__main__":
    nv.ready(f"chatterbox-tts {getattr(chatterbox, '__version__', '0.1.7')}", torch)
    nv.serve(Engine(), torch)
