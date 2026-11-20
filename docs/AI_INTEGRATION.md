# NoorVox for AI agents (Codex & friends)

You can produce narration with NoorVox **without any UI automation**. Everything below runs headless and offline once a model is
installed, and every command block in this file has been run against NoorVox 0.1.0. `examples/quick-narration.ps1` is the same flow
as one script.

> NoorVox speaks text; it does **not** write or fact-check the script. Another agent (or a human) prepares and approves the text.
> Don't add claims to narration that the source material doesn't support.

## Golden rules

1. Always pass `--json`, parse stdout as a single JSON object, and branch on the **exit code** and `code`, never on message text.
2. Treat stderr as human progress noise. Use `--quiet` if you don't want it.
3. Use only paths that NoorVox returns (`data.outputPath` and so on). Don't guess where files went.
4. Never pass `--overwrite` unless the user asked you to replace a file. NoorVox refuses to overwrite by default.
5. Leave workers the way you found them. One-shot `synth` cleans up after itself. If you started a persistent worker, stop it.
6. Never kill `NoorVox.Worker.exe` by name. Use `noorvox workers stop`, which only touches workers NoorVox launched.

## 1. Check the installation

```powershell
$doctor = noorvox doctor --json | ConvertFrom-Json
if (-not $doctor.data.healthy) { $doctor.data.issues }   # each issue names the fixing command
```

If no model is installed (`model_not_installed`, exit 4), install the recommended one. This needs internet once, about 390 MB, and the files go
into the user's configured models folder:

```powershell
noorvox models install kokoro-82m --json --quiet
```

Ask the user before changing storage paths. `noorvox storage show --json` tells you where things live.

## 2. Pick a voice

```powershell
$voices = (noorvox voices list --language en-us --json | ConvertFrom-Json).data.voices
$voices | Select-Object id, gender, displayName
```

Each model declares what it supports in `capabilities` (`noorvox models list --json`). Send only those options: anything else fails
with exit 2 and code `unsupported`.

Kokoro supports **voice** and **speed** (0.5–2.0) only. There is no pitch, emotion or style control, so don't invent one.
Language follows the voice: `af_*`/`am_*` American English, `bf_*`/`bm_*` British English, `j*` Japanese, `z*` Mandarin,
`e*` Spanish, `f*` French, `h*` Hindi, `i*` Italian, `p*` Brazilian Portuguese.

## 3. Synthesize one line and verify it

```powershell
$r = noorvox synth --text "It started, as so many terrible ideas do, with a single image." `
       --voice bm_george --out "E:\AI\NoorVox\Exports\demo\line01.wav" --manifest --json --quiet | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "$($r.code): $($r.message)" }
