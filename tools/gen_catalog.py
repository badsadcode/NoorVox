"""Regenerate src/NoorVox.Core/Models/catalog.json (AGENTS.md rule 8: every file has a sha256 and a license).

  python -I tools/gen_catalog.py <kokoro voices_root> <out.json>

voices_root = a folder that CONTAINS "voices/" (the Kokoro v1.0 voice files). Qwen3-TTS file lists come from the
Hugging Face API at pinned revisions: LFS files carry their sha256; small files are downloaded and hashed here.
"""
import hashlib, json, os, sys, urllib.request

voices_root, out = sys.argv[1], sys.argv[2]
QWEN_ENGINE_BYTES = 6_000_000_000  # measured size of an installed qwen-tts engine (5.5 GB), rounded up
CBX_ENGINE_BYTES = 6_000_000_000  # measured: the spike runtime was 5.4 GB, rounded up
# Measured peak GPU memory allocated (RTX 3080, 2026-11-21): English 3.49 GB, Multilingual 3.49 GB, Turbo 3.09 GB; rounded up.
CBX_VRAM = {"chatterbox-english": 4, "chatterbox-multilingual": 4, "chatterbox-turbo": 4}
UA = {"User-Agent": "noorvox-gen-catalog"}


def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''): h.update(b)
    return h.hexdigest()


def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=120) as r:
        return r.read()


# ---------------------------------------------------------------- Kokoro (ADR-0001)
voice_files = []
d = os.path.join(voices_root, "voices")
for fn in sorted(os.listdir(d)):
    p = os.path.join(d, fn)
    if fn.endswith(".npy") and os.path.isfile(p):
        voice_files.append({"path": "voices/" + fn, "size": os.path.getsize(p), "sha256": sha(p)})
base = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/"
cat = {
  "catalogSchemaVersion": 1,
  "catalogVersion": "2026.11.20",
  "models": [{
    "id": "kokoro-82m", "version": "1.0",
    "displayName": "Kokoro 82M v1.0",
    "description": "Recommended. Natural English narration plus 8 other languages. Runs well on CPU.",
    "backend": "kokoro-onnx", "recommended": True,
    "license": "Apache-2.0", "licenseUrl": "https://huggingface.co/hexgrad/Kokoro-82M",
    "source": "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/tag/v2.0.0",
    "defaultVoice": "af_heart", "sampleRate": 24000,
    "defaultVoicePacks": ["kokoro-82m-v1.0-voices"],
    "downloads": [
      {"url": base + "kokoro.onnx", "size": 325508342, "sha256": "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05", "kind": "file", "target": "kokoro.onnx"}
    ],
    "files": [{"path": "kokoro.onnx", "size": 325508342, "sha256": "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05"}],
    "capabilities": {"speed": True, "minSpeed": 0.5, "maxSpeed": 2.0, "style": False, "cloning": False, "blending": True,
                     "seed": False, "requiresGpu": False, "languages": [], "design": False},
    "family": "kokoro",
  }],
  "voicePacks": [{
    "id": "kokoro-82m-v1.0-voices", "version": "1.0",
    "modelId": "kokoro-82m", "modelVersions": ["1.0"],
    "displayName": "Kokoro v1.0 standard voices",
    "description": "All 54 official Kokoro v1.0 voices: American & British English, Japanese, Mandarin, Spanish, French, Hindi, Italian, Brazilian Portuguese.",
    "license": "Apache-2.0", "licenseUrl": "https://huggingface.co/hexgrad/Kokoro-82M",
    "source": "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/tag/v2.0.0",
    "downloads": [
      {"url": base + "voices.zip", "size": 65813438, "sha256": "313d823ee9ea8828c14b28042b933bbc03955d402afb785cbe0b877827d3ea1d", "kind": "zip", "target": "."}
    ],
    "files": voice_files
  }]
}

