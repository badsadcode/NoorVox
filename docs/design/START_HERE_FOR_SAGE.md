# NoorVox — Handoff for Sage

**Please read `NOORVOX_DESIGN.md` in full before coding.** `NoorVox_Interface_Mockup.png` is the human-facing visual target; it is not evidence that any feature exists yet.

## Mission

Build a polished Windows local voiceover studio **and** a dependable headless CLI from a shared C# core. The first speech backend is a researched and verified local Kokoro/ONNX implementation. The studio supports documentary narration, audiobooks, game dialogue, educational narration and quick voiceovers. The final CLI/timing exports are intended to be driven by Codex for video production.

## Non-negotiable product requirements

- Use **C#/.NET**, **WPF/WebView2**, and **React/TypeScript** unless a measured incompatibility requires a documented deviation.
- One core, human-facing GUI and AI-friendly CLI. The CLI cannot require launching WebView2.
- A clean runtime/process manager is essential: actual state, PID, health, active jobs, start/load/unload/stop/force-stop, Stop All and no accidental orphan processes or duplicate workers.
- **C: is allowed.** Never prevent other users from choosing C:. But allow Tom to select model, cache, temp, project and output paths on non-C: drives. Validate and display resolved model/cache paths. Do not promise literally zero OS-managed C: writes.
- Do not expose fake voice parameters; display what the installed backend actually supports.
- Batch workflows must be resumable, approved takes immutable by default, exports accompanied by actual segment timings.
- Offline synthesis without paid tokens or APIs.
- Create tested documentation **for other AIs** (`AI_INTEGRATION.md`, `CLI_REFERENCE.md`, `PROJECT_FORMAT.md`, examples), so Codex can generate audio without UI automation or guesswork.
- Design source code for reuse and future model backends, but implement a verified working vertical slice before building broad feature lists.

## First milestone acceptance demo

1. User selects non-C: folders for models, caches, audio and projects.
2. Sage imports a compatible local Kokoro model and verifies source/license/hash.
3. CLI starts a managed worker, loads the model, synthesizes a WAV, lists the worker, and stops/unloads it.
4. GUI shows truthful worker state and can synthesize/preview/export the same speech.
5. Closing the GUI under the default policy leaves no managed model worker consuming resources.
6. A tested CLI example produces JSON describing the real generated file and duration.

If model unloading cannot safely reclaim resources in-process, isolate the model worker and shut down/recycle that process. Document this clearly.

## Final delivery expectations

- Reproducible build and install/launch instructions.
- Working executable(s), not solely designs.
- Screenshot(s) of the working GUI, plus comparison to the concept.
- Tests for storage configuration, process cleanup, offline synthesis and project recovery.
- A CLI/API integration cookbook with exact commands **that actually run**.
- Known limitations, benchmark results and model-license notes.

**Important:** This archive is a **design handoff**, not a finished NoorVox app. Proposed CLI examples in the design spec are targets and must be updated to match the real implementation.
