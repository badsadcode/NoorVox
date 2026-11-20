# Model backends

What NoorVox can run, what each backend really supports, where its files live, and the licenses. Details and measurements:
[ADR-0001](adr/0001-kokoro-backend-and-worker-model.md).

## Kokoro-82M (the only backend in 0.1.0)

| | |
|---|---|
| Backend id | `kokoro-onnx` |
| Library | KokoroSharp 0.8.4 (MIT) → ONNX Runtime 1.22 (MIT), CPU execution provider |
| Phonemizer | MisakiSharp (Apache-2.0). espeak-ng is **not** used or shipped. |
| Model package | `kokoro-82m@1.0`: `kokoro.onnx` fp32, 325,508,342 bytes, sha256 `0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05` |
| Voice pack | `kokoro-82m-v1.0-voices`: 54 voices (`voices/*.npy`), each sha256-listed in the catalog |
| Source | github.com/Lyrcaxis/KokoroSharpBinaries release v2.0.0 (weights and voices identical to hexgrad/Kokoro-82M v1.0 as converted by KokoroSharp) |
| License | Model and voices: **Apache-2.0** (hexgrad/Kokoro-82M). Commercial use is allowed under Apache-2.0; keep the license notice when redistributing the files. |
| Output | 24 kHz, mono, 16-bit PCM WAV |
| Languages (by voice prefix) | `a` American English (20 voices), `b` British English (8), `j` Japanese (5), `z` Mandarin (8), `e` Spanish (3), `f` French (1), `h` Hindi (4), `i` Italian (2), `p` Brazilian Portuguese (3) |

### Capabilities as exposed

| Control | Supported | Notes |
|---|---|---|
| Voice | yes | any installed voice id |
| Speed | yes | 0.5–2.0 |
| Voice mixing | library supports it | not exposed in the UI/CLI yet |
| Pitch, emotion, style | **no** | not controls in Kokoro, so NoorVox doesn't show them |
| Loudness | export only | peak normalisation in `export assemble` |
| Phoneme timestamps | library supports it | not used: subtitles are segment-level |
| Deterministic output | **no** | identical inputs can differ by a few samples, so takes are identified by inputs (cacheKey), not audio hashes |

### Performance (measured on the development PC: Windows 10, CPU inference; GPU unused)

| | |
|---|---|
| Model load | ~4 s from a cold disk, ~1 s when cached by the OS |
| Synthesis | about 6× faster than real time on long passages (20 s of audio in ~3.4 s); short sentences have ~1–2 s of fixed overhead |
| Memory | worker ~1.0 GB with the model loaded; ~46 MB after Unload (process recycle) |

### GPU

Not supported in 0.1.0, and reported as "unavailable" everywhere:
- DirectML fails on Kokoro's dynamic-shape ConvTranspose (ADR-0001).
- CUDA needs CUDA 12 + cuDNN 9 at runtime and hasn't been tested.
- CPU speed is sufficient for production use.

## Model and voice files on disk

```text
<models>\kokoro-82m\1.0\
  kokoro.onnx
  noorvox.model.json            model manifest (written last = install complete)
  voices\af_heart.npy …         54 voices
  voicepacks\kokoro-82m-v1.0-voices.json
```

- Install: `noorvox models install kokoro-82m` or the studio's first run / Models page. Download → `<cache>\staging` → sha256 check → move.
- Import offline copies: `noorvox models import --from DIR`. Weights must match a catalog hash.
- Verify: `noorvox models verify` (sha256) or **Verify files** in the studio.
- Updates install side by side; older versions stay because takes record the model sha256 they were made with.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `model_not_installed` | `noorvox models install kokoro-82m`, or point the models folder at an existing install (Settings → Storage). |
| `verification_failed` after a download | Run the install again: partial files are deleted and the download restarts or resumes. |
| A voice sounds wrong for the language | The language follows the voice prefix (`bf_…` is British English). Pick a voice for the script's language. |
| A segment says nothing | Text with nothing pronounceable in the voice's language (e.g. only symbols) produces no audio and fails with `synthesis_failed`. |
| Names mispronounced | Add a replacement in the project's `voices\pronunciation.json`, e.g. `NoorEngine` → `Noor Engine`. |

## Adding a backend (for developers)

1. Implement `ITtsBackend` (`src/NoorVox.Core/Backends/Contracts.cs`) in a new `NoorVox.Backends.<Name>` project.
2. Load it **only** in a worker process. A backend needing Python or a GPU runtime gets its own worker executable, and the supervisor and IPC stay the same.
3. Report truthful `TtsCapabilities`; the UI shows only what's `true`.
4. Add the model package(s) and voice packs, with sha256 and license, to `catalog.json` (`tools/gen_catalog.py`).
5. Candidates noted so far: Qwen TTS (likely GPU + Python sidecar).
