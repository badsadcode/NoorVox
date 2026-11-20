# Changelog

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
- Phase 2: NoorVox Studio (WPF + WebView2 + React): Quick Narration, model/worker monitor, storage setup, themes.
- Phase 3: projects (documentary/audiobook/dialogue), takes and approvals, batch jobs with resume.
- Phase 4: export assembly, SRT/VTT, timing manifests, `generate`/`jobs`/`export` CLI verbs.
- Remote catalog refresh, GPU execution (CUDA), more backends (e.g. Qwen TTS), `models remove`.