$r.data.outputPath      # absolute path of the WAV
$r.data.durationMs      # measured from samples
Test-Path $r.data.outputPath
```

`--manifest` writes `line01.noorvox.json` next to the WAV, containing the text hash, voice, speed, model sha256 and timings.

## 4. Many lines: keep one worker warm

A one-shot `synth` starts the model each time, which costs about 4–5 s. For batches, start a persistent worker once, synthesize, then stop it:

```powershell
noorvox workers start --model kokoro-82m --idle-timeout 300 --json --quiet | Out-Null
$lines = @(
  @{ id = "ep01_s03_p01"; text = "It started, as so many terrible ideas do, with a single image." },
  @{ id = "ep01_s03_p02"; text = "A window, a Vulkan device, and a triangle. Nothing more." }
)
$results = foreach ($l in $lines) {
  $out = "E:\AI\NoorVox\Exports\demo\batch\$($l.id).wav"
  $r = noorvox synth --text $l.text --voice bm_george --out $out --json --quiet | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw "$($l.id): $($r.code) $($r.message)" }
  [pscustomobject]@{ id = $l.id; path = $r.data.outputPath; durationMs = $r.data.durationMs; reused = $r.data.worker.reused }
}
noorvox workers stop --worker default --json --quiet | Out-Null
$results
```

`reused: true` confirms the warm worker served the request. Name files by **stable line IDs**, not by text.

For long or tricky text, prefer `--text-file` (UTF-8) or `--stdin` over `--text`, to avoid shell-quoting problems:

```powershell
"Long paragraph...`nSecond paragraph." | noorvox synth --stdin --out "E:\AI\NoorVox\Exports\demo\para.wav" --json --quiet
```

## 5. Failure handling

| Exit / code | What to do |
|---|---|
| 4 `model_not_installed` | `noorvox models install kokoro-82m --json` (ask first if the user is offline or on a metered connection) |
| 4 `voice_not_found` | Choose an id from `voices list` |
| 5 `invalid_input` | Text was empty after trimming; fix the input |
| 6 `worker_already_running` / `worker_busy` | Someone else's worker is active. Use a one-shot `synth` (no `--worker`), or wait |
| 7 `worker_*` | `noorvox workers list --json`; for `error`/`unresponsive`: `noorvox workers stop --worker <id> --force --json` |
| 9 `output_exists` | Choose a new `--out`, or ask before using `--overwrite` |
| 10 `download_failed` | Run the same install command again; it resumes |
| 11 `cancelled` | Nothing was published to `--out`. For `generate`: finished segments are kept; run the `resume` command in `data` |

Retries are safe. A failed `synth` never leaves a partial file at `--out`, and installs resume and verify.

## 6. Lifecycle and cleanup

```powershell
noorvox workers list --json      # what's running, PID, model, memory, current job
noorvox workers stop --all --json
```

Note for sandboxed agents: if your command runner kills its whole process tree or job when a command ends, a persistent worker
may be terminated with it. Persistent workers try to leave the caller's job, but some sandboxes forbid that.
One-shot `synth` always works.

## 7. Projects: batch production with resume

For anything longer than a few lines, use a **project**: stable segment ids, takes you can compare, approvals, and resumable jobs.
`examples/production.ps1` runs all of this for a documentary, an audiobook and a game-dialogue set; it was run against NoorVox 0.1.0.

```powershell
$p = "E:\AI\NoorVox\Projects\NoorEngine Documentary"
noorvox projects create --path $p --title "NoorEngine Documentary" --kind documentary --voice bm_george --json --quiet
noorvox projects import --project $p --file "episode1.md" --json --quiet         # '# Episode', '## Scene', paragraphs
$r = noorvox generate --project $p --approve --json --quiet | ConvertFrom-Json   # whole project; --section/--scene/--segment to narrow
if ($LASTEXITCODE -eq 11) { noorvox jobs resume --project $p --job $r.data.jobId --json --quiet }   # after Ctrl+C / crash
$clips = noorvox export clips --project $p --json --quiet | ConvertFrom-Json
$clips.data.clips | Select-Object segmentId, file, durationMs, text
```

For game dialogue, import a CSV (`line_id,character,text[,set,notes]`), cast each character, generate, and export. The files are named by line id:

```powershell
noorvox projects import --project $dlg --file lines.csv --json --quiet
noorvox projects cast --project $dlg --speaker vendor --voice am_michael --json --quiet
noorvox generate --project $dlg --approve --json --quiet
noorvox export clips --project $dlg --json --quiet          # -> npc_vendor_greeting_001.wav, … + clips.json
```

Fixing pronunciation and segment boundaries headlessly:

```powershell
noorvox pronunciation set --project $p --match NoorEngine --replace "Noor Engine" --json --quiet   # data.segmentsChanged → regenerate those
noorvox projects split --project $p --segment ep01.s02.p003 --before "It wasn't even" --json --quiet
noorvox projects merge --project $p --segment ep01.s02.p003 --json --quiet
noorvox voices blend --name "Documentary Narrator" --mix bm_george:0.6,bm_fable:0.4 --json --quiet          # → voice bm_documentarynarrator
```

Rules for agents working with projects:

- **Never overwrite approvals.** `--approve` only fills segments with no approval. To change one, `takes approve --segment ID --take ID`,
  and only when the user asked for it.
- **Interrupted?** Resume the job and don't start a new one: `jobs list` shows `interrupted` jobs, and `jobs resume` finishes them without redoing finished segments.
  A new `generate --policy missing` is also safe (it reuses matching takes), but resuming keeps one job history.
- **Edited text** makes approved segments `stale` (`projects show`). Regenerate them, then let the user approve.
- **Check before handing off:** `projects show --json` → `counts.approved == segments`, or export with `--allow-missing` and report `missing`.

## 8. Finished episode for a video agent (script → WAV + subtitles + timing manifest)

`examples/agent-episode.ps1` is the whole pipeline, and it was run against NoorVox 0.1.0: Markdown script in, verified episode out.

```powershell
.\examples\agent-episode.ps1 -Script .\examples\projects\episode2.md -ProjectDir "E:\AI\NoorVox\Projects\Episode 2" -Voice bm_george
```

The steps it performs, which you can also run yourself:

```powershell
noorvox projects create --path $p --title "Episode 2" --kind documentary --voice bm_george --json --quiet
noorvox projects import --project $p --file episode2.md --json --quiet
noorvox generate --project $p --section ep01 --approve --json --quiet            # exit 11 → jobs resume (see §7)
noorvox export assemble --project $p --section ep01 --rate 48000 --normalize --out "$p\exports\episode" --json --quiet
```

Hand the video agent the **`*.manifest.json`** (schema: `docs/schemas/assembly-manifest.schema.json`). It points to the WAV, the SRT/VTT and
every segment's exact `startMs`/`endMs`, text, speaker and **`notes`**. Notes are the script's `<!-- visual: … -->` comments, meant as cut and B-roll cues.

Verify before handing off (the example does all of this):

- `audio.file`, `subtitles.srt` and `subtitles.vtt` exist; the WAV size is `44 + 2 × audio.samples`.
- Segments are in order: each `startMs` is at least the previous `endMs`, and the last `endMs` ≤ `audio.durationMs`.
- `missing` and `stale` are empty (or the user agreed otherwise); the SRT has one cue per segment.

Don't re-time anything from text length, and don't invent word timings: the manifest is segment-accurate by design.
For game dialogue, use `export clips` (one file per line id) instead of assembly.

## 9. Qwen3-TTS: directed delivery, reproducible takes, cloned voices (NVIDIA GPU)

Qwen voices follow written directions instead of a speed slider, and a seed makes a take reproducible. They need an NVIDIA GPU.

```powershell
# One-time: engine (~5.2 GB, hash-locked) + model (~4.5 GB). Resumable; safe to re-run.
noorvox models install qwen3-tts-1.7b-customvoice --json --quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "install failed: $LASTEXITCODE" }