# ---------------------------------------------------------------- Qwen3-TTS (ADR-0002)
QWEN_LANGS = ["auto", "english", "chinese", "japanese", "korean", "german", "french", "russian", "portuguese", "spanish", "italian"]
QWEN_SPEAKERS = [  # the model config's spk_id; descriptions paraphrase the Qwen3-TTS model card
    ("aiden", "Aiden", "male", "english", "Sunny American male voice with a clear midrange."),
    ("ryan", "Ryan", "male", "english", "Dynamic male voice with strong rhythmic drive."),
    ("vivian", "Vivian", "female", "chinese", "Bright, slightly edgy young female voice."),
    ("serena", "Serena", "female", "chinese", "Warm, gentle young female voice."),
    ("uncle_fu", "Uncle Fu", "male", "chinese", "Seasoned male voice with a low, mellow timbre."),
    ("dylan", "Dylan", "male", "chinese", "Youthful Beijing male voice (Beijing dialect)."),
    ("eric", "Eric", "male", "chinese", "Lively Chengdu male voice, slightly husky (Sichuan dialect)."),
    ("ono_anna", "Ono Anna", "female", "japanese", "Playful Japanese female voice, light and nimble."),
    ("sohee", "Sohee", "female", "korean", "Warm Korean female voice with rich emotion."),
]
QWEN = [  # name, pinned revision, kind, measured peak VRAM rounded up to GB (None = not measured yet)
    ("1.7B-CustomVoice", "0c0e3051f131929182e2c023b9537f8b1c68adfe", "custom", 6),
    ("1.7B-Base", "fd4b254389122332181a7c3db7f27e918eec64e3", "clone", 6),
    ("0.6B-CustomVoice", "85e237c12c027371202489a0ec509ded67b5e4b5", "custom", None),
    ("0.6B-Base", "5d83992436eae1d760afd27aff78a71d676296fc", "clone", None),
    ("1.7B-VoiceDesign", "5ecdb67327fd37bb2e042aab12ff7391903235d3", "design", None),
]
KIND_ID = {"custom": "customvoice", "clone": "base", "design": "voicedesign"}
KIND_NAME = {"custom": "CustomVoice", "clone": "Base (voice cloning)", "design": "VoiceDesign (voice from a description)"}


def qwen_model(name, rev, kind, vram):
    repo = f"Qwen/Qwen3-TTS-12Hz-{name}"
    tree = json.loads(fetch(f"https://huggingface.co/api/models/{repo}/tree/{rev}?recursive=1"))
    files, downloads = [], []
    for e in sorted(tree, key=lambda e: e["path"]):
        if e["type"] != "file" or e["path"] == ".gitattributes":
            continue
        url = f"https://huggingface.co/{repo}/resolve/{rev}/{e['path']}"
        digest = e["lfs"]["oid"] if e.get("lfs") else hashlib.sha256(fetch(url)).hexdigest()
        files.append({"path": e["path"], "size": e["size"], "sha256": digest})
        downloads.append({"url": url, "size": e["size"], "sha256": digest, "kind": "file", "target": e["path"]})
    size = name.split("-")[0]
    big = size == "1.7B"
    caps = {"speed": False, "minSpeed": 1.0, "maxSpeed": 1.0, "style": kind == "custom" and big, "cloning": kind == "clone",
            "design": kind == "design", "blending": False, "seed": True, "requiresGpu": True, "languages": QWEN_LANGS}
    if kind == "custom":
        desc = f"Qwen3-TTS {size}: 9 built-in speakers" + (
            " that follow written style directions (\"calm, warm, a hint of humour\")." if big else ".") + " Needs an NVIDIA GPU. 10 languages."
    elif kind == "design":
        desc = (f"Qwen3-TTS {size}: creates a new voice from a written description (\"a warm British woman in her fifties, calm audiobook narrator\"). "
                "Save a voice you like and the Base model speaks with it. Needs an NVIDIA GPU. 10 languages.")
    else:
        desc = f"Qwen3-TTS {size}: speaks in a voice cloned from a short recording (with consent). Needs an NVIDIA GPU. 10 languages."
    m = {
        "id": f"qwen3-tts-{size.lower()}-{KIND_ID[kind]}", "version": "1.0",
        "displayName": f"Qwen3-TTS {size} {KIND_NAME[kind]}",
        "description": desc, "backend": "qwen3-tts", "recommended": False,
        "license": "Apache-2.0", "licenseUrl": f"https://huggingface.co/{repo}", "source": f"https://huggingface.co/{repo}/tree/{rev}",
        "defaultVoice": {"custom": "aiden", "design": "designed"}.get(kind, ""), "sampleRate": 24000, "defaultVoicePacks": [],
        "downloads": downloads, "files": files, "capabilities": caps, "engine": "qwen-tts", "family": "qwen3-tts", "minVramGb": vram,
    }
    if kind == "design":  # one pseudo-voice: the description in the style field defines it on every call
        m["builtInVoices"] = [{"id": "designed", "displayName": "Designed voice", "gender": "unknown", "language": "auto",
                               "description": "A new voice made from your written description. Save one you like as a permanent voice."}]
    if kind == "custom":
        m["builtInVoices"] = [{"id": i, "displayName": n, "gender": g, "language": l, "description": t} for i, n, g, l, t in QWEN_SPEAKERS]
    print(m["id"], len(files), "files", sum(f["size"] for f in files), "bytes", file=sys.stderr)
    return m


