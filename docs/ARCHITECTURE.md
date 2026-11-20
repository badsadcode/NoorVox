# NoorVox architecture (as implemented, 0.3.0)

Decisions and measurements: [ADR-0001](adr/0001-kokoro-backend-and-worker-model.md), [ADR-0002](adr/0002-qwen3-tts-and-gpu.md) (Qwen3-TTS, GPU). Target design: [design/NOORVOX_DESIGN.md](design/NOORVOX_DESIGN.md).

```text
 noorvox.exe (CLI) ──────────┐      ┌──── NoorVoxStudio.exe (WPF) ── WebView2 ── React UI (https://app.noorvox.local)
                             ▼      ▼                       typed JSON bridge: StudioApi (allow-listed methods)
                ┌──────────────────────┐
                │    NoorVox.Core      │  storage · catalog · installer · registry · WAV · IPC client · supervisor
                └───┬──────────┬───────┘
                    │          │ CreateProcess (no inherited handles) + optional Job Object
         files on   │          ▼
         disk only  │   ┌──────────────────────┐  named pipe (current user only) + per-launch token
                    │   │  NoorVox.Worker.exe  │◄──────────────────────────────────────────────────
                    │   │  WorkerHost          │
                    │   │  └ ITtsBackend       │  NoorVox.Backends.Kokoro → KokoroSharp → ONNX Runtime (CPU, or CUDA from an engine pack)
                    │   │    chosen per model    │  NoorVox.Backends.Qwen ─ stdin/stdout JSON ─► python.exe noorvox_qwen.py
                    │   └──────────────────────┘     (engine pack Python, own kill-on-close Job Object) → PyTorch CUDA
                    ▼
   <home>\settings.json · <home>\runtime\workers\<id>.json · <models>\<id>\<ver>\… · <temp>\logs\worker-<id>.log
```

## Projects

| Project | Role | May reference |
|---|---|---|
| `src/NoorVox.Core` | Backend-agnostic services and contracts | nothing NoorVox-specific |
| `src/NoorVox.Backends.Kokoro` | `KokoroBackend : ITtsBackend` | Core, KokoroSharp, ONNX Runtime |
| `src/NoorVox.Worker` | Worker process (`NoorVox.Worker.exe`) | Core, backends |
| `src/NoorVox.Cli` | `noorvox.exe` | Core (references the Worker project only so the exe is copied next to it) |
| `src/NoorVox.Studio` | `NoorVoxStudio.exe`: WPF shell, WebView2 host, bridge, quit policy | Core, WebView2 (Worker referenced for copying only) |
| `src/NoorVox.Studio.Web` | React 19 + TypeScript + Vite UI, built into `NoorVox.Studio/wwwroot` | — (talks only to the bridge) |
| `tests/NoorVox.Tests` | xUnit v3; real processes, fake HTTP server | Core, Cli |
| `spikes/KokoroSpike` | Phase 0 throwaway measurement tool | — |

Rule: **inference libraries are only ever loaded in the worker process.** Neither the CLI nor the studio loads ONNX Runtime.
Text → WAV logic lives once in `Core/Narration/NarrationService` and is used by both `noorvox synth` and the studio.

## Studio (`NoorVox.Studio`, `NoorVox.Studio.Web`)

