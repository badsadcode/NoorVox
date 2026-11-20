# NoorVox release notes

## 0.2.0 — creative tools

- **Voice blending:** your own narrator from 2–4 Kokoro voices (immutable, hashed, usable everywhere).
- **Pronunciation editor:** with a test button and affected-segment counts.
- **Segment split/merge and keyboard shortcuts** in the project workspace.
- Verified: 62 xUnit tests; projects smoke 16/16 including pronunciation, split, merge and the blend dialog in the real studio; a real
  blend synthesized and verified (`bm_documentarynarrator` = 60 % George + 40 % Fable).
- Everything below (the 0.1.0 acceptance matrix, benchmarks, limitations) still applies. The known limitation "voice mixing not exposed" is resolved.

---

# NoorVox 0.1.0 — release notes

**Bad Ideas Works · 20 November 2026 · Windows 10/11 x64**

NoorVox is a local voiceover studio: documentaries, audiobooks, game dialogue and quick narration, generated offline with Kokoro-82M,
with a headless CLI that AI agents (Codex) can drive end to end. This release completes the five phases of the design spec
(`docs/design/NOORVOX_DESIGN.md`). Everything below says what was **verified**, and how.

## Get it

- `NoorVox-0.1.0-win-x64.zip` (built by `tools\publish.ps1 -Zip`, about 137 MB): extract, then run `Install.ps1` (or run `NoorVoxStudio.exe` directly).
- Self-contained; needs no .NET install. It does need the Microsoft Edge WebView2 Runtime, which is built into Windows 11.
- First run: choose folders (any drive), then install the voice (about 440 MB, once). Fully offline afterwards.

## Verification behind this release

