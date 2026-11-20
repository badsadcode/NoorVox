# ADR-0002 — Qwen3-TTS as a second backend, and Kokoro on the GPU

**Status:** Proposed (spike done, integration not started) · **Date:** 2026-11-20 · **Author:** Sage · **Reviewer:** Tom

## Context

Tom asked for a Qwen voice model ("one for cloning, one for generation; audiobooks don't need to be fast"), and noticed
Kokoro runs on the CPU. ADR-0001 left GPU as "later" (DirectML fails; CUDA needs CUDA 12 + cuDNN 9).

## Facts checked (2026-11-20)

| | |
|---|---|
| Models | Qwen3-TTS (released 2026-01-22, Apache-2.0), 12 Hz family: **Base** 0.6B/1.7B = voice clone from ~3 s reference audio + transcript; **CustomVoice** 0.6B/1.7B = 9 built-in speakers (EN: Ryan, Aiden), 1.7B also follows natural-language style instructions; **VoiceDesign** 1.7B = voice from a text description. |
| Languages | zh, en, ja, ko, de, fr, ru, pt, es, it. **No Polish** (neither has Kokoro). |
| Size | 1.7B: 4.3 GB per model (3.6 GB model + 0.65 GB speech tokenizer). 0.6B: 2.4 GB. Pinned revisions in `spikes/QwenSpike/download.py`. |
| Runtime | Python ≥ 3.10; official package `qwen-tts` 0.1.1 (Apache-2.0) with transformers 4.57.3; NVIDIA GPU + CUDA required. flash-attn has no Windows build, so PyTorch SDPA is used. |
| Fast runtime | `faster-qwen3-tts` 0.5.4 (MIT): CUDA-graph capture of the decode step. Needs `qwen-tts-hf` + transformers 5.x. **transformers 5.23 breaks it**; pinned 5.15.1 works. |

## Measurements (RTX 3080 10 GB, driver 621.01, torch 2.11.0+cu128; ~5 GB of VRAM already used by other apps; 3D apps running)

| Engine / mode | Warm RTF (lower = faster) | Peak VRAM | Load (warm cache) |
|---|---|---|---|
| Kokoro, CPU (current) | 0.17 (≈6× real time) | — | 2.5 s |
| **Kokoro, CUDA** (ORT GPU 1.22 + CUDA/cuDNN DLLs from the torch cu128 install) | **0.09 (≈11×)** | small | 14.5 s first CUDA init |
| Qwen 1.7B CustomVoice, `qwen-tts` | 3.4–3.6 (slower than real time) | 4.5 GB | 2.8 s |
| Qwen 1.7B Base (clone), `qwen-tts` | 3.35 | 4.6 GB | — |
| **Qwen 1.7B CustomVoice, `faster-qwen3-tts`** | **0.38 (≈2.6×)** | 4.8 GB | + 8.7 s graph warm-up once |
| **Qwen 1.7B Base (clone), `faster-qwen3-tts`** | **0.38–0.41** | 4.9 GB | + 1.7 s warm-up |

- With the standard runtime the GPU was only 20–50 % busy: the per-token Python/kernel-launch overhead dominates. CUDA graphs fix this (9× faster).
- Kokoro CUDA vs CPU: same length and peak, envelope correlation 0.998. Waveform phase differs, as between two CPU runs (non-deterministic, ADR-0001).
- Clone check (MFCC timbre fingerprint vs the reference George clip): fast clone 0.988, standard clone 0.990, Aiden 0.952, Heart 0.944.
  The clone resembles the reference, and the fast runtime doesn't lose quality. A crude metric; listening samples are in `E:\AI\NoorVox\Exports\demo`.
- First-run costs: the runtime is ~5.1 GB (mostly torch + CUDA DLLs), each 1.7B model 4.3 GB. A cold load from a busy HDD took up to 255 s.

## Decision (proposed)

1. **Qwen runs in a Python sidecar owned by `NoorVox.Worker`.** A C# `QwenBackend : ITtsBackend` starts `python -I noorvox_qwen.py` from an isolated
   runtime and speaks newline-delimited JSON over stdin/stdout. Supervisor, IPC, registry, Job Objects, Monitor and the quit policy stay unchanged,
   and the sidecar dies with its worker.
2. **Runtime as an installable "engine pack"**, like models, installed with one click:
   - a pinned `requirements.lock` (torch 2.11.0+cu128, faster-qwen3-tts 0.5.4, transformers 5.15.1, …) installed into `<models>\runtimes\qwen\<version>`;
   - pip and HF caches redirected into NoorVox's cache folder;
   - needs a Python 3.12 (system install or the embeddable package from python.org, sha256-verified);
   - Verify = import + CUDA check.
3. **Catalog:** Qwen models (pinned revisions, per-file sha256 from the HF LFS ids). CustomVoice 1.7B and Base 1.7B first; 0.6B variants for 6 GB GPUs.
4. **Truthful capabilities:**
   - Qwen gets **style instructions** (CustomVoice 1.7B, VoiceDesign) and **voice profiles**, but **no speed slider** (ask for "slower" in the instruction).
   - Kokoro keeps speed and has no instructions.
   - Sampling is stochastic: takes record the seed, and the seed joins the cacheKey.
   - GPU VRAM is reported from `torch.cuda` (a credible source), not "unavailable".
5. **Voice profiles (Qwen):**
   - *built-in* (9 speakers);
   - *designed* (description → short reference clip → reusable clone prompt);
   - *cloned* (reference audio + transcript).
   Profiles are immutable, hashed and stored with the model.
6. **Cloning safeguards:**
   - An explicit consent statement per cloned profile ("my own voice / I have the speaker's permission"), stored with the reference provenance (file hash, date).
   - Local files only (no URLs).
   - The profile list shows which voices are clones.
   - Export manifests record `voiceKind: clone` for transparency.
7. **Kokoro GPU:** ship ONNX Runtime GPU (it falls back to CPU) and enable CUDA when CUDA 12/cuDNN 9 DLLs are available, either from the installed
   Qwen engine pack or a small "CUDA libraries" pack. It's an opt-in setting with the provider shown truthfully in the Monitor.
8. GPU sharing: a Qwen worker holds about 5 GB of VRAM. The Monitor shows it, and *Unload* frees it. Running both engines at once is allowed when VRAM permits.

## Consequences / open points

- Adds ~10 GB of optional downloads (runtime + 2 models), all user-located.
- `faster-qwen3-tts` is young (0.5.x) and pins transformers 5.x. NoorVox pins exact versions and keeps the official `qwen-tts` path as a fallback runtime.
- No Polish voices from either engine.
- Licensing: Qwen3-TTS models Apache-2.0; qwen-tts Apache-2.0; faster-qwen3-tts MIT; PyTorch BSD-3; NVIDIA CUDA/cuDNN redistributables under NVIDIA's EULA
  (shipped only inside the PyTorch wheels, the same as any pip install).
