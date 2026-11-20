# Changelog

## 0.1.0-dev — Phase 3: production workflows (2026-11-20)

### Implemented and tested
- Project format (folder of JSON + WAV, documented in PROJECT_FORMAT.md) with six templates: documentary, audiobook, dialogue,
  narration, education, custom. Stable segment ids; per-section script files; speakers and casting; pronunciation replacements.
- Import from Markdown/text (headings → sections/scenes, paragraphs → segments, HTML comments → notes) and dialogue CSV/JSON
  (line ids, characters, sets). Validated before writing; originals kept read-only in references/.
- Takes and approvals: every generation is a new immutable take; approval is a pointer; edits and recasts mark segments stale and never
  touch approved takes; auto-approve fills only empty approvals.
- Resumable batch jobs: persisted before and during execution, one runner per project, crash detection, resume without redoing finished
  segments, soft stop (after current) and hard stop.
- Clip export with id-based names and clips.json.
- CLI: `projects create|import|show|validate|cast`, `generate`, `jobs list|status|resume`, `takes list|approve|unapprove`, `export clips`.
- Studio: Projects page (templates, create, open, recents) and project workspace (tree, autosaving segment editor, status, takes comparison
  and approval, generate scene/section/project, stop/resume, casting, clip export).
- Shared `WorkerLease` (quick narration, CLI and project jobs use the same worker logic).
- Tests: 38 xUnit tests (+9 project tests with a fake engine); `examples/production.ps1` (all three templates with the real model);
  `tools/studio-smoke-projects.mjs` (10/10, including a GUI stop + resume); a real hard-kill of the CLI mid-job resumed correctly.

### Fixed during Phase 3 testing
- A rejected import could leave a reference copy behind; imports of the same file in the same second collided.
- Job results mixed cumulative and per-run counts (now `job` + `thisRun`).

## 0.1.0-dev — Phase 2: NoorVox Studio (2026-11-20)

### Implemented and tested
- `NoorVoxStudio.exe`: WPF + WebView2 shell with a locked-down, local-only React/TypeScript UI and a typed, allow-listed bridge.
- First-run setup: storage choices before any download, then a one-click verified voice install.
- Quick Narration: script editor with draft autosave and text import; voice picker with audition; speed; Generate (Ctrl+Enter);
  session takes with waveform playback and seek, measured durations, Export (WAV + provenance JSON) and Reveal.
- Models & voices: installed models (license, source, sha256, size), voice packs, install/update with progress, verify.
- Runtime monitor: live state (icon + word), PID, model, memory, CPU %, provider, jobs and current work; Start, Unload (recycle),
  Stop, Force stop (confirmed), Stop all. GPU is reported as unavailable, never as fake numbers.
- Settings: storage paths (change/open/reset), six themes, stop-on-exit policy, idle timeout, default voice.
- Quit policy: studio engines stop on exit by default (a Job Object guarantees it even on a crash); warns when keeping them.
- Shared `NarrationService` in Core (CLI `synth` now uses it); `WorkerSupervisor` accepts a host-owned shared Job Object.
- `tools/studio-smoke.mjs`: scripted end-to-end test of the real studio (10 checks); screenshots in docs/screenshots.
- 29 xUnit tests (+2: shared job lifetime, studio dedicated-engine policy).

### Fixed during Phase 2 testing
- A crash on close when no workers were running (`Close()` called inside the Closing event).
- Session takes were lost when switching pages; voice and speed were not remembered.
- The waveform didn't follow theme changes; generated audio didn't autoplay inside WebView2.

## 0.1.0-dev — Phase 1: headless core (2026-11-20)

### Implemented and tested
- Storage: six independently configurable path classes, `NOORVOX_HOME` and portable mode, resolved-path reporting. C: allowed.
- Model catalog with per-file sha256. One-command install with resumable, verified downloads and auto-installed voice packs.
  Voice pack install, side-by-side updates, hash-verified import of local models, full and quick verify.
- Isolated worker process with named-pipe IPC: start, load, synthesize, unload (= recycle), graceful stop, force stop, stop all,
  idle timeout, crash detection, duplicate prevention, Job Object binding (no orphans), PID-reuse protection.
- CLI `noorvox` with JSON envelope and stable exit codes: `version`, `doctor`, `storage show|set`,
  `models list|install|update|import|verify`, `voices list|packs|install`, `workers list|status|start|load|unload|stop`, `synth`.
- Offline Kokoro-82M v1.0 synthesis on CPU (24 kHz mono 16-bit WAV), measured durations, provenance manifest.
- Docs: CLI reference, AI integration cookbook, architecture, ADR-0001, AGENTS.md, a runnable example.

### Planned (not implemented)
- Phase 4: export assembly, SRT/VTT, timing manifests, `generate`/`jobs`/`export` CLI verbs.
- Remote catalog refresh, GPU execution (CUDA), more backends (e.g. Qwen TTS), `models remove`.
