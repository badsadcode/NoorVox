# Brief: from source files to a voiceover and a video

A reusable playbook for coding agents (Codex & friends). The user hands you some files ("read these, make a voiceover and a video
about them") and you deliver a narrated script, verified audio, subtitles and, if a video is wanted, an MP4 built on the exact timings.

> **Paste-able prompt for the user** (edit the bracketed parts):
>
> ```text
> Read E:\Codex\NoorVox\docs\agent-briefs\VOICEOVER_AND_VIDEO.md and follow it.
> Sources: [folder or files]
> Goal: [e.g. a 2-minute explainer / devlog episode / trailer / audiobook chapter] for [audience].
> Voice: [e.g. calm British male narrator / Qwen "aiden" with a warm documentary style / my designed voice "margaret"]
> Video: [yes, 1920x1080 with the screenshots in the sources / audio only]
> Output: [folder, e.g. E:\AI\NoorVox\Exports\my-episode]
> Show me the script before generating audio: [yes/no]
> ```

Everything below is the contract. Exact flags and JSON fields: [CLI_REFERENCE.md](../CLI_REFERENCE.md). Recipes: [AI_INTEGRATION.md](../AI_INTEGRATION.md).

## 0. Ground rules

- **The sources are the truth.** Narrate what they say. Don't invent facts, numbers, names or quotes; if something is unclear or
  missing, say so in the report instead of filling the gap. Keep the user's terminology.
- **Ask before** installing anything (models, engines, ffmpeg), downloading, or spending a long GPU run (Qwen models load for minutes).
  If the user said "do everything", that counts as permission for NoorVox models/engines from its catalog only.
- **Never** create a cloned voice yourself: `voices clone` needs the *user's* consent statement. Using voices that already exist
  (`voices list`) is fine, including the user's cloned and designed voices.
- **Never overwrite** existing files in the output folder or the user's approved takes. Use a new folder or ask.
- **Measured, not estimated:** every duration you report comes from NoorVox's JSON (`durationMs`, `manifest`), never from word counts.
- Use the CLI with `--json --quiet`, check the exit code of every call, and read `code`/`message` on failure. Don't drive the studio UI.
- PowerShell: don't name a helper function `nv`, `ni`, `sc`… (built-in aliases win silently); use `Invoke-NoorVox`.

## 1. Find NoorVox and check what's available

```powershell
$cmd = Get-Command noorvox -ErrorAction SilentlyContinue
$nv = if ($cmd) { $cmd.Source } else { "E:\Apps\NoorVox\noorvox.exe" }    # Tom's portable install; ask the user if neither exists
& $nv doctor --json --quiet | ConvertFrom-Json | Select-Object -ExpandProperty data
& $nv models list --json --quiet | ConvertFrom-Json | ForEach-Object { $_.data.installed } | Select-Object reference, capabilities
```

Pick the engine from what is installed and what the user asked for:

| Need | Model | Voice | Notes |
|---|---|---|---|
| Fast, reliable narration (default) | `kokoro-82m` | e.g. `bm_george`, `bf_emma`, `am_michael`, `af_heart` | CPU, ~6× real time; speed 0.5–2.0; no style control |
| Directed delivery ("calm, warm, a hint of humour") | `qwen3-tts-1.7b-customvoice` | `aiden`, `ryan` (English) | NVIDIA GPU; `--style`; no speed; first load takes minutes |
| The user's own or designed voices | `qwen3-tts-1.7b-base` | ids from `voices list --model qwen3-tts-1.7b-base` (`kind`: clone / designed / synthetic) | NVIDIA GPU |

`voices list --model <ref> --json` shows each voice's `language`, `gender`, `kind` and `description`. Match the script's language.

## 2. Read the sources and write the script

1. List every file and read the relevant ones fully (text, Markdown, docs, code comments, notes). Note images and videos you could
   use as visuals: their paths and what they show.
2. Outline first: the one thing the audience should remember, 3–7 beats, an opening hook and a closing line.
3. Write for the ear, not the eye:
   - short sentences, one idea each; active voice; contractions are fine;
   - **write it as it should read on screen**: the subtitles show exactly this text. Fix how something is *spoken* with
     pronunciation rules in step 3 (`NoorVox` → `Noor Vox`, `0.3.1` → `three point one`), not by spelling it out in the script;
   - no tables, bullet lists, URLs or code in spoken text (describe them instead);
   - length: about **150 words per spoken minute** for Kokoro at 1.0× (a target only; real durations are measured later).
4. Save it as **NoorVox Markdown** in the output folder (`script.md`):

```markdown
# Episode title                      ← a section (an episode or chapter); one per file is typical
## Scene title                       ← a scene
First paragraph = one segment: one to three sentences, spoken as one take.
<!-- visual: screenshot of the editor with the triangle (sources/img/triangle.png) -->

Next paragraph = the next segment.
```

   - `<!-- visual: … -->` comments are never spoken. They become `notes` in the timing manifest and drive the video step, so add one
     to every segment that needs a specific image, naming the source file when there is one.
   - For several characters, use a dialogue CSV instead (`line_id,character,text,notes`; see AI_INTEGRATION.md §7).
5. List names, numbers and terms the engine may mispronounce ("NoorEngine" → "Noor Engine", "GLB" → "G L B", "0.3.1" → "three point
   one") for step 3. Version numbers, abbreviations and product names almost always need a rule.
6. If the user wants to see the script first, stop here and show it with the estimated length.

## 3. Produce the audio with a NoorVox project

```powershell
$out = "E:\AI\NoorVox\Exports\my-episode"                       # the user's output folder (must not exist or be empty)
$p   = Join-Path $out "project"
& $nv projects create --path $p --title "My Episode" --kind documentary --voice bm_george --json --quiet
& $nv projects import --project $p --file (Join-Path $out "script.md") --json --quiet
& $nv pronunciation set --project $p --match NoorEngine --replace "Noor Engine" --json --quiet      # one per term
& $nv pronunciation set --project $p --match "0.3.1" --replace "three point one" --partial --json --quiet   # non-words need --partial
# Qwen instead of Kokoro (only if installed and wanted):
# & $nv projects cast --project $p --model qwen3-tts-1.7b-customvoice --voice aiden --style "Calm, warm documentary narration." --json --quiet
$job = & $nv generate --project $p --approve --json --quiet | ConvertFrom-Json
if ($LASTEXITCODE -eq 11) { & $nv jobs resume --project $p --job $job.data.jobId --json --quiet }  # interrupted: resume, never restart
```

- Kinds: `documentary`, `audiobook`, `narration`, `education`, `dialogue`, `custom`. For dialogue: cast each speaker
  (`projects cast --speaker <id> --voice …`).
- With Qwen, start a persistent worker first so the model loads once (`workers start --worker qwen --model … `), pass
  `--worker qwen --keep-worker` to `generate`, and `workers stop --worker qwen` at the end (frees ~5 GB of GPU memory).
- **Check:** `projects show --project $p --json` → `counts.approved` must equal `segments`. Failed segments are listed with errors;
  fix the text (e.g. nothing pronounceable) and run `generate --policy missing --approve` again.
- **Sanity-check timing:** for each segment, words ÷ (durationMs / 60000) should be roughly 110–200 wpm. Far outside that means a
  truncated or garbled take: regenerate that segment (`generate --segment ID --policy all`) and approve the new take (`takes approve`).

## 4. Assemble: one WAV, subtitles, timing manifest

```powershell
$asm = & $nv export assemble --project $p --rate 48000 --normalize --out (Join-Path $out "audio") --json --quiet | ConvertFrom-Json
$manifestPath = $asm.data.sections[0].manifest      # for multiple sections, one manifest each
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
```

The manifest (`docs/schemas/assembly-manifest.schema.json`) gives `audio.file`, `audio.durationMs`, `subtitles.srt`/`.vtt` and, for
every segment, `startMs`, `endMs`, `text`, `speaker` and `notes` (your visual cues). Verify before going on:

- the WAV, SRT and VTT exist; segments are in order and `endMs` ≤ `audio.durationMs`;
- `missing` and `stale` are empty.

Never re-time anything from text length: the manifest is exact to the segment.

## 5. The video (only if asked)

NoorVox makes audio, subtitles and timings; the picture is made with **ffmpeg** using those timings. If `ffmpeg` isn't installed, ask
the user (Windows: `winget install --id Gyan.FFmpeg`), or deliver audio + subtitles + `shotlist.md` and say the video step is pending.

`tools\make-video.ps1` in the NoorVox repo does this from the manifest:

```powershell
& E:\Codex\NoorVox\tools\make-video.ps1 -Manifest $manifestPath -Images (Join-Path $out "visuals") -Out (Join-Path $out "video.mp4") `
    -Width 1920 -Height 1080 -Subtitles burn        # or: -Subtitles soft (toggleable track) / none
```

How it picks a picture for each segment, in order:
1. a file in `-Images` named after the segment id (`ep01.s02.p003.png`);
2. an image path mentioned in that segment's `<!-- visual: … -->` note, if it exists;
3. otherwise the previous segment's image, or a title card (theme-coloured background with the section/scene title) for the first one.

Each image is shown from the segment's `startMs` to the next segment's `startMs` (pauses included), scaled to fit with letterboxing.
Prepare `visuals/` yourself: copy the relevant source images, renamed to segment ids; screenshots or diagrams the user provided
are better than anything generated. Don't fabricate screenshots of real products.

Verify the result with `ffprobe`: duration within 100 ms of `audio.durationMs`, one video and one audio stream (plus a subtitle
stream for `soft`).

## 6. Deliver and report

Output folder layout:

```text
<out>\script.md                  the narration as written
<out>\project\                   the NoorVox project (re-generate, swap voices, fix pronunciation later)
<out>\audio\<section>.wav        48 kHz, peak-normalised
<out>\audio\<section>.srt / .vtt / .manifest.json
<out>\visuals\                   images used, named by segment id (if video)
<out>\video.mp4                  (if video)
<out>\report.md
```

`report.md` (and your final message) says, briefly:
- what the piece covers, which sources were used, and anything left out or uncertain;
- model, voice(s), style, pronunciation rules;
- **measured** total duration and segment count; how many segments were regenerated and why;
- video: resolution, which segments reuse an image or got a title card (candidates for better visuals);
- anything not done (e.g. video pending ffmpeg) and the exact command to finish it.
