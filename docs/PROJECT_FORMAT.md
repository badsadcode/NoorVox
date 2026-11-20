# NoorVox project format (schemaVersion 1)

A NoorVox project is a **folder of readable JSON plus WAV files**. There's no database, so you can version it with git (minus audio),
back it up, or edit the scripts by hand. This document describes the format as implemented in NoorVox 0.1.0.
Sources: `src/NoorVox.Core/Projects/*.cs`.

```text
NoorEngine Documentary/
  noorvox.project.json        project root: title, kind, defaults, speakers, section list
  scripts/
    ep01.json                 one file per section (episode / chapter / dialogue set)
  takes/
    ep01.s01.p001/
      t20261120-170435-9378.wav
      t20261120-170435-9378.json   take record (written after the WAV = commit marker)
  references/
    20261120_165512_episode1.md   original imports, read-only, never spoken from
  voices/
    pronunciation.json        text replacements applied before synthesis
  exports/                    default target for clip exports
  .noorvox/
    jobs/job_*.json           generation jobs (resumable)
    tmp/                      in-flight audio (cleared at the next job start)
    write.lock, job.lock      short-lived locks (safe to delete when nothing is running)
```

## Structure: Section → Scene → Segment

Every project has the same three levels. The **kind** (template) only changes labels, id prefixes and default pauses:

| kind | Section | Scene | Segment | Id prefix | Speakers |
|---|---|---|---|---|---|
| `documentary` | Episode | Scene | Segment | `ep` | narrator |
| `audiobook` | Chapter | Section | Paragraph | `ch` | narrator |
| `dialogue` | Dialogue set | Conversation | Line | `set` | characters |
| `narration` | Part | Section | Segment | `part` | narrator |
| `education` | Lesson | Section | Segment | `lesson` | narrator |
| `custom` | Section | Group | Segment | `sec` | characters |

### Ids

- Section ids: `<prefix><nn>`, e.g. `ep01`. Scene ids: `<section>.s<nn>`, e.g. `ep01.s02`.
- Segment ids: `<scene>.p<nnn>`, e.g. `ep01.s02.p003`. For dialogue they come from the imported `line_id` instead (`npc_vendor_greeting_001`).
- Ids are assigned once and **never change**. Reordering or editing text keeps them, and take folders and exported file names use them.
  Allowed characters: letters, digits, `_ - .` (max 80, must start with a letter or digit, no `..`). Ids are unique across the whole project.

## `noorvox.project.json`

```json
{
  "schemaVersion": 1,
  "projectId": "3f2a9c1b8d7e",
  "title": "NoorEngine Documentary",
  "kind": "documentary",
  "createdAt": "2026-11-20T17:04:09+01:00",
  "modifiedAt": "2026-11-20T17:06:12+01:00",
  "defaults": { "model": null, "voiceId": "bm_george", "speed": 1.0 },
  "pauses": { "betweenSegmentsMs": 350, "betweenScenesMs": 900, "sectionEdgeMs": 1500 },
  "speakers": [ { "speakerId": "narrator", "name": "Narrator", "voiceId": null, "speed": null } ],
  "sections": [ { "sectionId": "ep01", "title": "Episode 1 – The Terrible Idea", "file": "scripts/ep01.json" } ],
  "generator": "NoorVox 0.1.0"
}
```

- `defaults.model`: `null` means NoorVox's default installed model; or `"kokoro-82m"` / `"kokoro-82m@1.0"`.
- `pauses` (milliseconds) are used by `export assemble`. Generation doesn't use them. Segment `pauseBeforeMs`/`pauseAfterMs` override the adjacent gap.

## `scripts/<sectionId>.json`

```json
{
  "schemaVersion": 1,
  "sectionId": "ep01",
  "title": "Episode 1 – The Terrible Idea",
  "scenes": [
    {
      "sceneId": "ep01.s01",
      "title": "Opening",
      "segments": [
        {
          "segmentId": "ep01.s01.p001",
          "text": "Every engine starts somewhere.",
          "speakerId": "narrator",
          "voiceId": null, "speed": null,
          "pauseBeforeMs": null, "pauseAfterMs": null,
          "notes": "visual: empty editor window",
          "approvedTakeId": "t20261120-170435-9378"
        }
      ]
    }
  ]
}
```

- **Effective voice** = `segment.voiceId` → speaker's `voiceId` → `defaults.voiceId` → the model's default voice. **Effective speed** follows the same order (0.5–2.0).
- `notes` are editorial and never spoken.
- `approvedTakeId` is a *pointer*. Approving another take moves the pointer; take files are never modified or deleted.

## Takes: `takes/<segmentId>/<takeId>.{wav,json}`

