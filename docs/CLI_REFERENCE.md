# NoorVox CLI reference

`noorvox.exe` is the headless interface to NoorVox. It never starts WebView2, and it never prompts.
This document covers **what is implemented in NoorVox 0.1.0**. Anything not listed here does not exist yet.

- Machine-readable help: `noorvox --help --json` (commands, options, exit codes).
- Contract version: envelope `schemaVersion: 1`.
- A test (`DocsContractTests`) fails if a command in the CLI is missing from this file.

## Conventions

| Topic | Behaviour |
|---|---|
| `--json` | stdout receives **exactly one** JSON envelope (below). Nothing else is written to stdout. |
| Progress | Always goes to **stderr**: a live-updating line in a terminal, or a line every ~2 s when redirected. Suppress it with `--quiet`. |
| `--non-interactive` | Accepted everywhere. The CLI never prompts, so it changes nothing. |
| Paths | Every returned path is an absolute, resolved path. Relative inputs resolve against the current directory. |
| Overwrites | `synth` refuses to overwrite an existing file unless you pass `--overwrite` (code `output_exists`). |
| Decimal numbers | Use `.` (`--speed 1.1`), whatever the Windows locale. |
| Home / settings | `%APPDATA%\NoorVox\settings.json`. Override with `NOORVOX_HOME=<dir>`, or put a `noorvox.portable` file next to the exe (everything then lives in `.\data`). |

### JSON envelope

```json
{
  "schemaVersion": 1,
  "tool": "noorvox",
  "toolVersion": "0.1.0",
  "command": "synth",
  "success": true,
  "code": "ok",
  "message": "Wrote E:\\AI\\NoorVox\\Exports\\line.wav (3.52 s).",
  "data": { },
  "warnings": []
}
```

On failure: `success: false`, `code` is an error code (below), `message` is readable text, and `data` holds details
when available (for example a `hint` with the command that fixes the problem). The process exit code is non-zero.

### Exit codes

| Exit | Meaning | Typical `code` values |
|---:|---|---|
| 0 | Success | `ok` |
| 1 | Internal error (bug) | `internal_error` |
| 2 | Invalid arguments | `invalid_arguments` |
| 3 | Configuration error | `config_error` |
| 4 | Model or voice missing | `model_not_installed`, `voice_not_found`, `unknown_model` |
| 5 | Invalid input text | `invalid_input` |
| 6 | Worker busy / already running | `worker_busy`, `worker_already_running` |
| 7 | Worker failure | `worker_start_failed`, `worker_unresponsive`, `worker_stop_timeout`, `worker_error`, `model_load_failed` |
| 8 | Synthesis failed | `synthesis_failed` |
| 9 | File system | `io_error`, `permission_denied`, `output_exists` |
| 10 | Download / verification | `download_failed`, `verification_failed` |
| 11 | Cancelled (Ctrl+C) | `cancelled` |
| 12 | Not found | `worker_not_running` |

## Commands

### `noorvox version`
Also available as `noorvox --version`. `data`: `version`, `schemaVersion`, `runtime`, `os`, `executable`.

### `noorvox doctor`
Health check. It always exits 0 when it can run. Read `data.healthy` and `data.issues` (each issue includes the command that fixes it).
`data` also includes `home`, `storage[]`, `backends`, `catalogVersion`, `models[]`, `workerExecutable` and `workers[]`.

### `noorvox storage show`
Shows the configured and resolved path for each storage class: `models`, `cache`, `temp`, `projects`, `exports`, `webview`.
For each one it reports `path`, `source` (`configured` / `default…`), `drive`, `exists` and `freeBytes`. It never creates folders.

### `noorvox storage set [--models DIR] [--cache DIR] [--temp DIR] [--projects DIR] [--exports DIR] [--webview DIR]`
Each path is created, checked by writing a probe file, and saved. Pass the value `default` to reset one path.
Existing files are **not moved**. C: is allowed.

### `noorvox storage audit`
Every storage location (plus the home) with `path`, `source`, `drive`, `exists`, `bytes`, `files`, `freeBytes` and `onSystemDrive`,
plus `strays[]`: NoorVox data found in default locations that the current settings don't use (`path`, `bytes`, `files`, `note`).
Each stray is also listed in `warnings`. Read-only. See STORAGE_AND_PRIVACY.md.

