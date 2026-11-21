"""Drives NoorVox's Chatterbox sidecar (src/NoorVox.Worker/engines/chatterbox/noorvox_chatterbox.py) directly, as the worker would,
and reports measured timings, durations and GPU memory. ADR-0003 measurements.
  python -I drive.py <engine python.exe> <variant> <model dir> <out dir> <ref1.wav> [<ref2.wav>]
"""
import json
import os
import subprocess
import sys
import wave

engine_py, variant, model_dir, out_dir, ref1 = sys.argv[1:6]
ref2 = sys.argv[6] if len(sys.argv) > 6 else None
script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "NoorVox.Worker", "engines", "chatterbox", "noorvox_chatterbox.py")
os.makedirs(out_dir, exist_ok=True)
env = dict(os.environ, HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
p = subprocess.Popen([engine_py, "-I", "-u", os.path.abspath(script)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open(os.path.join(out_dir, "stderr.log"), "w"),
                     text=True, encoding="utf-8", env=env)
TEXT = "It started, as so many terrible ideas do, with a single image. A window, a device, and a triangle. Nothing more."
n = 0


def call(req):
    global n
    n += 1
    req["id"] = str(n)
    p.stdin.write(json.dumps(req) + "\n")
    p.stdin.flush()
    while True:
        msg = json.loads(p.stdout.readline())
        if msg.get("event") == "progress":
            continue
        return msg


def secs(path):
    with wave.open(path) as w:
        return w.getnframes() / w.getframerate()


ready = json.loads(p.stdout.readline())
print("ready:", {k: ready.get(k) for k in ("ok", "engine", "torch", "cudaVersion", "device")})
r = call({"op": "load", "modelDir": model_dir, "variant": variant, "warmup": True})
print(f"load: ok={r['ok']} loadMs={r.get('loadMs')} warmupMs={r.get('warmupMs')} reservedGB={r['vram']['reservedBytes']/2**30:.2f}" if r["ok"] else r)

cases = [("default_s1", {}, 1), ("george_s7", {"refAudio": ref1}, 7), ("george_s7_again", {"refAudio": ref1}, 7)]
if variant != "turbo":
    cases += [("george_emo0.25", {"refAudio": ref1, "emotion": 0.25}, 7), ("george_emo1.5", {"refAudio": ref1, "emotion": 1.5}, 7),
              ("george_pace0.2", {"refAudio": ref1, "pace": 0.2}, 7), ("george_pace0.9", {"refAudio": ref1, "pace": 0.9}, 7)]
if variant == "multilingual":
    cases += [("george_german", {"refAudio": ref1, "languageId": "de", "text": "Es begann, wie so viele schreckliche Ideen, mit einem einzigen Bild."}, 7)]
if ref2:
    cases += [("margaret_s3", {"refAudio": ref2}, 3)]
peak = 0
for name, extra, seed in cases:
    out = os.path.join(out_dir, f"{variant}_{name}.wav")
    req = {"op": "synth", "text": extra.pop("text", TEXT), "seed": seed, "out": out, **extra}
    r = call(req)
    if not r["ok"]:
        print(name, "FAILED", r["error"])
        continue
    d = secs(out)
    peak = max(peak, r["vram"]["peakAllocatedBytes"])
    print(f"{name:18} audio {d:5.2f} s  synth {r['elapsedMs']/1000:5.2f} s  RTF {r['elapsedMs']/1000/d:.2f}  watermark={r.get('watermark')}")
a, b = (os.path.join(out_dir, f"{variant}_george_s7.wav"), os.path.join(out_dir, f"{variant}_george_s7_again.wav"))
if os.path.exists(a) and os.path.exists(b):
    print("same seed -> identical file:", open(a, "rb").read() == open(b, "rb").read())
print(f"peak GPU memory allocated: {peak/2**30:.2f} GB")
p.stdin.close()
p.wait(timeout=60)
