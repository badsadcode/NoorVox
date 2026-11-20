"""Download pinned Qwen3-TTS revisions into NoorVox's models folder (all caches on E:).
  python -I download.py <models_root> <name> [<name> ...]   e.g. 1.7B-CustomVoice 1.7B-Base
"""
import os
import sys

os.environ.setdefault("HF_HOME", r"E:\AI\NoorVox\Cache\hf")
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
from huggingface_hub import snapshot_download  # noqa: E402

PINNED = {  # repo revision (commit sha) recorded 2026-11-20
    "1.7B-CustomVoice": "0c0e3051f131929182e2c023b9537f8b1c68adfe",
    "1.7B-Base": "fd4b254389122332181a7c3db7f27e918eec64e3",
    "1.7B-VoiceDesign": "5ecdb67327fd37bb2e042aab12ff7391903235d3",
    "0.6B-CustomVoice": "85e237c12c027371202489a0ec509ded67b5e4b5",
}

root = sys.argv[1]
for name in sys.argv[2:]:
    target = os.path.join(root, "qwen3-tts", name)
    path = snapshot_download(f"Qwen/Qwen3-TTS-12Hz-{name}", revision=PINNED[name], local_dir=target)
    print("downloaded", name, "->", path, flush=True)
