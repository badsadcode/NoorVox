# ADR-0001 — First TTS backend, worker process model, storage & model acquisition

**Status:** Accepted (Phase 0) · **Date:** 2026-11-20 · **Author:** Sage · **Reviewer:** Tom

## Context

The design spec (`docs/design/NOORVOX_DESIGN.md`) asks Phase 0 to decide the Kokoro adapter, the
worker/process model and storage initialisation, *based on measurements rather than assumptions*, and to produce a real
offline WAV from a model stored on a user-chosen non-C: drive. Tom also added a requirement (2026-11-20):
**end users must not have to think about downloading models**. Model install and upgrades are an easy,
built-in, automatic part of the app. Manual import stays available but optional.

## Evidence (measured on Tom's machine)

Machine: Windows 10 Pro 19045, RTX 3080 10 GB (driver 621.01), .NET SDK 10.0.404, no CUDA toolkit installed.
Spike: `spikes/KokoroSpike` (throwaway; reproducible commands below).

| Item | Result |
|---|---|
| Library | **KokoroSharp 0.8.4** (MIT, active: last push 2026-08), ONNX Runtime 1.22, phonemizer **MisakiSharp** (Apache-2.0). espeak-ng is **not spawned** for any language (KokoroSharp replays a measured espeak dump), so no GPL binary is involved. |
| Model | `kokoro.onnx` fp32, 325,508,342 bytes, sha256 `0cfd5e79…bb05`, from `github.com/Lyrcaxis/KokoroSharpBinaries` release v2.0.0. Kokoro-82M weights and voices: **Apache-2.0** (hexgrad/Kokoro-82M). |
| Storage | Model at `E:\AI\NoorVox\Models\kokoro-82m-v1.0\`, voices (54 v1.0 `.npy` voices + 103 v1.1-zh voices = 157 loaded; 79 MB) in `…\voices\`, output in `E:\AI\NoorVox\Temp\spike\`. Downloaded through `E:\AI\NoorVox\Cache\staging` → hash verified → moved. `LoadModel(path)` performs no network access. |
| Output | Valid PCM WAV, **24,000 Hz, mono, 16-bit**. Duration measured from the data chunk. |
| CPU (KokoroSharp.CPU) | load 2.5 s; a 20.4 s passage synthesised in 5.6 s cold, **3.4–3.5 s warm (RTF ≈ 0.17, about 6× real time)**. Working set 24 MB → 600 MB after load → about 1.09 GB peak during synthesis. |
| Unload in-process | After `Dispose()` + full GC the working set stays at **393 MB** (private 361 MB) vs a 24 MB baseline. **In-process unload does not reclaim memory.** |
| Determinism | Same text/voice/speed gave sample counts 489,237 / 489,272 / 489,237. **Output is not bit-reproducible.** |
| DirectML (KokoroSharp.DirectML 0.8.4) | ① NuGet doesn't copy `DirectML.dll` 1.15.4 to output for AnyCPU builds, so the outdated 2022 `System32\DirectML.dll` loads → `DXGI_ERROR_UNSUPPORTED`. ② With the correct DLL, the session loads (9.5 s) but synthesis fails: `ConvTranspose '/N.1/pool/ConvTranspose' … E_INVALIDARG`, also with memory pattern off and sequential execution. **DirectML is not usable with this model today.** |
| CUDA | Not tested: needs CUDA 12 + cuDNN 9 on PATH (or about 1 GB+ of redistributable DLLs). |

Reproduce:

```powershell
cd spikes\KokoroSpike
dotnet build -c Release -p:Ort=Cpu
.\bin\Cpu\Release\net10.0\KokoroSpike.exe --model E:\AI\NoorVox\Models\kokoro-82m-v1.0\kokoro.onnx `
  --voices E:\AI\NoorVox\Models\kokoro-82m-v1.0\voices --out E:\AI\NoorVox\Temp\spike --runs 3
```

## Decisions

1. **Backend:** `KokoroBackend` adapter over **KokoroSharp** (pinned 0.8.4), referencing the base `KokoroSharp`
   package plus an explicitly chosen ONNX Runtime package. Hidden behind `ITtsBackend`, so KokoroSharp types never leak
   into Core. Use `KokoroWavSynthesizer` / `SynthesizeWithTimestampsAsync` (no playback code in the worker).
2. **Execution provider:** **CPU is the v1 default and the supported baseline**. At about 6× real time it is good enough
   for production. GPU is an optional, separately-tested later provider (CUDA first; DirectML re-evaluated with newer
   ORT/model exports). The UI shows the provider actually in use and never claims GPU when running on CPU.
3. **Process model:** **one isolated worker process per backend** (`NoorVox.Worker.exe`), supervised by the core and
   placed in a **Windows Job Object** (`KILL_ON_JOB_CLOSE`), so a dying supervisor can't leave orphans.
   **"Unload" = recycle the worker process**, because in-process unload leaves about 370 MB resident (measured). The UI labels it
   accordingly ("Unload — restarts worker").