### `noorvox diagnostics bundle [--out FILE.zip]`
Writes a support zip with `environment.json`, `settings.json` (recent project paths reduced to folder names), `storage-audit.json`,
`models.json`, `workers.json`, `logs/` (last 1 MB of each NoorVox log) and `README.txt`. It never includes scripts, text, audio or project files.
Default: `<exports>\noorvox-support-<timestamp>.zip`. `data`: `path`, `bytes`.

### `noorvox models list`
`data.installed[]` lists installed models: `reference`, `path`, `modelSha256`, `license`, `source`, `origin` (`download`/`import`), `voices` (count) and `voicePacks`.
`data.catalog[]` lists models NoorVox can install, with `downloadBytes`, `installed` and `updateAvailable`.

### `noorvox models install <id[@version]> [--no-voices]`
Downloads from the catalog URL into `<cache>\staging`, resuming an interrupted download where it left off. It checks the
size and sha256 of every download and every file, moves the files into `<models>\<id>\<version>\`, and writes the
manifest last. By default it also installs the model's default voice packs. Running it again on an installed model does nothing
(`status: alreadyInstalled`, `bytesDownloaded: 0`).
Example: `noorvox models install kokoro-82m`.

### `noorvox models update [--check]`
For each installed model id, installs the newest catalog version alongside the existing one, plus any missing default voice packs.
Old versions are kept, because takes record the model hash they were made with. `--check` only reports what would be installed (`updateAvailable`).
The v0.1 catalog is embedded in the app; refreshing the catalog from the internet is planned.

### `noorvox models import --from DIR`
Imports weights that are already on disk. The `*.onnx` file must match a catalog sha256; unknown weights are rejected with `unknown_model`.
Voice packs found **complete** in `DIR/voices/` are imported as well; incomplete packs are skipped, with a warning. Import uses no network.

### `noorvox models verify [<id[@version]>] [--quick]`
Re-hashes all files (sha256) of one model or all installed models, including their voice packs. `--quick` checks only that files exist and have the right size.
Fails with `verification_failed` (exit 10) and per-file problems in `data`.

### `noorvox voices list [--model REF] [--language CODE]`
Lists installed voices (from the installed voice packs). Each voice has `id` (e.g. `af_heart`), `language` (`en-us`, `en-gb`, `ja`, `zh`, `es`, `fr`, `hi`, `it`, `pt-br`), `languageName`, `gender`, `displayName` and `packId`.
No worker is needed.

### `noorvox voices packs [--model REF]`
Lists the voice packs available for a model, with `installed`, `voices` (count) and `downloadBytes`.

### `noorvox voices install <pack-id[@version]> [--model REF]`
Downloads, verifies and installs a voice pack into an installed model. It uses the same staging and verification as `models install`.

### `noorvox workers list`
Lists NoorVox-managed workers that are running or broken. Stopped workers are omitted. For each worker it reports:

| Field | Meaning |
|---|---|
| `state` | `starting`, `idle` (no model), `loading`, `ready`, `generating`, `stopping`, `error` (process gone without clean shutdown), `unresponsive` (process alive, no IPC answer) |
| `pid`, `startedAt`, `uptimeSeconds`, `lastHeartbeatAt` | Process facts. `lastHeartbeatAt` is when this call got an answer. |
| `loadedModel` | `id`, `version`, `modelSha256`, `directory`, `loadedAt`, `loadMs`. `null` = no model loaded. |
| `currentJob` | The running synthesis (`requestId`, `startedAt`, `textChars`, `voiceId`), or `null`. |
| `resources` | `workingSetBytes`, `privateBytes`, `cpuTimeMs` (cumulative), `sampledAt`, `source`, `gpu` (always reported honestly: unavailable on CPU) |
| `boundToLauncher` | `true` = the worker dies automatically with the process that launched it |
| `idleTimeoutSeconds`, `logPath`, `error` | |

Only processes that NoorVox launched and recorded (PID plus exact start time) are reported or touched.

### `noorvox workers status [--worker ID]`
Same fields for one worker (default id `default`). A worker that isn't running reports `state: "stopped"` (exit 0).

### `noorvox workers start [--worker ID] [--model REF] [--idle-timeout SECONDS]`
Starts a **persistent** worker that keeps running after the CLI exits. Without `--model` it starts `idle`; with `--model` it loads the model and returns `ready`.
It exits by itself after `--idle-timeout` seconds without work (default from settings: 900; `0` = never).
Starting an id that is already running fails with `worker_already_running` (exit 6); NoorVox never starts duplicates.

### `noorvox workers load [--worker ID] [--model REF]`
Loads a model into a running worker. If the worker has a different model loaded, it is recycled first.

### `noorvox workers unload [--worker ID]`
Unloads by **recycling** the worker: a graceful stop, then a fresh process with the same id and no model loaded.
Unloading inside the same process does not return memory reliably (measured; ADR-0001). `data.status.pid` is the new PID.

### `noorvox workers stop (--worker ID | --all) [--force] [--timeout SECONDS]`
Graceful by default: the worker finishes its current synthesis, then exits. If it doesn't exit within `--timeout` (default 30 s), the command fails with
`worker_stop_timeout` and names the active job. Run again with `--force` to terminate the process tree (the interrupted job's output is discarded).
Stopping a crashed (`error`) worker clears its stale record. `--graceful` is accepted for readability.

### `noorvox synth (--text TEXT | --text-file FILE | --stdin) [options]`

| Option | Default | Notes |
|---|---|---|
| `--out FILE.wav` | `<exports>\noorvox_<timestamp>.wav` | Must end in `.wav`. The folder is created if needed. |
| `--voice ID` | settings → model default (`af_heart`) | Must be installed (`voices list`). |
| `--speed X` | `1.0` | 0.5 – 2.0. This is the only delivery control Kokoro really supports. |
| `--model REF` | settings → first installed | `kokoro-82m` or `kokoro-82m@1.0` |
| `--worker ID` | — | Use this specific worker; it is started (bound to this CLI call) if it isn't running. |
| `--keep-worker` | off | Leave the worker running afterwards (persistent `default`, with the idle timeout). |
| `--overwrite` | off | Replace an existing `--out`. |
| `--manifest` | off | Also write `<out>.noorvox.json` containing the same `data`. |

**How a worker is chosen:**
1. A running `default` worker that is `ready` with the same model is reused, and left running.
2. A running `default` worker that is `idle` gets the model loaded, is used, and is left running.
3. Otherwise a private one-shot worker `synth-<pid>` is started **bound to this CLI process**, used, and stopped.
   If the CLI is killed, Windows terminates that worker too (Job Object). No orphans.

**Output** (`data`): `outputPath`, `durationMs`, `durationSeconds`, `sampleRate` (24000), `channels` (1), `bitsPerSample` (16),
`samples`, `bytes`, `text {chars, sha256}`, `voice`, `speed`, `model {id, version, modelSha256, license}`,
`backend {id, executionProvider}`, `worker {id, reused, startedForRequest, stoppedAfter, stateAfter}`,
`timings {synthesisMs, totalMs, realTimeFactor}`, `createdAt`, `generator` and, with `--manifest`, `manifestPath`.

Durations are **measured from the written WAV's samples**, never estimated. The file is synthesized into `<temp>\synth`,
validated as PCM WAV, then moved to `--out`; a failed run leaves no partial file at `--out`.

## Projects (Phase 3)

The project format is described in [PROJECT_FORMAT.md](PROJECT_FORMAT.md). Every project command takes `--project DIR` (the folder containing
`noorvox.project.json`).

### `noorvox projects create --path DIR --title TITLE [--kind KIND] [--voice ID] [--model REF]`
Creates the project folder. `DIR` must be new or empty. Kinds: `documentary`, `audiobook`, `dialogue`, `narration` (default), `education`, `custom`.
`--voice` sets the default voice. `data`: `root`, `project`, `kind` (labels, id prefix, default pauses).

### `noorvox projects import --project DIR --file FILE [--format md|txt|csv|json]`
Appends sections from a script: Markdown/text headings and paragraphs, or a dialogue CSV/JSON with `line_id, character, text[, set, notes]`.
Ids are validated before anything is written; on error (e.g. a duplicate `line_id`) the project is unchanged (exit 5). The original is kept
read-only in `references/`. `data`: `reference`, `sections[] {sectionId, title, scenes, segments, firstSegmentId}`, `speakers`.

### `noorvox projects show --project DIR [--segments] [--no-text]`
Summary: `title, kind, labels, model, defaults, speakers, segments, counts {approved, generated, stale, missing, empty}, approvedDurationMs,
sections[] {sectionId, title, scenes, segments, counts}, recentJobs, warnings`. With `--segments`, `data` is `{ summary, sections }`, where `sections`
holds every scene and segment: `segmentId, text, speakerId, effectiveVoice, effectiveSpeed, spokenText, status, takes, approvedTakeId,
latestTakeId, durationMs, notes`. `--no-text` omits the text fields.

### `noorvox projects validate --project DIR`
Checks section files, unique and valid ids, known speakers, speed range, approved takes existing on disk, and that the voices used are installed.
On problems it exits 5 with `data.problems[]`.

### `noorvox projects rename --project DIR --title TITLE`
Changes the title in `noorvox.project.json`. The folder isn't renamed.

### `noorvox projects duplicate --project DIR --path NEWDIR [--title TITLE]`
Copies scripts, takes (so approvals stay valid), references and pronunciation to an empty `NEWDIR`, with a new `projectId`.
The title defaults to "<title> (copy)". Job history, locks, temp audio and `exports\` aren't copied. `data`: `root`, `projectId`, `title`.

### `noorvox projects cast --project DIR [--speaker ID] --voice ID [--speed X]`
Without `--speaker`, sets the project default voice (and speed). With `--speaker`, sets that character's voice. Approved takes are untouched;
affected segments become `stale` until regenerated and approved.

### `noorvox generate --project DIR [--section ID] [--scene ID] [--segment ID[,ID…]] [--policy missing|all] [--approve] [--worker ID] [--keep-worker]`
Creates a **job** for the scope (default: whole project) and runs it now, one segment at a time, using the same worker rules as `synth`.

- `--policy missing` (default) reuses a take that matches the current text, voice, speed, model and pronunciation, and generates otherwise.
  `all` always makes a new take.
- `--approve` approves the resulting take for segments **without** an approved take. It never replaces an approval.
- Each finished segment is committed (WAV, take record, job file) before the next one starts.
- Progress per segment goes to stderr: `[3/6] ep01.s02.p001 generated (5.0 s)`.

`data`: `job {jobId, state, total, done, failed, remaining, generated, reused, skipped, …}` (cumulative across runs of this job),
`thisRun {generated, reused, skipped, failed}`, `items[] {segmentId, state, takeId, durationMs, error}`.

Exit codes:

| Exit | When | What `data` holds |
|---|---|---|
| 0 | Every item done | the job summary |
| 8 `synthesis_failed` | Some items failed | the same data; the successful ones are kept |
| 11 `cancelled` | Ctrl+C | `jobId` and the exact `resume` command |

### `noorvox jobs list --project DIR`
Jobs newest first, with the same summary fields. A job whose process died is reported as `interrupted`.

### `noorvox jobs status --project DIR --job ID`
One job with every item.

### `noorvox jobs resume --project DIR --job ID [--worker ID] [--keep-worker]`
Continues an interrupted job, or retries its failed items, from the first unfinished segment. Finished items are never redone.
Output is the same as `generate`.

### `noorvox takes list --project DIR --segment ID`
`segmentId, text, status, approvedTakeId, takes[] {takeId, path, durationMs, voiceId, speed, createdAt, jobId, approved, matchesCurrent, spokenText}`.

### `noorvox takes approve --project DIR --segment ID (--take ID | --latest)`
Moves the approval pointer. Take files are immutable.

### `noorvox takes unapprove --project DIR --segment ID`
Clears the approval; no files are deleted.

### `noorvox export clips --project DIR [--section ID] [--out DIR] [--allow-missing] [--overwrite]`
Copies approved takes with id-based names: dialogue uses `npc_vendor_greeting_001.wav`, other kinds `001_ep01.s01.p001.wav`. It also writes
`clips.json` (per clip: `segmentId, sectionId, sceneId, file, durationMs, sampleRate, text, speaker, voiceId, takeId, stale`).
Default `--out`: `<project>\exports\<section|all>_clips_<timestamp>`.

- Segments without an approved take fail the export (exit 5) unless `--allow-missing` is given; then they're skipped and listed in `missing`.
- Approved-but-stale takes are exported and reported in `warnings`.
- Existing files are never overwritten without `--overwrite`.

### `noorvox export assemble --project DIR [--section ID] [--rate HZ] [--normalize] [--peak-dbfs DB] [--no-subtitles] [--out DIR] [--allow-missing] [--overwrite]`
Joins each section's approved takes into **one WAV** (all sections if `--section` is omitted) and writes, next to it:

| File | Content |
|---|---|
| `<sectionId>_<title>.wav` | PCM 16-bit mono at `--rate` (default: the model's native 24000; allowed 16000, 22050, 24000, 32000, 44100, 48000, 96000) |
| `<sectionId>_<title>.srt` / `.vtt` | One cue per segment at its measured position; text wrapped at 42 characters; dialogue speakers as `NAME:` (SRT) or `<v Name>` (VTT). `--no-subtitles` skips them. |
| `<sectionId>_<title>.manifest.json` | Timing manifest, schema [schemas/assembly-manifest.schema.json](schemas/assembly-manifest.schema.json) |
| `assembly_index.json` | Only when several sections are assembled: the list of manifests and the total duration |

- **Pauses** come from the project (`pauses` in `noorvox.project.json`): `sectionEdgeMs` at the start and end, `betweenScenesMs` across a scene
  boundary, `betweenSegmentsMs` otherwise. A segment's `pauseBeforeMs`/`pauseAfterMs` replaces the adjacent gap (if both sides set one, the larger wins).
- **Resampling** uses windowed sinc (Blackman, 32 zero-crossings), tested for length, pitch, level and aliasing rejection.
  **`--normalize`** scales the export so its peak is `--peak-dbfs` (default −1). Takes on disk are never modified; `processing.gainDb` records the gain.
- **Timing** is exact: every segment's `startMs/endMs` (and `startSample/endSample` at the output rate) is where its approved take sits in the file.
  There's no word-level alignment.
- Segments without an approved take fail the command (exit 5) unless `--allow-missing` is given; then they're left out and listed in `missing`.
  Approved but out-of-date takes are used and listed in `stale` (also in `warnings`).
- Default `--out`: `<project>\exports\assembled_<timestamp>`. Existing files are never replaced without `--overwrite`.

`data`: `outputDir`, `index`, `sections[] {manifest, audio, srt, vtt, durationMs, sampleRate, segments, missing, stale, gainDb}`, `totalDurationMs`.

### Manifest at a glance (`*.manifest.json`)

```json
{
  "schemaVersion": 1, "kind": "noorvox.assembly", "generator": "NoorVox 0.1.0",
  "project": { "projectId": "…", "title": "NoorEngine Documentary", "kind": "documentary", "root": "E:\\…" },
  "section": { "sectionId": "ep01", "title": "Episode 1 – The Terrible Idea" },
  "audio": { "file": "E:\\…\\ep01_episode_1_the_terrible_idea.wav", "sampleRate": 48000, "channels": 1, "bitsPerSample": 16,
             "samples": 1661520, "durationMs": 34615, "bytes": 3323084, "sha256": "…" },
  "subtitles": { "srt": "…srt", "vtt": "…vtt", "granularity": "segment" },
  "pauses": { "betweenSegmentsMs": 350, "betweenScenesMs": 900, "sectionEdgeMs": 1500, "rule": "…" },
  "processing": { "sourceSampleRate": 24000, "resampled": true, "resampler": "windowed-sinc (Blackman, 32 zero-crossings)",
                  "normalized": true, "gainDb": 4.49, "peakDbfsAfter": -1 },
  "scenes": [ { "sceneId": "ep01.s01", "title": "Opening", "startMs": 1500, "endMs": 11194 } ],
  "segments": [ { "index": 2, "segmentId": "ep01.s01.p002", "sceneId": "ep01.s01", "sceneTitle": "Opening", "speaker": "Narrator",
                  "voiceId": "bm_george", "text": "What if we just wrote our own?", "spokenText": "What if we just wrote our own?",
                  "notes": "visual: empty editor window, cursor blinking", "startMs": 8761, "endMs": 11194, "durationMs": 2433,
                  "startSample": 420528, "endSample": 537312, "takeId": "t2026…", "takeFile": "takes/ep01.s01.p002/t2026….wav", "stale": false } ],
  "missing": [], "stale": [],
  "timing": "Segment times are exact sample positions of each approved take in this file (ms = samples / sampleRate). No word-level alignment."
}
```

## Not implemented yet (planned)

GPU execution, remote catalog refresh, `models remove`, segment split/merge/reorder from the CLI, MP3/other codecs, loudness (LUFS)
normalisation, word-level alignment. See docs/design/NOORVOX_DESIGN.md §12 for the original target contract.