# Load once (tens of seconds to minutes from an HDD), then reuse the worker for every line.
noorvox workers start --worker qwen --model qwen3-tts-1.7b-customvoice --json --quiet | Out-Null

$r = noorvox synth --worker qwen --model qwen3-tts-1.7b-customvoice --voice aiden --language english `
       --style "Calm, warm documentary narration with a hint of humour. Speak slowly." `
       --text "It started, as so many terrible ideas do, with a single image." `
       --out "E:\AI\NoorVox\Exports\demo\qwen01.wav" --manifest --json --quiet | ConvertFrom-Json
$r.data.seed    # keep it: the same seed + text + settings reproduce this exact WAV on this PC

# Same line again, identical audio:
noorvox synth --worker qwen --model qwen3-tts-1.7b-customvoice --voice aiden --language english `
  --style "Calm, warm documentary narration with a hint of humour. Speak slowly." --seed $r.data.seed `
  --text "It started, as so many terrible ideas do, with a single image." --out "E:\AI\NoorVox\Exports\demo\qwen01b.wav" --json --quiet

noorvox workers stop --worker qwen --json --quiet   # frees ~5 GB of GPU memory
```

Rules for agents:
- Don't pass `--speed` to Qwen (code `unsupported`); put the pace in `--style`. `--style` only works on `qwen3-tts-1.7b-customvoice`.
- Speakers: `aiden`, `ryan` (English); `vivian`, `serena`, `uncle_fu`, `dylan`, `eric` (Chinese); `ono_anna` (Japanese); `sohee` (Korean).
  Every speaker can speak every supported language; pass `--language`.
- **Cloned voices need a human's consent.** `noorvox voices clone … --consent own-voice|speaker-permission` records a statement the
  *user* makes. An agent must never choose a consent value on the user's behalf or clone a voice from audio it found; ask the user.
- Projects: `noorvox projects cast --project DIR --model qwen3-tts-1.7b-customvoice --voice aiden --style "…"` switches a project to Qwen;
  speakers get their own `--style`. Takes record `style` and `seed`.
- `gpu_out_of_memory` (exit 8): other apps hold the GPU. Ask the user to close them, or use a 0.6B model. Don't retry in a loop.

Kokoro on the GPU is a user setting (`noorvox gpu set --kokoro cuda`), not something to toggle per call.

## Reference

Exact flags, fields and exit codes: [CLI_REFERENCE.md](CLI_REFERENCE.md).