for q in QWEN:
    cat["models"].append(qwen_model(*q))

# ---------------------------------------------------------------- Chatterbox (Resemble AI, MIT)
# Each variant lists exactly the files its chatterbox-tts 0.1.7 loader reads (ChatterboxTTS / ChatterboxMultilingualTTS /
# ChatterboxTurboTTS.from_local); the English repo also holds ~10 GB of other versions NoorVox doesn't download.
CBX_REV = "5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18"          # ResembleAI/chatterbox, 2026-06-10
CBX_TURBO_REV = "749d1c1a46eb10492095d68fbcf55691ccf137cd"    # ResembleAI/chatterbox-turbo, 2025-12-15
CBX_LANGS = ["arabic", "danish", "german", "greek", "english", "spanish", "finnish", "french", "hebrew", "hindi", "italian",
             "japanese", "korean", "malay", "dutch", "norwegian", "polish", "portuguese", "russian", "swedish", "swahili",
             "turkish", "chinese"]
CHATTERBOX = [  # id, display name, repo, revision, files, multilingual, sliders, description
    ("chatterbox-english", "Chatterbox (English)", "ResembleAI/chatterbox", CBX_REV,
     ["README.md", "ve.safetensors", "t3_cfg.safetensors", "s3gen.safetensors", "tokenizer.json", "conds.pt"], False, True,
     "Resemble AI's Chatterbox: natural English voices with an emotion-intensity slider and a pace control. Speaks in any of your "
     "voices (clones, designed, saved) from the reference audio alone. Output carries an inaudible watermark. Needs an NVIDIA GPU."),
    ("chatterbox-multilingual", "Chatterbox Multilingual", "ResembleAI/chatterbox", CBX_REV,
     ["README.md", "ve.pt", "t3_mtl23ls_v2.safetensors", "s3gen.pt", "grapheme_mtl_merged_expanded_v1.json", "Cangjie5_TC.json", "conds.pt"], True, True,
     "Chatterbox in 23 languages (incl. German, French, Spanish, Polish, Japanese, Chinese) with emotion and pace controls. "
     "Uses your voices from their reference audio. Watermarked output. Needs an NVIDIA GPU."),
    ("chatterbox-turbo", "Chatterbox Turbo", "ResembleAI/chatterbox-turbo", CBX_TURBO_REV,
     ["README.md", "ve.safetensors", "t3_turbo_v1.safetensors", "t3_turbo_v1.yaml", "s3gen_meanflow.safetensors", "conds.pt",
      "added_tokens.json", "merges.txt", "special_tokens_map.json", "tokenizer_config.json", "vocab.json"], False, False,
     "Chatterbox Turbo: a faster, distilled English model. No emotion or pace controls (the model ignores them). Uses your voices "
     "from their reference audio. Watermarked output. Needs an NVIDIA GPU."),
]


