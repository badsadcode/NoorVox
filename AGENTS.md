# AGENTS.md — for coding assistants working on NoorVox

- **Using NoorVox to make audio?** Read `docs/AI_INTEGRATION.md` (cookbook) and `docs/CLI_REFERENCE.md` (exact contract). Don't automate a UI.
- **Changing NoorVox?** Read `docs/ARCHITECTURE.md` and `docs/adr/` first. The product spec is `docs/design/NOORVOX_DESIGN.md`. The mockup is guidance, not a goal.

## Build and test

```powershell
dotnet build NoorVox.sln
$env:NOORVOX_TEST_MODELS_DIR = "E:\AI\NoorVox\Models"   # optional: enables real-synthesis tests (needs kokoro-82m installed there)
dotnet test tests\NoorVox.Tests
```

Studio UI: `npm run dev` in `src/NoorVox.Studio.Web` (browser + mock bridge). Real end-to-end check (opens a window, needs an installed model):

```powershell
node tools\studio-smoke.mjs src\NoorVox.Studio\bin\Debug\net10.0-windows\win-x64\NoorVoxStudio.exe docs\screenshots
```

Projects end to end in the studio (isolated home, real models, opens a window):

```powershell
node tools\studio-smoke-projects.mjs <NoorVoxStudio.exe> <noorvox.exe> E:\AI\NoorVox\Models <new-scratch-dir> docs\screenshots
```

All three templates headless with the real model: `.\examples\production.ps1 -Noorvox <noorvox.exe> -WorkDir <new-dir>`.

Tests use isolated temp homes (`NOORVOX_HOME`) and never touch the user's settings or workers.

## Rules

1. **Never kill processes by name.** Stop workers with `noorvox workers stop` / `WorkerSupervisor`, which only touch recorded, NoorVox-launched PIDs.
2. **Don't add fake controls.** Expose only what `TtsCapabilities` says the backend supports (Kokoro: voice and speed).
3. **No inference code outside the worker process.** Core stays backend-agnostic.
4. **Measured, never estimated:** durations come from WAV samples; resource numbers carry `source` + `sampledAt`; unknown = "unavailable".
5. **C: is allowed.** Never block a path because of its drive; always show resolved paths.
6. **Never overwrite** user audio or approved takes without an explicit flag.
7. **Interface changes:** update `CommandTable.cs`, `docs/CLI_REFERENCE.md` (a test enforces coverage), `docs/AI_INTEGRATION.md` and examples, and bump
   `NoorVoxInfo.SchemaVersion` for breaking JSON changes.
8. **Catalog changes:** regenerate `src/NoorVox.Core/Models/catalog.json` with `tools/gen_catalog.py`. Every file needs a sha256 and a license.
9. Model weights, voices and generated audio are never committed (`.gitignore`).
10. **Studio bridge:** every new UI capability is an explicit method in `StudioApi` with validated parameters. Never expose raw paths,
    process launching or arbitrary URLs to JavaScript; keep disk and process work in C#. Keep `src/NoorVox.Studio.Web/src/types.ts` in sync.
11. **Projects:** take files are immutable and never deleted by code; approval is only a pointer; auto-approve never replaces an approval;
    every write goes through `ProjectStore` (atomic + locked). Keep `docs/PROJECT_FORMAT.md` in sync and bump `schemaVersion` for breaking changes.
