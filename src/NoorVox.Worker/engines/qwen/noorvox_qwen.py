"""NoorVox Qwen3-TTS sidecar (ADR-0002).

Started only by NoorVox.Worker (QwenBackend) with the engine pack's private Python:
    python.exe -I -u noorvox_qwen.py
Protocol: one JSON object per line. Requests arrive on stdin, responses go to the ORIGINAL stdout.
Anything libraries print is redirected to stderr, which the worker copies into its log.
EOF on stdin means the worker is gone: exit at once (the worker's Job Object is the second safety net).

Requests:  {"id": "...", "op": "load", "modelDir": "...", "warmup": true}
           {"id": "...", "op": "synth", "mode": "custom"|"clone", "text": "...", "language": "Auto"|"English"|...,
            "speaker": "aiden", "instruct": "...", "refAudio": "...", "refText": "...", "seed": 123, "out": "x.wav"}
Responses: {"id": "...", "ok": true, ...} | {"id": "...", "ok": false, "error": {"code": "...", "message": "..."}}
Events (no id, any time before a reply): {"event": "progress", "stage": "reading" | "warmup"}
"""
import json
import os
import re
import sys
import time
import traceback
from datetime import datetime, timezone

PROTOCOL = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
sys.stdout = sys.stderr  # keep library chatter off the protocol stream

MAX_CHUNK_CHARS = 600  # ~40 s of speech; the model's token budget per call is ~170 s


def send(obj):
    PROTOCOL.write(json.dumps(obj, ensure_ascii=False) + "\n")
    PROTOCOL.flush()


def now():
    return datetime.now(timezone.utc).isoformat()


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
    send({"event": "ready", "ok": False, "error": {"code": "engine_broken", "message": f"Engine import failed: {exc}"}})
    sys.exit(3)


def vram():
    if not torch.cuda.is_available():
        return None
    free, total = torch.cuda.mem_get_info()
    return {
        "allocatedBytes": torch.cuda.memory_allocated(),
        "reservedBytes": torch.cuda.memory_reserved(),
        "peakAllocatedBytes": torch.cuda.max_memory_allocated(),
        "deviceFreeBytes": free,
        "deviceTotalBytes": total,
        "device": torch.cuda.get_device_name(0),
        "source": "torch.cuda",
        "sampledAt": now(),
    }


def chunks(text):
    """Split long text at sentence ends so no single call exceeds the model's token budget."""
    if len(text) <= MAX_CHUNK_CHARS:
        return [text]
    sentences = re.split(r"(?<=[.!?…。！？])\s+", text)
    out, cur = [], ""
    for s in sentences:
        while len(s) > MAX_CHUNK_CHARS:  # a run-on "sentence": cut at the last space
            cut = s.rfind(" ", 0, MAX_CHUNK_CHARS)
            cut = cut if cut > 0 else MAX_CHUNK_CHARS
            if cur:
                out.append(cur)
                cur = ""
            out.append(s[:cut].strip())
            s = s[cut:].strip()
        if cur and len(cur) + 1 + len(s) > MAX_CHUNK_CHARS:
            out.append(cur)
            cur = s
        else:
            cur = f"{cur} {s}".strip()
    if cur:
        out.append(cur)
    return [c for c in out if c]


class Engine:
    def __init__(self):
        self.model = None
        self.model_dir = None

    def load(self, req):
        if self.model is not None:
            if os.path.normcase(req["modelDir"]) == os.path.normcase(self.model_dir):
                return {"alreadyLoaded": True}
            raise EngineError("worker_busy", "A different model is already loaded; recycle the worker.")
        if not torch.cuda.is_available():
            raise EngineError("gpu_required", "Qwen3-TTS needs an NVIDIA GPU with CUDA; none is available to PyTorch.")
        torch.cuda.reset_peak_memory_stats()
        send({"event": "progress", "stage": "reading"})
        t0 = time.perf_counter()
        self.model = FasterQwen3TTS.from_pretrained(req["modelDir"], local_files_only=True)
        load_ms = round((time.perf_counter() - t0) * 1000)
        warm_ms = 0
        if req.get("warmup", True):
            send({"event": "progress", "stage": "warmup"})
            t0 = time.perf_counter()
            self.model.warmup(prefill_len=100)
            warm_ms = round((time.perf_counter() - t0) * 1000)
        self.model_dir = req["modelDir"]
        return {"loadMs": load_ms, "warmupMs": warm_ms, "vram": vram()}

    def synth(self, req):
        if self.model is None:
            raise EngineError("model_not_installed", "No model loaded.")
        text = req["text"].strip()
        if not text:
            raise EngineError("invalid_input", "Text is empty.")
        seed = int(req["seed"])
        torch.manual_seed(seed)
        torch.cuda.manual_seed_all(seed)
        np.random.seed(seed % (2**32))
        language = req.get("language") or "Auto"
        parts, sr = [], None
        t0 = time.perf_counter()
        for piece in chunks(text):
            if req["mode"] == "custom":
                wavs, sr = self.model.generate_custom_voice(
                    text=piece, speaker=req["speaker"], language=language, instruct=req.get("instruct") or None)
            elif req["mode"] == "clone":
                wavs, sr = self.model.generate_voice_clone(
                    text=piece, language=language, ref_audio=req["refAudio"], ref_text=req["refText"])
            else:
                raise EngineError("invalid_arguments", f"Unknown mode {req['mode']!r}.")
            parts.append(np.asarray(wavs[0], dtype=np.float32).reshape(-1))
        torch.cuda.synchronize()
        elapsed_ms = round((time.perf_counter() - t0) * 1000)
        audio = np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)
        if audio.size == 0:
            raise EngineError("synthesis_failed", "Qwen3-TTS produced no audio.")
        peak = float(np.max(np.abs(audio)))
        if peak > 1.0:  # avoid wrap-around clipping in the 16-bit file
            audio = audio / peak
        sf.write(req["out"], audio, sr, subtype="PCM_16")
        return {"sampleRate": sr, "samples": int(audio.size), "elapsedMs": elapsed_ms, "chunks": len(parts),
                "seed": seed, "vram": vram()}


class EngineError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def main():
    send({"event": "ready", "ok": True, "pid": os.getpid(), "python": sys.version.split()[0], "torch": torch.__version__,
          "cuda": torch.cuda.is_available(), "cudaVersion": torch.version.cuda,
          "device": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None})
    engine = Engine()
    for line in sys.stdin:  # EOF = worker gone
        line = line.strip()
        if not line:
            continue
        rid = None
        try:
            req = json.loads(line)
            rid = req.get("id")
            op = req.get("op")
            if op == "load":
                result = engine.load(req)
            elif op == "synth":
                result = engine.synth(req)
            elif op == "stats":
                result = {"vram": vram()}
            elif op == "exit":
                send({"id": rid, "ok": True})
                return
            else:
                raise EngineError("invalid_arguments", f"Unknown op {op!r}.")
            send({"id": rid, "ok": True, **result})
        except EngineError as e:
            send({"id": rid, "ok": False, "error": {"code": e.code, "message": str(e)}})
        except Exception as e:  # noqa: BLE001
            traceback.print_exc()
            code = "gpu_out_of_memory" if "out of memory" in str(e).lower() else "synthesis_failed"
            send({"id": rid, "ok": False, "error": {"code": code, "message": f"{type(e).__name__}: {e}"}})


if __name__ == "__main__":
    main()