def chatterbox_model(mid, name, repo, rev, wanted, multilingual, sliders, desc):
    tree = {e["path"]: e for e in json.loads(fetch(f"https://huggingface.co/api/models/{repo}/tree/{rev}?recursive=1")) if e["type"] == "file"}
    files, downloads = [], []
    for path in wanted:
        e = tree[path]
        url = f"https://huggingface.co/{repo}/resolve/{rev}/{path}"
        digest = e["lfs"]["oid"] if e.get("lfs") else hashlib.sha256(fetch(url)).hexdigest()
        files.append({"path": path, "size": e["size"], "sha256": digest})
        downloads.append({"url": url, "size": e["size"], "sha256": digest, "kind": "file", "target": path})
    caps = {"speed": False, "minSpeed": 1.0, "maxSpeed": 1.0, "style": False, "cloning": True, "design": False, "blending": False,
            "seed": True, "requiresGpu": True, "languages": CBX_LANGS if multilingual else [],
            "emotion": sliders, "emotionMin": 0.25 if sliders else 0, "emotionMax": 2.0 if sliders else 0, "emotionDefault": 0.5 if sliders else 0,
            # pace = cfg_weight. 0.0 crashes chatterbox-tts 0.1.7 (tensor size mismatch), so the range starts at 0.1.
            # Measured (English, 3 seeds): 0.1 → 8.85 s, 0.5 → 8.11 s, 1.0 → 7.87 s for the same text: lower is slower, gently.
            "pace": sliders, "paceMin": 0.1 if sliders else 0, "paceMax": 1.0 if sliders else 0, "paceDefault": 0.5 if sliders else 0,
            "watermark": "perth",
            # Inline tags: only where the authors document them (the Turbo README) and the tokenizer defines them. The English
            # tokenizer also contains tag-like tokens, but nothing says that model was trained on them, so none are offered.
            "tags": sorted(json.loads(fetch(f"https://huggingface.co/{repo}/resolve/{rev}/added_tokens.json")).keys()) if mid == "chatterbox-turbo" else []}
    m = {"id": mid, "version": "1.0", "displayName": name, "description": desc, "backend": "chatterbox", "recommended": False,
         "license": "MIT", "licenseUrl": f"https://huggingface.co/{repo}", "source": f"https://huggingface.co/{repo}/tree/{rev}",
         "defaultVoice": "default", "sampleRate": 24000, "defaultVoicePacks": [], "downloads": downloads, "files": files,
         "capabilities": caps, "engine": "chatterbox", "family": "chatterbox", "minVramGb": CBX_VRAM.get(mid),
         "builtInVoices": [{"id": "default", "displayName": "Chatterbox default", "gender": "unknown", "language": "english" if not multilingual else "auto",
                            "description": "The voice that ships with the model. Pick one of your voices for anything else."}]}
    print(mid, len(files), "files", sum(f["size"] for f in files), "bytes", file=sys.stderr)
    return m


for c in CHATTERBOX:
    cat["models"].append(chatterbox_model(*c))

