import hashlib, json, os, sys
voices_root, out = sys.argv[1], sys.argv[2]   # voices_root = folder that CONTAINS "voices/"
def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''): h.update(b)
    return h.hexdigest()
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
    "files": [{"path": "kokoro.onnx", "size": 325508342, "sha256": "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05"}]
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
with open(out, "w", newline="\n") as f: json.dump(cat, f, indent=2); f.write("\n")
print(len(voice_files), "voices")
