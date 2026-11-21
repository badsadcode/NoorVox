# ADR-0003 — Chatterbox as a third engine

**Status:** Accepted and implemented in 0.4.0 · **Date:** 2026-11-21 · **Author:** Sage · **Reviewer:** Tom

## Context

Tom wanted a lighter, popular model alongside Qwen3-TTS. Chatterbox (Resemble AI, MIT) offers voice cloning from audio alone (no
transcript) and an emotion-intensity control, which no NoorVox engine had.

## Facts checked (2026-11-21, from PyPI, Hugging Face and the package source, not from memory)

| | |
|---|---|
| Package | `chatterbox-tts` 0.1.7 (2026-03-26), MIT. Pins **torch 2.6.0**, torchaudio 2.6.0, transformers 5.2.0, numpy < 2, gradio 6.8.0. |
| Conflict | Qwen's engine uses torch 2.11 / transformers 5.15.1, so Chatterbox needs its **own engine pack** (`chatterbox`, Python 3.12.10 + torch 2.6.0+cu124). |
| Variants | `ChatterboxTTS` (English), `ChatterboxMultilingualTTS` (23 languages), `ChatterboxTurboTTS` (repo `ResembleAI/chatterbox-turbo`). |
| Files | English: `ve/t3_cfg/s3gen.safetensors`, `tokenizer.json`, `conds.pt`. Multilingual (as loaded by 0.1.7): `ve.pt`, `t3_mtl23ls_v2.safetensors`, `s3gen.pt`, `grapheme_mtl_merged_expanded_v1.json`, `Cangjie5_TC.json`, `conds.pt`. Turbo: `ve`, `t3_turbo_v1`, `s3gen_meanflow` + GPT-2 tokenizer files. The English repo also holds ~10 GB of other versions; NoorVox downloads only what each loader reads (3.0–3.2 GB per variant). |
| Controls | English/Multilingual: `exaggeration` (emotion), `cfg_weight` (pace), `temperature`. **Turbo ignores exaggeration and cfg** (its code logs "not supported… ignored"). Multilingual: `language_id`. |
| Watermark | Every variant applies Resemble AI's Perth implicit watermark to every output, unconditionally. |

## Measurements (RTX 3080 10 GB, other GPU apps running, models on HDD)

| | English | Multilingual | Turbo |
|---|---|---|---|
| Load (cold) + warm-up | 8–230 s + 22 s | 91 s + 4 s | 131 s + 3 s |
| Warm real-time factor | 1.0–1.15 (≈ real time) | 1.11–1.32 | **0.49–0.55 (≈ 2× real time)** |
| Peak GPU memory allocated | 3.49 GB | 3.49 GB | 3.09 GB |
| Same seed → identical WAV | yes | yes | yes |

- **Emotion** (exaggeration 0.25 → 1.5, George clone): 8.72 s → 7.32 s, so higher emotion is also faster and more animated.
- **Pace** (cfg_weight, default voice, mean of 3 seeds): 0.1 → 8.85 s, 0.3 → 8.44 s, 0.5 → 8.11 s, 1.0 → 7.87 s. Lower is slower, gently
  (≈ 12% end to end). **0.0 crashes** chatterbox-tts 0.1.7 (tensor size mismatch when guidance is off), so NoorVox's range is 0.1–1.0.
- Compared with Qwen 1.7B (faster-qwen3-tts, RTF ≈ 0.39, 4.9 GB): Chatterbox is lighter on memory but not faster, except Turbo.

## Decision

1. Third backend `chatterbox`, implemented on a shared `PythonSidecarBackend` (process, Job Object, JSON lines, load stages, GPU
   numbers). Engines only build their synth requests. `NoorVox.Backends.Qwen` became `NoorVox.Backends.Python`, and both sidecars
   share `engines/common/noorvox_sidecar.py`.
2. Engine pack `chatterbox` from a hash-locked lock (`chatterbox.lock`, 110 packages). Engines now declare their own post-install
   imports (`verifyImports`); Chatterbox's include Perth's watermarker.
3. Catalog models `chatterbox-english`, `chatterbox-multilingual`, `chatterbox-turbo`. Capabilities: cloning (any voice profile:
   clone, designed or saved, from its reference audio; no transcript needed), seed, GPU, watermark `perth`; emotion 0.25–2.0 and pace
   0.1–1.0 on English/Multilingual only; 23 languages on Multilingual. A built-in voice `default` (the model's `conds.pt`).
4. New controls `--emotion` / `--pace` (CLI) and sliders (studio), shown only where the capability exists. Takes and manifests record
   `emotion`, `pace` and `watermark`.
5. **The catalog now overrides installed manifests' capabilities** for models it knows, so corrections (like the pace minimum found
   during this work) reach models that are already installed.

## Found and fixed along the way

- `resemble-perth` imports `pkg_resources`, which setuptools ≥ 81 no longer ships; without it `PerthImplicitWatermarker` is `None` and
  Chatterbox can't even be constructed. The lock pins setuptools 78.1.0, and the engine's install check imports the watermarker.
- The CLI parser didn't know `--no-engine` as a value-less flag (0.3.0), so `--no-engine --json` lost `--json`. A test now checks every
  value-less option in the usage strings against the parser's flag list.
- The engine installer's post-install check was hard-coded to Qwen's modules.

## Consequences

- Another optional ~5.5 GB engine pack plus 3.0–3.2 GB per Chatterbox model, all user-chosen.
- Chatterbox output is always watermarked; NoorVox says so in the UI, the JSON and the take records. It's a feature for provenance,
  and not something NoorVox removes.
