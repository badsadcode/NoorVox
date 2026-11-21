# NoorVox CLI reference

`noorvox.exe` is the headless interface to NoorVox. It never starts WebView2, and it never prompts.
This document covers **what is implemented in NoorVox 0.2.0**. Anything not listed here does not exist yet.

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
  "toolVersion": "0.2.0",
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
| 2 | Invalid arguments | `invalid_arguments`, `unsupported` (a control the model doesn't have, e.g. `--speed` on Qwen) |
| 3 | Configuration error | `config_error` |
| 4 | Model or voice missing | `model_not_installed`, `voice_not_found`, `unknown_model`, `engine_not_installed` |
| 5 | Invalid input text | `invalid_input` |
| 6 | Worker busy / already running | `worker_busy`, `worker_already_running` |
| 7 | Worker failure | `worker_start_failed`, `worker_unresponsive`, `worker_stop_timeout`, `worker_error`, `model_load_failed`, `engine_broken`, `gpu_required` |
| 8 | Synthesis failed | `synthesis_failed`, `gpu_out_of_memory` |
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

### `noorvox storage set [--models DIR] [--cache DIR] [--temp DIR] [--projects DIR] [--exports DIR] [--webview DIR] [--recordings DIR]`
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
`data.installed[]` lists installed models: `reference`, `path`, `modelSha256`, `license`, `source`, `origin` (`download`/`import`), `voices` (count),
`voicePacks`, `capabilities` and `engine`.
`data.catalog[]` lists models NoorVox can install, with `downloadBytes`, `installed`, `updateAvailable`, `capabilities`, `engine` and `minVramGb`.

`capabilities` says which controls are real for that model (only these are accepted by `synth`, `projects cast` and the studio):

| Field | Kokoro 82M | Qwen3-TTS CustomVoice 1.7B | CustomVoice 0.6B | Base 1.7B / 0.6B |
|---|---|---|---|---|
| `speed` (`minSpeed`–`maxSpeed`) | yes, 0.5–2.0 | no | no | no |
| `style` (written directions) | no | yes | no | no |
| `cloning` (voice profiles) | no | no | no | yes |
| `blending` | yes | no | no | no |
| `seed` (reproducible takes) | no | yes | yes | yes |
| `requiresGpu` | no | NVIDIA + CUDA | NVIDIA + CUDA | NVIDIA + CUDA |
| `languages` | — (the voice decides) | `auto`, `english`, `chinese`, `japanese`, `korean`, `german`, `french`, `russian`, `portuguese`, `spanish`, `italian` | same | same |

Catalog ids: `kokoro-82m`, `qwen3-tts-1.7b-customvoice`, `qwen3-tts-1.7b-base`, `qwen3-tts-0.6b-customvoice`, `qwen3-tts-0.6b-base`,
`qwen3-tts-1.7b-voicedesign`, `chatterbox-english`, `chatterbox-multilingual`, `chatterbox-turbo`.

**Chatterbox** (Resemble AI, MIT; engine pack `chatterbox`): `cloning` is true for all three variants and needs only the profile's
reference audio, no transcript; voice `default` is the model's own voice. English and Multilingual have `emotion` and `pace`;
Turbo has neither (its code ignores them), but it has 19 inline `tags` (`[laugh]`, `[chuckle]`, `[sigh]`, `[gasp]`, `[cough]`, `[whispering]`,
`[angry]`, `[dramatic]`, …) that go straight into the text; `capabilities.tags` lists them, taken from the model's tokenizer. Captions
written by `export assemble` leave tags out. Multilingual adds 23 `languages`. `watermark: "perth"`: every output carries Resemble
AI's inaudible watermark, which `synth` reports as `data.watermark`.

### `noorvox models install <id[@version]> [--no-voices] [--no-engine]`
Downloads from the catalog URL into `<cache>\staging`, resuming an interrupted download where it left off. It checks the
size and sha256 of every download and every file, moves the files into `<models>\<id>\<version>\`, and writes the
manifest last. By default it also installs the model's default voice packs. Running it again on an installed model does nothing
(`status: alreadyInstalled`, `bytesDownloaded: 0`).
Example: `noorvox models install kokoro-82m`.

A model that needs an engine pack (Qwen: `qwen-tts`) gets it installed first unless you pass `--no-engine`; the engine result is listed
in `data.voicePacks` next to voice packs. Qwen models are multi-file Hugging Face snapshots at a pinned revision; files already present and
verified in the target folder are not downloaded again.

### `noorvox engines list`
`data.engines[]`: `id`, `version`, `displayName`, `kind` (`python` | `native`), `state` (`installed`, `notInstalled`, `incomplete`,
`updateAvailable`), `path`, `approxInstallBytes`, `license`, `source`, `requires`, `usedBy`, and `verified` (what the engine's own Python
reported when it was installed: `python`, `torch`, `transformers`, `cuda`, `cudaVersion`, `device`, `deviceMemoryBytes`).

| Engine | What it is | Needed for |
|---|---|---|
| `chatterbox` | Private Python 3.12.10 plus PyTorch 2.6 + CUDA 12.4 and chatterbox-tts 0.1.7 (hash-locked) | Chatterbox models (it pins an older PyTorch than Qwen, so it is a separate pack) |
| `qwen-tts` | Private Python 3.12.10 (python.org NuGet package, sha256-checked) plus PyTorch 2.11 + CUDA 12.8, faster-qwen3-tts 0.5.4 and transformers 5.15.1 (~5.2 GB) | Qwen3-TTS models; also provides the CUDA/cuDNN DLLs for Kokoro on GPU |
| `onnxruntime-cuda` | ONNX Runtime 1.22 GPU DLLs from the official NuGet package (~330 MB) | `gpu set --kokoro cuda` |

### `noorvox engines install <qwen-tts|chatterbox|onnxruntime-cuda>`
Installs into `<models>\engines\<id>\<version>\`. Python engines run `pip install --require-hashes --no-deps --only-binary=:all:` with
a lock embedded in NoorVox: every package is pinned to an exact version **and** its sha256, so nothing unverified is installed. pip's cache
lives in `<cache>\pip`. The manifest `noorvox.engine.json` is written last, after an import + CUDA check; until then the engine counts as
`incomplete`, and running the command again resumes. pip's output is passed through as progress (`[done/total packages]`).

### `noorvox engines remove <id>`
Deletes the engine folder. Models, voice profiles and takes are untouched; Qwen models can't load until the engine is installed again.

### `noorvox gpu status`
`data.kokoroProvider` (`cpu`/`cuda`), `kokoroCudaReady` (both engine packs installed), `engines` paths, and `detected`: the GPU as PyTorch
saw it when the `qwen-tts` engine was installed (`detectedAt`). This is a stored measurement, not a live probe.

### `noorvox gpu set --kokoro cpu|cuda`
Chooses Kokoro's execution provider for workers that load it afterwards (`workers unload` to apply now). With `cuda`, the worker loads
ONNX Runtime's GPU build from the `onnxruntime-cuda` engine and CUDA 12 / cuDNN 9 from the `qwen-tts` engine. If either is missing, or
CUDA fails, Kokoro runs on the CPU and the worker reports why (`workers status` → `error` note, `executionProvider: CPU`).
Measured on an RTX 3080: CPU real-time factor 0.17, CUDA 0.09, same speech (ADR-0002).

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
Each voice has `id`, `language`, `languageName`, `gender`, `displayName`, `packId`, `kind` (`pack`, `blend`, `built-in` for Qwen's nine
speakers, `clone` for voice profiles) and an optional `description`.
Lists installed voices (from the installed voice packs). Each voice has `id` (e.g. `af_heart`), `language` (`en-us`, `en-gb`, `ja`, `zh`, `es`, `fr`, `hi`, `it`, `pt-br`), `languageName`, `gender`, `displayName` and `packId`.
No worker is needed.

### `noorvox voices packs [--model REF]`
Lists the voice packs available for a model, with `installed`, `voices` (count) and `downloadBytes`.

### `noorvox voices blend --name NAME --mix ID:WEIGHT,ID:WEIGHT[,…] [--model REF]`
Creates a new voice as a weighted average of 2–4 installed voices, e.g. `--mix bm_george:0.6,bm_fable:0.4`. Weights are normalised.
- The voice id is `<dominant voice's language+gender prefix>_<name letters/digits>` (e.g. `bm_documentarynarrator`). The dominant voice decides the language.
- It's saved as `voices/<id>.npy` in the model folder, with its recipe in `voices/<id>.blend.json`, and registered (with sha256) in the model's local
  `custom-voices` pack. From then on it works everywhere a voice id is accepted.
- Blends are **immutable**: an existing id is refused (`output_exists`, exit 9), because takes are matched by voice id. Create a new name instead.

`data`: `voiceId, name, path, sha256, parts[] {voiceId, weight}, language`.

### `noorvox voices clone --name NAME --reference FILE.wav (--transcript TEXT | --transcript-file FILE) --consent own-voice|speaker-permission [--language NAME] [--gender female|male]`
Creates a **voice profile** (`kind: clone`) for models that can clone (Qwen3-TTS Base). The reference must be a PCM WAV of 2–60 s (5–15 s of clean,
single-speaker speech works best); the transcript is exactly what is said in it. `--consent` is required and stored verbatim with the
profile:

| `--consent` | Stored statement |
|---|---|
| `own-voice` | "This recording is my own voice." |
| `speaker-permission` | "I have the speaker's permission to create and use a synthetic copy of their voice." |

The recording is **copied** into `<models>\voice-profiles\<voiceId>\reference.wav` with its sha256; only the original *file name*
is recorded, never its path. Profiles are immutable (a second profile with the same name fails with `output_exists`). The voice id is
derived from the name (`"Tom Narrator"` → `tom_narrator`) and appears in `voices list` of every cloning model with `kind: clone`.
`synth` output and take records then carry `voiceKind: clone`.

### `noorvox voices design --name NAME --description TEXT [--text SAMPLE] [--language NAME] [--seed N]`
Makes a new voice with **Qwen3-TTS VoiceDesign** (`qwen3-tts-1.7b-voicedesign`): it speaks `--text` (default: a neutral storytelling
sentence) in a voice matching `--description` ("a warm British woman in her fifties, calm audiobook narrator"), then keeps that sample as a
voice profile of `kind: designed`, spoken by a Qwen Base model from then on (so it stays the same voice every time). No real person's
voice is involved, so instead of a consent statement the profile records its provenance (model, description, and the statement
"Synthetic voice designed by Qwen3-TTS VoiceDesign…"). `data`: `voiceId`, `name`, `kind`, `description`, `referenceDurationMs`, `seed`,
`path`, `usableWith`. Each run designs a *new* voice; pass `--seed` to reproduce one.

The VoiceDesign model can also be used directly with `synth --model qwen3-tts-1.7b-voicedesign --voice designed --style "<description>"`;
there `--style` is required and *is* the voice.

### `noorvox recordings list`
The Voice lab library: `<recordings>\<id>.wav` + `<id>.json` (default `Documents\NoorVox\Recordings`, set with `storage set --recordings`).
`data.recordings[]`: `id`, `name`, `transcript`, `notes`, `source` (`microphone`/`imported`), `durationMs`, `sampleRate`, `createdAt`, `path`.
A recording can become a cloned voice with `voices clone --reference <path> --transcript … --consent …` (the user's consent, never the agent's).

### `noorvox voices remove-profile --voice ID`
Deletes a voice profile (its reference copy and consent record). Takes and exports already made with it are not touched.

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
| `--speed X` | `1.0` | Kokoro: 0.5 – 2.0. Models without a speed control (Qwen) accept only 1.0 (`unsupported` otherwise). |
| `--style TEXT` | — | Delivery directions, e.g. `"Calm, warm documentary narration. Speak slowly."` Only models with `capabilities.style` (Qwen CustomVoice 1.7B), or the voice description for VoiceDesign. Max 1,000 characters (NoorVox's cap; 1–3 concrete sentences work best). |
| `--seed N` | random | 0 – 2147483647. Models with `capabilities.seed` (Qwen): the same seed, text, settings, GPU and engine give a byte-identical take. The seed used is always reported in `data.seed`. |
| `--language NAME` | `auto` / profile language | Models with `capabilities.languages`: Qwen `auto`, `english`, `chinese`, …; Chatterbox Multilingual one of its 23 languages (`english`, `german`, `polish`, …). |
| `--emotion X` | model default (0.5) | Models with `capabilities.emotion` (Chatterbox English/Multilingual): emotion intensity `emotionMin`–`emotionMax` (0.25–2.0): low = flat and calm, high = dramatic. |
| `--pace X` | model default (0.5) | Models with `capabilities.pace` (Chatterbox English/Multilingual): `paceMin`–`paceMax` (0–1); lower = slower, more deliberate delivery. Try `--emotion 0.7 --pace 0.3` for drama. |
| `--model REF` | settings → first installed | e.g. `kokoro-82m`, `qwen3-tts-1.7b-customvoice@1.0` |
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
`timings {synthesisMs, totalMs, realTimeFactor}`, `createdAt`, `generator`, `style`, `seed`, `language`, `emotion`, `pace`, `watermark`, `voiceKind`
(`pack`/`blend`/`built-in`/`clone`) and, with `--manifest`, `manifestPath`. Qwen output is 24 kHz mono 16-bit, like Kokoro.

Qwen models load in their engine's Python process (the worker's child, in the worker's kill-on-close Job Object). The first load of a 1.7B
model reads ~4.5 GB and captures CUDA graphs: expect 30 s to several minutes on an HDD; later syntheses in a kept worker are ~2.6× faster
than real time on an RTX 3080. Use `--keep-worker` (or `workers start --model …`) for more than one line.

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

### `noorvox projects split --project DIR --segment ID (--at INDEX | --before TEXT)`
Splits a segment at a character index, or before the first occurrence of `TEXT`. The first part keeps the id and its approval pointer (now
`stale`, since its text changed); the second part becomes a new segment right after it (same speaker/voice/speed, no takes) and inherits
`pauseAfterMs`. `data`: `first`, `second` (segments).

### `noorvox projects merge --project DIR --segment ID`
Appends the next segment of the **same scene** (same speaker) and removes it from the script. Its take files stay on disk.
Notes are joined. `data`: the merged segment.

### `noorvox pronunciation list --project DIR`
The rules in `voices\pronunciation.json`: `match, replace, wholeWord, caseSensitive`.

### `noorvox pronunciation set --project DIR --match WORD --replace TEXT [--case-sensitive] [--partial]`
Adds or replaces the rule for `WORD` (whole words and case-insensitive by default; `--partial` also matches inside words).
`data.segmentsChanged` counts segments whose spoken text changes. Approved ones among them become `stale`.

### `noorvox pronunciation remove --project DIR --match WORD`
Removes the rule (exit 2 if there is none).

### `noorvox projects cast --project DIR [--speaker ID] --voice ID [--speed X] [--style TEXT] [--model REF]`
Without `--speaker`, sets the project default voice (and speed/style); `--model` switches the whole project to another installed model
(e.g. `qwen3-tts-1.7b-customvoice`). With `--speaker`, sets that character's voice and optional style (`--style ""` clears it). Options
are checked against the model's capabilities. Approved takes are untouched; affected segments become `stale` until regenerated and approved.
A segment can override the style in its section file (`"style": "whispering"`), see PROJECT_FORMAT.md.

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

Remote catalog refresh, Qwen VoiceDesign (voice from a description), streaming playback, `models remove`, segment split/merge/reorder from the CLI, MP3/other codecs, loudness (LUFS)
normalisation, word-level alignment. See docs/design/NOORVOX_DESIGN.md §12 for the original target contract.