| Suite | What it does | Result |
|---|---|---|
| `dotnet test` (xUnit) | 56 tests: storage, catalog/installer (fake HTTP: resume, corruption), worker lifecycle (real processes: crash, idle, duplicates, Job Object, pipe inheritance), real Kokoro synthesis, projects/takes/jobs (fake engine: interruption, crash journal), assembly (sample-exact timing, resampler quality), contrast, docs contract, schema, performance | 56/56 |
| `tools/studio-smoke.mjs` | Real studio via the WebView2 DevTools protocol: generate, monitor, unload, themes, 150 %/200 % layout, quit, no leftover worker | 16/16 |
| `tools/studio-smoke-projects.mjs` | Real studio, isolated home: open project, autosave, generate, Stop now → Resume, new take + approve, play take, assemble dialog, quit | 12/12 |
| `examples/production.ps1` | Documentary, audiobook and dialogue projects with the real model, headless | pass (49 s) |
| `examples/agent-episode.ps1` | Markdown script → verified 48 kHz episode + SRT/VTT + manifest (the spec's Phase 4 gate) | pass (34 s for 41 s of audio) |
| Hard-kill test | CLI killed mid-job → worker died with it, job reported `interrupted 2/6`, resume → exactly one take per segment | pass |
| Install test | Release zip → `Install.ps1 -Portable` → installed CLI works → `Uninstall.ps1` leaves nothing | pass |
| By hand (Tom) | Quick-narration Export dialog; generation heard from the GUI | pass |

## Acceptance criteria (spec §15)

✅ met · 🟡 partly met (see note) · ❌ not met

### 15.1 Functional

| Criterion | | Evidence / note |
|---|---|---|
| Create/open/rename/duplicate a project in the GUI; reopens after restart | 🟡 | Open, rename and the reopen-after-restart flow are covered by the smoke test; rename and duplicate are covered by unit tests. **Create and Duplicate in the GUI use native folder pickers, which automation can't drive and which no human has clicked yet.** |
| Quick Narration with no terminal, valid WAV | ✅ | studio smoke; Export confirmed by hand |
| Documentary: episode/scene navigation, voice assignment, preview, export plus timing manifest | ✅ | projects smoke; `agent-episode.ps1` |
| Audiobook: import, edit chapters, batch, resume after interruption, export chapter WAVs | ✅ | `production.ps1`, hard-kill test, `export assemble`. EPUB/DOCX import isn't offered (it was optional in the spec). |
| Dialogue: stable line ids, characters/voices, per-line WAVs named by id | ✅ | `production.ps1` (`npc_vendor_greeting_001.wav`) |
| Edit one segment and regenerate without replacing unrelated approved takes | ✅ | ProjectTests, projects smoke |
| CLI performs equivalent operations headlessly with parseable JSON | ✅ | CLI_REFERENCE (contract test), examples |
| Exported manifest refers to existing audio with measured durations | ✅ | AssemblyTests (schema check), `agent-episode.ps1` verification |

### 15.2 Model and process

| Criterion | | Evidence / note |
|---|---|---|
| Installed / loaded / generating / stopped are distinguished | ✅ | Monitor and `workers list`: idle, loading, ready, generating, stopping, error, unresponsive |
| Start, Load, Unload, Stop, Stop All, Force Stop behave as documented | ✅ | WorkerLifecycleTests, studio smoke (unload: 1 GB → 46 MB) |
| Cancelling an active job doesn't corrupt the project or approve incomplete takes | ✅ | ProjectTests, journal crash test, GUI Stop now |
| A crashed worker becomes `error`/`unresponsive` and the app stays usable | ✅ | WorkerLifecycleTests |
| Closing follows an explicit worker policy; no surprise workers left | ✅ | Job Object + quit policy; smoke checks the worker PID is gone (CPU only, so no VRAM involved) |
| Two clients can't start duplicate ownership of one worker | ✅ | lock file + worker mutex; duplicate-start test |
| RAM/CPU reporting works; GPU stats truthful or explicitly unavailable | ✅ | Monitor; GPU always shows "unavailable" |

### 15.3 Storage

| Criterion | | Evidence / note |
|---|---|---|
| Installation on C: allowed | ✅ | no drive is ever refused; installer default is `%LOCALAPPDATA%\Programs\NoorVox` |
| Installation/storage on a non-C: drive supported | ✅ | the development setup runs entirely from E: |
| Models, caches, project data, temp audio and exports all on chosen non-C: paths | ✅ | Storage Audit of the development setup |
| Downloads show the destination and don't silently land in the Windows profile | ✅ | first-run and Models page show the target folder before installing |
| Storage Audit shows configured and resolved paths, including WebView2 data | ✅ | `storage audit`, Settings panel; also finds stray copies |
| Works offline after provisioning | 🟡 | By design: synthesis loads by path and makes no network calls (ADR-0001, code review). **Not tested with the network adapter disabled.** |

### 15.4 Quality

| Criterion | | Evidence / note |
|---|---|---|
| Reasonable high-DPI at 100/150/200 % | 🟡 | Per-monitor-v2 DPI manifest; no horizontal overflow at 150 %/200 % effective widths (smoke); icon-only sidebar at narrow widths. **Not checked on physical high-DPI monitors.** |
| No UI freeze during model load, long generation or slow disk | 🟡 | All bridge work runs off the UI thread; generation runs in the worker process. **No dedicated slow-disk stress test.** |
| Keyboard navigation, autosave, take comparison, progress controls in scripted smoke tests | 🟡 | Autosave, takes, approval, stop/resume and progress are scripted. **Keyboard-only navigation is checked by hand, not scripted.** |
| Generated clip is audible, valid PCM, sample count matches duration | ✅ | SynthesisTests (signal peak), WavInfo checks everywhere |
| Documentation examples run in CI or repeatable local tests | 🟡 | All examples run locally and are repeatable. **There's no CI pipeline**, because the real-model tests need the 400 MB model. |

## Benchmarks (development PC, CPU inference)

| Measurement | Result |
|---|---|
| Model download + verify (391 MB + 66 MB voices) | 13 s |
| Model load | ~4 s cold, ~1 s warm |
| Synthesis speed | ~6× real time on long passages (RTF ≈ 0.17); GUI: 15.4 s of narration in 3.5 s |
| First GUI generation including engine start | 5–6 s |
| Worker memory | ~1.0 GB loaded → ~46 MB after Unload |
| Episode script → finished 48 kHz episode (41 s audio, 5 segments), headless | 34 s |
| Large project, 2,000 paragraphs / 20 chapters (NoorVox overhead, instant fake engine) | import 0.24 s · overview 0.45 s · batch job **25.8 s** (was 35.1 s) · re-run with all takes reused **1.0 s** (was 10.9 s) · edit a segment < 0.01 s · assemble a 100-paragraph chapter at 48 kHz 1.2 s |

The batch overhead (about 13 ms per segment, mostly creating the take files) is under 1 % of real synthesis time (2–4 s per paragraph) and grows linearly.

## Known limitations

- **CPU only.** DirectML fails on Kokoro's model graph; CUDA is untested. GPU is shown as unavailable.
- **One backend** (Kokoro-82M v1.0, 54 voices). The catalog is embedded in the app; refreshing it online and checking daily for updates (planned in ADR-0001) aren't implemented, so `models update` only knows models from the app's own release.
- **No mid-sentence cancel** (a KokoroSharp limitation): "Stop now" ends the worker; "Stop after this one" waits for the segment.
- **Not bit-reproducible**: re-generating identical text can differ by a few samples. Takes are matched by inputs.
- **Export is WAV only**, with peak normalisation (no LUFS). Subtitles are per segment; there's no word-level alignment.
- **Pronunciation** is plain text replacement; IPA/phoneme entries aren't exposed.
- **Editing**: no segment split/merge or drag reordering; no undo history beyond autosaved text; no command palette.
- Very large projects (10,000+ segments) load the full overview at once. That's fine at 2,000; it isn't virtualised.
- The installer is a per-user PowerShell script: no MSI, no code signing, no Add/Remove Programs entry.
- Packaged apps (MSIX) may virtualise `%APPDATA%`; use portable mode if NoorVox is launched from several places (see STORAGE_AND_PRIVACY.md).

## Licenses

| Component | License |
|---|---|
| NoorVox code | to be chosen by Bad Ideas Works (the repository has no license file yet) |
| Kokoro-82M model and voices | Apache-2.0 (hexgrad) |
| KokoroSharp | MIT · MisakiSharp: Apache-2.0 |
| ONNX Runtime | MIT |
| Microsoft.Web.WebView2 SDK | Microsoft BSD-style license; the Edge WebView2 Runtime is part of Windows |
| React, Vite | MIT |
| .NET runtime (self-contained) | MIT |