| Concern | Implementation |
|---|---|
| Hosting | WebView2 with the user-data folder set to the configured `webview` storage path **before** creation. The UI is served from the virtual host `app.noorvox.local` (bundled files, no server, no CDN); generated previews from `media.noorvox.local` → `<temp>\studio\previews`. A CSP limits scripts, media and connections to those origins. |
| Lock-down | Navigation outside the app origin is cancelled, new windows are blocked, permissions are denied, and devtools, context menus and accelerators are off unless debugging (`NOORVOX_DEVTOOLS=1`). Autoplay is allowed so generated audio can play straight away. |
| Bridge | UI → `{id, method, params}` via `chrome.webview.postMessage`; host → `{id, ok, result|error}` or `{event, data}`. Only messages from the app origin are accepted. `StudioApi` is an explicit allow-list (app/storage/models/voices/workers/narration/settings/shell). Paths written come only from settings or native dialogs; revealed files must sit inside NoorVox storage; URLs must be https to model-source hosts. |
| Events | `install.progress`, `narration.progress`, `app.closing`. Worker state is polled every 2 s (`workers.list`); CPU % is computed in the UI from two cumulative CPU-time samples. Session takes live in app state, so they survive page switches. |
| Engine policy | One dedicated worker `studio`. It starts on first Generate (or from the Monitor) and stays warm. When *stop on exit* is on (default) it is bound to a Job Object owned by the main window for its whole lifetime, so even a crash of the studio ends it. Idle timeout comes from settings. |
| Quit | `OnClosing` lists studio-launched workers. With stop-on-exit it stops them gracefully (with a forced fallback); otherwise it warns (keep / stop / cancel). The final `Close()` is posted to the dispatcher, and the job handle is disposed last. |
| Single instance | Named mutex `Local\NoorVox.Studio`. |
| Screenshots / smoke | `NoorVoxStudio.exe --page <p> --screenshot <png> [--screenshot-delay ms] [--exit-after-screenshot]`. `tools/studio-smoke.mjs` drives the real studio over the WebView2 DevTools protocol (localhost, test runs only): generate, monitor, unload, themes, quit, no leftover worker. |
| Dev loop | `npm run dev` in `src/NoorVox.Studio.Web` runs the UI in a browser against `mock.ts` (clearly marked mock data). |

## Projects (`Core/Projects`) — Phase 3

| Piece | Responsibility |
|---|---|
| `ProjectModel` | File DTOs (`ProjectFile`, `SectionFile`, `Segment`, `TakeRecord`), project kinds (templates), segment statuses. Format: [PROJECT_FORMAT.md](PROJECT_FORMAT.md). |
| `ProjectStore` | Create/open, atomic writes under a cross-process `write.lock`, stable id generation, add/remove/update segments, effective voice/speed, pronunciation, cacheKey, takes, status, approval (a pointer; take files are immutable). |
| `ScriptImporter` | Markdown/text and dialogue CSV/JSON → sections/scenes/segments. Validates everything before writing; keeps the original read-only in `references/`. |
| `JobRunner` | Persisted batch jobs: the file is written before the run and after every item; `job.lock` makes one runner per project; stale `running` jobs become `interrupted`; resume re-queues unfinished items. Synthesis is a delegate (`SegmentSynthesizer`), so tests use a fake engine. |
| `ProjectGeneration` | Runs a job on a real worker via `WorkerLease` (the same lease logic as `synth` and the studio). |
| `ProjectOverview` | Snapshot with per-segment status and counts, plus `Validate` (used by `projects show/validate` and the studio workspace). |
| `ClipExporter` | Approved takes → id-named WAVs + `clips.json`; refuses missing approvals unless allowed; never overwrites silently. |

A take is committed in order: synthesize into `.noorvox/tmp`, validate as PCM WAV, move to `takes/<seg>/<take>.wav`, then atomically write
`<take>.json` (the commit marker), then update the job file. A crash at any point leaves either a complete take or nothing that counts.

Studio: `ProjectsApi` adds `projects.*` bridge methods. Project paths are accepted only if they are in `studio.recentProjects`, which only native
dialogs and Create extend. Jobs run in the background (one per project) and push `job.progress` / `job.status` / `job.finished` events. Takes are
played from the virtual host `takes.noorvox.local`, mapped to the open project's `takes` folder. Closing the studio cancels running jobs, which
become `interrupted` and resumable. `tools/studio-smoke-projects.mjs` exercises the workspace end to end in an isolated home.

## Export (`Core/Export`, `Core/Audio/Pcm.cs`) — Phase 4

