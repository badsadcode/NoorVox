"""NoorVox Qwen3-TTS sidecar (ADR-0002). Started only by NoorVox.Worker (QwenBackend) with the qwen-tts engine's Python:
    python.exe -I -u noorvox_qwen.py
Protocol: engines/common/noorvox_sidecar.py. Synth request fields:
    {"mode": "custom"|"clone"|"design", "text", "language": "Auto"|"English"|..., "seed", "out",
     "speaker" (custom), "instruct" (custom style / design description), "refAudio" + "refText" (clone)}
"""
import os
import sys
import time

# -I (isolated mode) keeps the user's site-packages and the current directory out; NoorVox's own shared module is added explicitly.
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "common"))
import noorvox_sidecar as nv  # noqa: E402


def _stub_sox():
    """qwen-tts imports `sox` for its 25 Hz tokenizer, which the 12 Hz models NoorVox uses never call. sox has no wheel
    (sdist only), so the hash-locked engine leaves it out; this stub satisfies the import and fails loudly if used."""
    import types

    class _Missing(types.ModuleType):
        def __getattr__(self, name):
            if name.startswith("__"):  # import machinery and inspect probe these; behave like a plain module
                raise AttributeError(name)
            raise RuntimeError(f"sox.{name} was called, but the NoorVox engine does not include sox (25 Hz tokenizer only).")

    sys.modules.setdefault("sox", _Missing("sox"))


_stub_sox()

try:
    import numpy as np
    import soundfile as sf
    import torch
    from faster_qwen3_tts import FasterQwen3TTS
except Exception as exc:  # noqa: BLE001 - report any import failure to the worker
    nv.import_failed(exc)


class Engine:
    def __init__(self):
        self.model = None
        self.model_dir = None

    def load(self, req):
        if self.model is not None:
            if os.path.normcase(req["modelDir"]) == os.path.normcase(self.model_dir):
                return {"alreadyLoaded": True}
            raise nv.EngineError("worker_busy", "A different model is already loaded; recycle the worker.")
        if not torch.cuda.is_available():
            raise nv.EngineError("gpu_required", "Qwen3-TTS needs an NVIDIA GPU with CUDA; none is available to PyTorch.")
        torch.cuda.reset_peak_memory_stats()
        nv.progress("reading")
        t0 = time.perf_counter()
        self.model = FasterQwen3TTS.from_pretrained(req["modelDir"], local_files_only=True)
        load_ms = round((time.perf_counter() - t0) * 1000)
        warm_ms = 0
        if req.get("warmup", True):
            nv.progress("warmup")
            t0 = time.perf_counter()
            self.model.warmup(prefill_len=100)
            warm_ms = round((time.perf_counter() - t0) * 1000)
        self.model_dir = req["modelDir"]
        return {"loadMs": load_ms, "warmupMs": warm_ms, "vram": nv.vram(torch)}

    def synth(self, req):
        if self.model is None:
            raise nv.EngineError("model_not_installed", "No model loaded.")
        text = req["text"].strip()
        if not text:
            raise nv.EngineError("invalid_input", "Text is empty.")
        seed = int(req["seed"])
        nv.seed_everything(torch, np, seed)
        language = req.get("language") or "Auto"
        parts, sr = [], None
        t0 = time.perf_counter()
        for piece in nv.chunks(text):
            if req["mode"] == "custom":
                wavs, sr = self.model.generate_custom_voice(
                    text=piece, speaker=req["speaker"], language=language, instruct=req.get("instruct") or None)
            elif req["mode"] == "design":
                wavs, sr = self.model.generate_voice_design(text=piece, instruct=req["instruct"], language=language)
            elif req["mode"] == "clone":
                wavs, sr = self.model.generate_voice_clone(
                    text=piece, language=language, ref_audio=req["refAudio"], ref_text=req["refText"])
            else:
                raise nv.EngineError("invalid_arguments", f"Unknown mode {req['mode']!r}.")
            parts.append(np.asarray(wavs[0], dtype=np.float32).reshape(-1))
        torch.cuda.synchronize()
        elapsed_ms = round((time.perf_counter() - t0) * 1000)
        samples = nv.write_wav(np, sf, req["out"], parts, sr)
        return {"sampleRate": sr, "samples": samples, "elapsedMs": elapsed_ms, "chunks": len(parts), "seed": seed, "vram": nv.vram(torch)}


if __name__ == "__main__":
    nv.ready("faster-qwen3-tts", torch)
    nv.serve(Engine(), torch)