4. **IPC:** named pipe, current-user ACL, a random per-launch token passed to the worker at spawn, newline-delimited
   JSON request/response/event messages with request IDs. Heartbeat every 2 s; `unresponsive` after 3 missed beats.
5. **Single broker:** the first NoorVox process (GUI or CLI) to create a named mutex plus a broker pipe owns the
   supervisor. Other clients talk to the broker, or the CLI briefly becomes the broker when none is running. An
   ownership registry (`<data>/runtime/workers.json`: PID + process start time + token hash) prevents
   attaching to recycled PIDs. This prevents duplicate workers.
6. **Audio:** keep native 24 kHz mono 16-bit for takes. Resampling (e.g. 48 kHz) happens only at export. Duration is always
   measured from samples.
7. **Cache/take identity:** because output is not bit-reproducible, takes are identified by **input hash** (normalised
   spoken text + voice + speed + model hash + backend version), never by audio hash. Regenerating produces a new take.
8. **Voices** are model-package assets stored next to the model (`<models>/kokoro-82m-v1.0/voices`), not copied into
   the app folder. The NuGet-bundled voice copy is excluded from shipped builds.
9. **Storage initialisation:** `settings.json` stores independent roots (models, cache, temp, projects, exports, webview).
   Config location: `%APPDATA%\NoorVox\settings.json` by default, overridable by `NOORVOX_HOME` or a portable `noorvox.portable`
   marker next to the exe (then everything lives beside the app). First run asks; defaults are standard Windows
   locations; **C: is allowed**. ORT/KokoroSharp write nothing else to disk (no download happens when loading by path).
10. **Model acquisition (Tom's requirement):** a signed-off **model catalog** (`catalog.json`, shipped with the app and
    optionally refreshed) lists packages: id, version, files[url, size, sha256], license, languages and voice count.
    - First-run "Voice setup" defaults to **one click: "Install recommended voice (Kokoro 82M, about 400 MB)"**.
      The size and destination are shown. Optionally it starts automatically if the user ticks "set it up for me".
    - Downloads stream to `<cache>/staging/*.part` with resume (HTTP Range), verify sha256, then atomically move into
      `<models>/<id>/`. A failed hash is deleted and reported. It never activates silently.
    - **Updates:** checked at most daily (setting, on by default), when a newer catalog version exists.
      The app shows "Update available" with **one-click Update**. The new version installs side by side.
      Existing projects keep pinning the model hash used for their takes, and the old version is removed only on user
      confirmation. No silent re-download at every startup.
    - CLI parity: `noorvox models install <id> [--version latest] --json`, `noorvox models update --json`.
    - Manual `models import <folder>` remains for offline/air-gapped users.
    - Source of truth for v1: KokoroSharpBinaries v2.0.0 (fp32 + fp16) plus voices from the KokoroSharp package/HF.
      Voice files are hashed individually in the catalog.

## Addendum — Phase 1 implementation (2026-11-20)

- **Voice packs (Tom's request):** the catalog separates `models` from `voicePacks`. A model lists `defaultVoicePacks`, which install
  automatically, and further packs install with `noorvox voices install <pack>` (later a button in the studio). The Kokoro v1.0 standard pack
  (54 voices) comes from `voices.zip` of KokoroSharpBinaries v2.0.0. The `voices-zh` set in that archive belongs to the separate
  Kokoro v1.1-zh model and is deliberately **not** listed for v1.0.
- **Future backends:** a **Qwen TTS** backend is wanted "maybe later". It will be another `ITtsBackend` in its own worker process,
  probably needing a GPU and possibly a Python sidecar. Nothing in Core assumes Kokoro, except the metadata-only `KokoroVoiceNames`.
- **Broker:** instead of a resident broker process, ownership lives in the registry (PID + start time) plus a per-id lock file plus a
  worker-held named mutex. See ARCHITECTURE.md. It works for short-lived CLI calls and prevents duplicates even under races.
- **Launch:** `Process.Start` was replaced by `CreateProcessW` without handle inheritance. Otherwise a persistent worker keeps the
  caller's stdout pipe open, and agents capturing `noorvox workers start --json` would hang.
- Measured on the real install: download 391 MB in about 13 s; model load about 4 s; warm synthesis RTF about 0.17–0.45
  (short sentences have higher overhead); one-shot `synth` of one sentence about 4.7 s end to end, including worker start and stop.

## Consequences

- An extra worker executable and an IPC layer, but clean, verifiable unload/stop and crash isolation.
- Synthesis capabilities shown in the UI: **voice, speed, language (from voice), voice mixing**. *No pitch, volume-in-model,
  emotion or style control*: Kokoro doesn't expose these, so the mockup's Pitch and Style/Tone controls are dropped.
  Volume and loudness become export-time processing, labelled as such.
- Phoneme timestamps exist (`SynthesizeWithTimestampsAsync`). They are possibly useful for future captions, but must be reported
  as backend-provided timestamps, not alignment.
- Open: CUDA provider packaging (size and licence of redistributables), fp16 quality/speed on CPU, DirectML
  revisit, and whether voice files should be fetched from HF rather than taken from the NuGet package.