| Piece | Responsibility |
|---|---|
| `PcmBuffer` | Mono 16-bit WAV ⇄ float samples (export processing only). |
| `Resampler` | Windowed-sinc (Blackman, 32 zero-crossings, cutoff = min(1, ratio), DC-normalised kernel), parallel per output sample. Tested: exact lengths, 1 kHz stays 1 kHz, level within 1 %, a 15 kHz tone removed when going 48 → 24 kHz. |
| `Assembler` | Plans the timeline at the takes' native rate (edge / scene / segment gaps, per-segment overrides), copies samples, then optionally resamples and peak-normalises the whole buffer. Writes WAV via temp + move, SRT/VTT, and the manifest (sha256 of the WAV, processing record). Refuses mixed sample rates, missing approvals (unless allowed) and overwriting (unless allowed). |
| `Subtitles` | Segment-level SRT/VTT; wraps long text, never re-times it; VTT escapes `<`, `>`, `&` and uses `<v Speaker>` for dialogue. |

Studio: `projects.assemble` (checks approvals before the folder dialog, so the user isn't sent through a picker for nothing) and
`projects.revealOutput`, which only opens folders containing a NoorVox manifest or `clips.json`.

## Delivery (Phase 5)

| Piece | Responsibility |
|---|---|
| `StorageAudit` | Read-only audit of every location plus stray-data detection in default locations (`%LOCALAPPDATA%\NoorVox`, `%APPDATA%\NoorVox`, `Documents\NoorVox`, `<exe>\data`). |
| `SupportBundle` | Zip of environment, reduced settings, audit, models, workers and log tails. Explicitly excludes project content. |
| Job journal | `<jobId>.journal`: one JSON line per finished item between snapshots (every 50 items or 5 s); `Load` replays it over the snapshot and ignores a torn last line. |
| `tools/installer` | `Install.ps1` (per user, copies, shortcuts, optional `noorvox.portable`) and `Uninstall.ps1` (removes the program and shortcuts, keeps data). |

## Storage (`Core/Storage`)

`StorageContext.Load()` resolves the home: `NOORVOX_HOME` → portable marker (`noorvox.portable` next to the exe → `.\data`) → `%APPDATA%\NoorVox`.
Six independent path classes (models, cache, temp, projects, exports, webview) each come from `settings.json`, or a default for the home mode.
C: is allowed; nothing hard-codes or forbids a drive. Reading settings never creates folders; `storage set` validates by writing a probe file.

## Models and voices (`Core/Models`)

- **Catalog** (`catalog.json`, embedded, generated by `tools/gen_catalog.py`): `models[]` and `voicePacks[]`, each with download URLs, sizes,
  sha256 for every download and every installed file, license and source. A voice pack targets `modelId` + `modelVersions`.
  A model lists `defaultVoicePacks`, which are installed automatically.
- **Installed layout:** `<models>\<id>\<version>\{kokoro.onnx, voices\*.npy, noorvox.model.json, voicepacks\<packId>.json}`.
  Manifests are written **last**, so a manifest's presence means the install is complete.
- **ModelInstaller:** download into `<cache>\staging\<id>-<ver>\*.part` (HTTP Range resume, 60 s stall timeout) → size + sha256 check →
  unpack only the files the catalog lists (which blocks zip-slip and junk) → sha256 every file → move into place → manifest → delete staging.
  Import = hash local `*.onnx` and look it up in the catalog. Unknown weights are refused.
- **Updates** install side by side. Old versions are kept because takes pin `modelSha256`.

## Worker supervision (`Core/Workers`, `Core/Ipc`, `NoorVox.Worker`)

| Concern | Implementation |
|---|---|
| Launch | `NativeProcess.Start`: `CreateProcessW` with `bInheritHandles = FALSE`, `CREATE_NO_WINDOW`, own process group, token in the environment block. (`Process.Start` would leak the caller's stdout pipe into a long-lived worker, so agents capturing output would hang. A regression test covers this.) |
| Lifetime binding | `boundToLauncher` workers go into a `JobObject` with `KILL_ON_JOB_CLOSE`: if the GUI or CLI dies, Windows kills them. Persistent workers try `CREATE_BREAKAWAY_FROM_JOB` so they can outlive a sandboxed caller, and fall back silently. |
| Ownership | `runtime\workers\<id>.json` = PID + exact process start time + pipe + token + launcher identity. Status checks re-verify PID, start time and image name, so a recycled PID is never attached to. Only recorded processes are ever stopped. |
| No duplicates | Async-safe cross-process lock file per id during start/stop, **plus** a named mutex held by the worker itself (`Local\NoorVox.Worker.<id>`), so the OS refuses a second live worker even if two supervisors race. |
| IPC | One request per named-pipe connection (`PipeOptions.CurrentUserOnly`), newline-delimited JSON, token compared in constant time. Methods: `status`, `load`, `synthesize`, `shutdown`. Status is answered while synthesis runs. |
| States | Worker-reported: `idle`, `loading`, `ready`, `generating`, `stopping`. Supervisor-derived: `stopped` (no record), `starting`, `unresponsive` (alive, no answer), `error` (record but process gone = crash). |
| Unload | Recycle the process (ADR-0001: in-process dispose kept about 370 MB resident). |
| Stop | Graceful `shutdown` (drains the current job), bounded wait, then error `worker_stop_timeout`. Force = kill process tree. The worker deletes its own record on clean exit, only if the PID matches. |
| Idle timeout | The worker exits itself after N idle seconds (persistent default 900 s). Status pings don't count as activity. |
| Resources | Working set, private bytes and CPU time are self-reported, with `sampledAt` and `source`. Qwen workers add `gpuMemory` (from `torch.cuda`, sampled after each load/synthesis) and `sidecar` (the Python engine's PID and memory, measured live). Kokoro reports GPU as unavailable (ONNX Runtime has no memory API); no fake zeroes. |
| Backend choice | A worker starts without a backend; `load` creates `KokoroBackend` or `QwenBackend` from the model manifest's `backend`. The supervisor passes paths the backend needs (`BackendLoadOptions`: engine folder, sidecar script, cache, Kokoro provider, CUDA DLL folders), so the worker never reads settings. |
| Python sidecar | `QwenBackend` starts the engine's `python.exe -I -u noorvox_qwen.py` with redirected stdio, assigns it to a worker-owned Job Object (kill on close), and closes stdin to stop it (the script exits on EOF). Library output goes to stderr, copied into the worker log; stdout carries only protocol lines. |

### Single broker vs. this design

The spec suggested one local broker process. Phase 1 instead treats the **registry + lock + worker mutex** as the shared source of truth,
which every NoorVox process (CLI invocations, later the GUI) consults. This works across short-lived CLI calls without a resident daemon.
If the GUI needs push events (live progress), it can host the supervisor in-process and still interoperate via the same registry.

## Synthesis path (`noorvox synth`)

text → validate (voice installed, speed 0.5–2.0, `--out` not existing) → choose a worker (reuse warm `default`, or a bound one-shot) →
worker writes `<temp>\synth\<guid>.wav` → CLI re-reads it as PCM WAV (`WavInfo`) → atomic move to `--out` → JSON with measured duration
and provenance (text sha256, voice, speed, model sha256, backend, worker, timings). A one-shot worker is stopped afterwards.

## Known limitations (Phase 1)

- Kokoro runs on CPU by default; CUDA is opt-in via engine packs (ADR-0002). DirectML fails on Kokoro's ConvTranspose.
- Qwen needs an NVIDIA GPU; there is no CPU fallback (it would be far slower than real time).
- Synthesis can't be cancelled mid-job (a KokoroSharp limitation): cancel = force-stop or recycle the worker.
- Kokoro output is not bit-reproducible run to run, so take identity uses inputs, not audio hashes. Qwen output is reproducible with a seed (recorded per take).
- Assembly output is WAV only (no MP3/AAC) and normalisation is peak-based, not LUFS. Segment split/merge and drag reordering aren't implemented.
- Very large projects (thousands of segments) load the whole overview at once; list virtualisation is future work.
- The studio's Export uses a native Save dialog, which the smoke test can't automate. It was verified by hand on 2026-11-20 (WAV + `.noorvox.json` written to Exports); re-check it manually after changes.
- The catalog is embedded; remote catalog refresh (for new models/voice packs without an app update) is planned.
- Future backends (e.g. Qwen TTS) plug in behind `ITtsBackend` in their own worker; they will likely need a GPU and a Python sidecar.