```json
{
  "schemaVersion": 1,
  "takeId": "t20261120-170435-9378",
  "segmentId": "ep01.s02.p001",
  "cacheKey": "9d0c…",
  "spokenText": "It started, as so many terrible ideas do, with a single image.",
  "sourceTextSha256": "5b1e…",
  "voiceId": "bm_george", "speed": 1.0,
  "modelId": "kokoro-82m", "modelVersion": "1.0", "modelSha256": "0cfd5e79…bb05", "backend": "kokoro-onnx",
  "file": "takes/ep01.s02.p001/t20261120-170435-9378.wav",
  "durationMs": 4998, "sampleRate": 24000, "samples": 119952,
  "createdAt": "2026-11-20T17:04:35+01:00",
  "jobId": "job_20261120_170431_1a2b",
  "generator": "NoorVox 0.1.0"
}
```

- WAV: PCM 16-bit mono at the model's native rate (24 kHz for Kokoro). `durationMs`/`samples` are measured from the file.
- A take is complete only if **both** files exist: the WAV is moved into place first, then the JSON is written atomically.
  A WAV without JSON is an interrupted write and is ignored.
- **cacheKey** = sha256 over `v1`, spoken text (after pronunciation rules), voice, speed, model sha256 and backend id.
  Kokoro's output is not bit-identical between runs, so identity is defined by inputs, never by audio hashes.

### Segment status (computed, never stored)

| status | meaning |
|---|---|
| `empty` | no text |
| `missing` | no take matches the current cacheKey |
| `generated` | a take matches, none is approved |
| `approved` | the approved take matches the current cacheKey |
| `stale` | a take is approved, but text/voice/speed/model/pronunciation changed since; it stays approved until someone chooses |

## Jobs: `.noorvox/jobs/<jobId>.json`

A job is written **before** it runs and after **every** segment:

```json
{
  "schemaVersion": 1, "jobId": "job_20261120_170431_1a2b", "kind": "generate",
  "scope": { "sectionId": null, "sceneId": null, "segmentIds": null },
  "policy": "missing", "autoApprove": true, "state": "completed", "model": "kokoro-82m@1.0",
  "createdAt": "…", "startedAt": "…", "finishedAt": "…",
  "items": [ { "segmentId": "ep01.s01.p001", "state": "generated", "takeId": "t2026…", "durationMs": 4800, "finishedAt": "…" } ],
  "total": 6, "done": 6, "failedCount": 0, "remaining": 0
}
```

- `policy`: `missing` reuses a take that matches the current settings and generates otherwise; `all` always makes a new take.
- `autoApprove`: approves the resulting take **only** where a segment has no approved take. It never replaces an approval.
- Job states: `queued · running · completed · failed · interrupted`. Item states: `queued · running · generated · reused · skipped · failed · interrupted`.
- A job left `running` by a process that no longer holds `job.lock` is reported as `interrupted`. Resuming re-queues unfinished and failed items
  and continues; finished items are never redone. Only one job runs per project at a time.

## `voices/pronunciation.json`

```json
{ "schemaVersion": 1, "rules": [ { "match": "NoorEngine", "replace": "Noor Engine", "wholeWord": true, "caseSensitive": false } ] }
```

These are plain text replacements applied, in order, to produce the spoken text. They're part of the cacheKey, so changing a rule marks affected segments `stale`.
Phonetic (IPA) entries are not supported yet.

## Imports

| Format | Rules |
|---|---|
| `.md` / `.txt` | `# Heading` starts a section, `## Heading` (or deeper) starts a scene; blank-line paragraphs and list items become segments; `<!-- … -->` becomes the next segment's note; links, images and emphasis markup are stripped. Text before any heading goes into a section named after the file. |
| `.csv` | Header with `line_id`, `character`, `text` (aliases: `id`, `speaker`, `line`), optional `set` (one section per set) and `notes`. RFC 4180 quoting; `,` or `;` separators. Characters become speakers. |
| `.json` | `[ { "lineId", "character", "text", "set"?, "notes"? } ]` or `{ "lines": [ … ] }`. |

Imports **append** sections. All ids are validated before anything is written, so a rejected import leaves the project untouched.
The original file is copied, read-only, into `references/`.

## Clip export (`exports/…/clips.json`)

Approved takes are copied with id-based names: dialogue uses `<lineId>.wav`, everything else `<nnn>_<segmentId>.wav`. The `clips.json` lists
`segmentId, sectionId, sceneId, file, durationMs, sampleRate, text, speaker, voiceId, takeId, stale`, plus `missing`, `stale` and `totalDurationMs`.
Existing files are never overwritten unless asked.

## Assembly output (`*.manifest.json`, `.wav`, `.srt`, `.vtt`)

See `CLI_REFERENCE.md` → `export assemble` and the JSON Schema `docs/schemas/assembly-manifest.schema.json`. Assembly reads approved takes and
never modifies them.

## Concurrency and safety

- Every file write is atomic (temp file + replace). Writes to the project and to section files are serialised across processes by `.noorvox/write.lock`.
  The studio and CLI can work on the same project; for simultaneous edits of the same segment, the last writer wins.
- Paths in project files are relative and resolved inside the project folder. Anything pointing outside is rejected.
- Moving or copying the folder keeps the project intact; nothing stores absolute paths except `references` names and job `scope` ids.