ORT = "runtimes/win-x64/native/"
cat["engines"] = [
  {
    "id": "chatterbox", "version": "1.0", "kind": "python",
    "displayName": "Chatterbox engine (PyTorch + CUDA)",
    "description": "A private Python 3.12 with PyTorch 2.6 (CUDA 12.4) and Resemble AI's chatterbox-tts 0.1.7, installed from a hash-locked list. Needed by Chatterbox voices (it pins an older PyTorch than Qwen, so it is a separate pack).",
    "license": "PSF-2.0 (Python), BSD-3-Clause (PyTorch), MIT (chatterbox-tts, resemble-perth); NVIDIA CUDA/cuDNN under NVIDIA's EULA (inside the PyTorch wheel)",
    "licenseUrl": "https://github.com/resemble-ai/chatterbox", "source": "https://www.nuget.org/packages/python/3.12.10 + PyPI + download.pytorch.org (cu124)",
    "downloads": [{"url": "https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg", "size": 14515433,
                   "sha256": "0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8", "kind": "zip-tree", "target": "python", "prefix": "tools/"}],
    "files": [], "lock": "chatterbox.lock",
    "pipIndexUrls": ["https://pypi.org/simple", "https://download.pytorch.org/whl/cu124"],
    "approxInstallBytes": CBX_ENGINE_BYTES, "requires": [],
    # perth's implicit watermarker fails to import without pkg_resources, and Chatterbox can't start without it: check it here.
    "verifyImports": ["chatterbox.tts", "chatterbox.tts_turbo", "chatterbox.mtl_tts", "perth.perth_net.perth_net_implicit.perth_watermarker"],
  },
  {
    "id": "qwen-tts", "version": "1.0", "kind": "python",
    "displayName": "Qwen3-TTS engine (PyTorch + CUDA)",
    "description": "A private Python 3.12 with PyTorch 2.11 (CUDA 12.8), faster-qwen3-tts and transformers, installed from a hash-locked list. Needed by Qwen voices; also brings the CUDA libraries Kokoro's GPU option uses.",
    "license": "PSF-2.0 (Python), BSD-3-Clause (PyTorch), Apache-2.0 (transformers, qwen-tts), MIT (faster-qwen3-tts); NVIDIA CUDA/cuDNN under NVIDIA's EULA (inside the PyTorch wheel)",
    "licenseUrl": "https://github.com/andimarafioti/faster-qwen3-tts",
    "source": "https://www.nuget.org/packages/python/3.12.10 + PyPI + download.pytorch.org (cu128)",
    "downloads": [{"url": "https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg", "size": 14515433,
                   "sha256": "0eb85c2dfccccf1b17352de4c397f69194035b7d37149eacc16f1147d93de3b8", "kind": "zip-tree", "target": "python", "prefix": "tools/"}],
    "files": [], "lock": "qwen-tts.lock",
    "pipIndexUrls": ["https://pypi.org/simple", "https://download.pytorch.org/whl/cu128"],
    "approxInstallBytes": QWEN_ENGINE_BYTES, "requires": [],
    "verifyImports": ["faster_qwen3_tts", "transformers"],
  },
  {
    "id": "onnxruntime-cuda", "version": "1.22.0", "kind": "native",
    "displayName": "ONNX Runtime CUDA provider (Kokoro on GPU)",
    "description": "ONNX Runtime 1.22 GPU build for Kokoro. Uses the CUDA 12 / cuDNN 9 libraries from the Qwen3-TTS engine.",
    "license": "MIT", "licenseUrl": "https://github.com/microsoft/onnxruntime/blob/main/LICENSE",
    "source": "https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu.Windows/1.22.0",
    "downloads": [{"url": "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.gpu.windows/1.22.0/microsoft.ml.onnxruntime.gpu.windows.1.22.0.nupkg",
                   "size": 207686427, "sha256": "5496c77ed4ea1835caf27fe2f1cc3e52f68fc4a29c3dd84125b8e15c0e444daa", "kind": "zip", "target": "."}],
    "files": [
      {"path": ORT + "onnxruntime.dll", "size": 13312032, "sha256": "8c934c01f34702f60de665273876dbe9f70fcbbe0f7e0f292840ac0c13daa338"},
      {"path": ORT + "onnxruntime_providers_cuda.dll", "size": 322615352, "sha256": "0f32c09da925ec58c650a0d72ccc950a73db570cae0adeffb3d07165419b8bd1"},
      {"path": ORT + "onnxruntime_providers_shared.dll", "size": 22072, "sha256": "3b53c353cd52a7be926beb289277b79de1e659a1dc3bb74b24c99a6a7296d9d9"},
    ],
    "approxInstallBytes": 0, "requires": ["qwen-tts"],
  },
]

with open(out, "w", newline="\n") as f:
    json.dump(cat, f, indent=2)
    f.write("\n")
print(len(voice_files), "voices", file=sys.stderr)
