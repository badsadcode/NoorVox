"""Shared protocol for NoorVox Python engine sidecars (ADR-0002). Used by engines/<name>/noorvox_<name>.py.

One JSON object per line. Requests arrive on stdin; responses go to the ORIGINAL stdout (library output is redirected to
stderr, which the worker copies into its log). EOF on stdin means the worker is gone: exit at once.

  ready     {"event": "ready", "ok": true, "engine": "...", "python": "...", "torch": "...", "cuda": true, "cudaVersion": "...", "device": "..."}
  progress  {"event": "progress", "stage": "reading" | "warmup"}           (any time before a reply)
  request   {"id": "...", "op": "load" | "synth" | "stats" | "exit", ...}
  reply     {"id": "...", "ok": true, ...}  |  {"id": "...", "ok": false, "error": {"code": "...", "message": "..."}}
"""
import json
import os
import re
import sys
import traceback
from datetime import datetime, timezone

PROTOCOL = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
sys.stdout = sys.stderr  # keep library chatter off the protocol stream

MAX_CHUNK_CHARS = 600  # per model call; engines split longer text at sentence ends


def send(obj):
    PROTOCOL.write(json.dumps(obj, ensure_ascii=False) + "\n")
    PROTOCOL.flush()


def progress(stage):
    send({"event": "progress", "stage": stage})


def now():
    return datetime.now(timezone.utc).isoformat()


class EngineError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def vram(torch):
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


def chunks(text, limit=MAX_CHUNK_CHARS):
    """Split long text at sentence ends so no single call exceeds the model's budget."""
    if len(text) <= limit:
        return [text]
    sentences = re.split(r"(?<=[.!?…。！？])\s+", text)
    out, cur = [], ""
    for s in sentences:
        while len(s) > limit:  # a run-on "sentence": cut at the last space
            cut = s.rfind(" ", 0, limit)
            cut = cut if cut > 0 else limit
            if cur:
                out.append(cur)
                cur = ""
            out.append(s[:cut].strip())
            s = s[cut:].strip()
        if cur and len(cur) + 1 + len(s) > limit:
            out.append(cur)
            cur = s
        else:
            cur = f"{cur} {s}".strip()
    if cur:
        out.append(cur)
    return [c for c in out if c]


def seed_everything(torch, np, seed):
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)
    np.random.seed(seed % (2**32))


def write_wav(np, sf, path, parts, sr):
    """Concatenate float chunks, avoid wrap-around clipping, write 16-bit PCM. Returns the sample count."""
    audio = np.concatenate(parts) if parts else np.zeros(0, dtype=np.float32)
    if audio.size == 0:
        raise EngineError("synthesis_failed", "The engine produced no audio.")
    peak = float(np.max(np.abs(audio)))
    if peak > 1.0:
        audio = audio / peak
    sf.write(path, audio, sr, subtype="PCM_16")
    return int(audio.size)


def ready(engine_name, torch):
    send({"event": "ready", "ok": True, "engine": engine_name, "pid": os.getpid(), "python": sys.version.split()[0],
          "torch": torch.__version__, "cuda": torch.cuda.is_available(), "cudaVersion": torch.version.cuda,
          "device": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None})


def import_failed(exc):
    send({"event": "ready", "ok": False, "error": {"code": "engine_broken", "message": f"Engine import failed: {exc}"}})
    sys.exit(3)


def serve(engine, torch):
    """Request loop: engine.load(req) / engine.synth(req) return dicts merged into the reply."""
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
                result = {"vram": vram(torch)}
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
