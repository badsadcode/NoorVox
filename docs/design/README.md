# NoorVox — Design Package

A proposed design and implementation handoff for the **NoorVox Local AI Voice Studio**, 20 November 2026.

## Contents

- **`NOORVOX_DESIGN.md`** — comprehensive product, UX, technical architecture, model/process lifecycle, storage, CLI, agent integration, quality gates, and implementation roadmap.
- **`NoorVox_Interface_Mockup.png`** — 1672 × 941 concept image illustrating the intended desktop studio; visual reference, not an implemented product.
- **`START_HERE_FOR_SAGE.md`** — concise build handoff, key decisions, and first milestone acceptance criteria.

## Key corrections captured

- Installing or storing data on `C:` is **allowed**. Users choose storage. Running with all significant model data and production files elsewhere must be supported.
- The GUI must include a truthful model/worker monitor and controls to start, load, unload, stop, and terminate NoorVox-owned workers cleanly.
- Voice generation supports more than documentaries: audiobooks, games, tutorials, and quick voiceovers.
- A documented headless CLI serves Codex and other AI agents without relying on UI automation.

This package is a **specification**, not an application binary.
