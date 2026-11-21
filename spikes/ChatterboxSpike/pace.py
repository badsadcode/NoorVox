"""Does Chatterbox's cfg_weight ("pace") really change pacing? Same text and seeds, cfg 0.0 / 0.5 / 1.0; measured audio durations.
  python -I pace.py <engine python.exe> <model dir> <out dir> [<ref.wav>]
"""
import json, os, subprocess, sys, wave
engine_py, model_dir, out_dir = sys.argv[1:4]
ref = sys.argv[4] if len(sys.argv) > 4 else None
script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "NoorVox.Worker", "engines", "chatterbox", "noorvox_chatterbox.py")
os.makedirs(out_dir, exist_ok=True)
p = subprocess.Popen([engine_py, "-I", "-u", os.path.abspath(script)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                     text=True, encoding="utf-8", env=dict(os.environ, HF_HUB_OFFLINE="1", PYTHONUTF8="1"))
TEXT = ("Every engine starts somewhere. This one started on a rainy Tuesday, with a cup of cold coffee, a stubborn idea, "
        "and absolutely no plan for what would happen next.")
n = 0
def call(req):
    global n; n += 1; req["id"] = str(n)
    p.stdin.write(json.dumps(req) + "\n"); p.stdin.flush()
    while True:
        m = json.loads(p.stdout.readline())
        if m.get("event") != "progress": return m
json.loads(p.stdout.readline())
assert call({"op": "load", "modelDir": model_dir, "variant": "english"})["ok"]
for pace in (0.1, 0.3):
    ds = []
    for seed in (1, 2, 3):
        out = os.path.join(out_dir, f"pace{pace}_s{seed}.wav")
        r = call({"op": "synth", "text": TEXT, "seed": seed, "out": out, "pace": pace, "emotion": 0.5, **({"refAudio": ref} if ref else {})})
        if not r["ok"]: print("pace", pace, "seed", seed, "ERROR", r["error"]); continue
        with wave.open(out) as w: ds.append(w.getnframes() / w.getframerate())
    if ds: print(f"pace {pace:.1f}: " + "  ".join(f"{d:5.2f}s" for d in ds) + f"   mean {sum(ds)/len(ds):5.2f}s")
p.stdin.close(); p.wait(timeout=60)
