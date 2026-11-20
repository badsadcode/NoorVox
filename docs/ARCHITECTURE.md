# NoorVox architecture (as implemented, v0.1.0-dev / Phases 1–2)

Decisions and measurements: [ADR-0001](adr/0001-kokoro-backend-and-worker-model.md). Target design: [design/NOORVOX_DESIGN.md](design/NOORVOX_DESIGN.md).

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
                    │   │  └ ITtsBackend       │  NoorVox.Backends.Kokoro → KokoroSharp → ONNX Runtime (CPU)
                    │   └──────────────────────┘
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
| Resources | Working set, private bytes and CPU time are self-reported, with `sampledAt` and `source`. GPU is always reported as unavailable (CPU provider); no fake zeroes. |

### Single broker vs. this design

The spec suggested one local broker process. Phase 1 instead treats the **registry + lock + worker mutex** as the shared source of truth,
which every NoorVox process (CLI invocations, later the GUI) consults. This works across short-lived CLI calls without a resident daemon.
If the GUI needs push events (live progress), it can host the supervisor in-process and still interoperate via the same registry.

## Synthesis path (`noorvox synth`)

text → validate (voice installed, speed 0.5–2.0, `--out` not existing) → choose a worker (reuse warm `default`, or a bound one-shot) →
worker writes `<temp>\synth\<guid>.wav` → CLI re-reads it as PCM WAV (`WavInfo`) → atomic move to `--out` → JSON with measured duration
and provenance (text sha256, voice, speed, model sha256, backend, worker, timings). A one-shot worker is stopped afterwards.

## Known limitations (Phase 1)

- CPU only. DirectML fails on Kokoro's ConvTranspose; CUDA is untested (needs CUDA 12 + cuDNN 9).
- Synthesis can't be cancelled mid-job (a KokoroSharp limitation): cancel = force-stop or recycle the worker.
- Output is not bit-reproducible run to run, so take identity uses inputs, not audio hashes.
- No projects, batch jobs, subtitles or export assembly yet (Phases 3–4).
- The studio's Export uses a native Save dialog, which the smoke test can't automate. It was verified by hand on 2026-11-20 (WAV + `.noorvox.json` written to Exports); re-check it manually after changes.
- The catalog is embedded; remote catalog refresh (for new models/voice packs without an app update) is planned.
- Future backends (e.g. Qwen TTS) plug in behind `ITtsBackend` in their own worker; they will likely need a GPU and a Python sidecar.
