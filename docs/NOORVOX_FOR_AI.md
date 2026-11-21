# NoorVox for AI assistants

**NoorVox** is a local, offline voiceover studio for Windows by Bad Ideas Works. It turns text into narrated WAV files with measured
timings, subtitles and provenance. It has a desktop studio for people and a command-line tool, `noorvox.exe`, for agents.
**As an AI, always use the CLI.** Never click through the studio UI.

This file is self-contained. For exhaustive detail see `docs/CLI_REFERENCE.md` (every flag and field) and `docs/AI_INTEGRATION.md`
(longer recipes) in the NoorVox repository. For "read these files and make a voiceover and a video", follow
`docs/agent-briefs/VOICEOVER_AND_VIDEO.md`.

---

## 1. Where it is

```powershell
$cmd = Get-Command noorvox -ErrorAction SilentlyContinue
$noorvox = if ($cmd) { $cmd.Source } else { "E:\Apps\NoorVox\noorvox.exe" }   # Tom's portable install
& $noorvox version --json
```

If neither exists, ask the user where NoorVox is installed. Don't search the whole disk.

- Tom's install is **portable**: settings live in `E:\Apps\NoorVox\data\settings.json`. Models, cache, temp audio, projects, exports and
  recordings live under `E:\AI\NoorVox\…` (`noorvox storage show --json` prints the real paths).
- NoorVox works **offline**. Only installing models or engines downloads anything, and only when asked.

## 2. Golden rules

1. **Always pass `--json --quiet`**, check the process exit code, and read the single JSON envelope on stdout:
   `{ "success": true|false, "code": "…", "message": "…", "data": {…}, "warnings": [] }`. Progress goes to stderr; ignore it.
2. **Only use controls the model has.** Each model declares `capabilities` (see §4). Anything else fails with `code: "unsupported"`.
3. **Durations are measured, never estimated.** Report `durationMs` from the JSON, not word counts.
4. **Never overwrite** existing audio unless the user asked: `synth` refuses to overwrite without `--overwrite`, and approvals are never replaced automatically.
5. **Ask before** installing models or engines (GB-sized downloads), changing settings or storage paths, or deleting anything.
6. **Cloned voices need the user's consent.** Never run `voices clone` with a consent value the user didn't give you, and never clone a
   voice from audio you found. Using voices that already exist is fine.
7. **Don't kill processes.** Stop NoorVox workers with `noorvox workers stop`, never by process name.
8. PowerShell tip: don't name a helper function `nv` (it's a built-in alias). Use something like `Invoke-NoorVox`.

```powershell
function Invoke-NoorVox {
    $r = & $noorvox @args --json --quiet | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "noorvox $($args[0]) $($args[1]) failed (exit $LASTEXITCODE, $($r.code)): $($r.message)" }
    $r.data
}
```

## 3. Discover what's installed (do this first)

```powershell
Invoke-NoorVox doctor                                  # storage, models, voices, workers; .issues lists problems
(Invoke-NoorVox models list).installed | Select-Object reference, displayName, capabilities
(Invoke-NoorVox voices list --model kokoro-82m).voices | Select-Object id, languageName, gender, kind, displayName
(Invoke-NoorVox voices list --model qwen3-tts-1.7b-base).voices | Select-Object id, kind, displayName, description
Invoke-NoorVox engines list                            # Qwen engine / ONNX Runtime CUDA pack
Invoke-NoorVox gpu status                              # Kokoro provider, GPU seen by the engine
Invoke-NoorVox recordings list                         # the user's Voice lab recordings
```

## 4. Models and what they can do

| Model reference | Runs on | Voices | Controls (capabilities) |
|---|---|---|---|
| `kokoro-82m` (default, recommended) | CPU, optional NVIDIA GPU | 54 voice files (`af_heart`, `bm_george`, `bf_emma`, `am_michael`, …) plus blends | `--speed` 0.5–2.0. No style, seed or language option (the voice's prefix decides the language). |
| `qwen3-tts-1.7b-customvoice` | NVIDIA GPU | 9 speakers: `aiden`, `ryan` (English); `vivian`, `serena`, `uncle_fu`, `dylan`, `eric` (Chinese); `ono_anna` (Japanese); `sohee` (Korean). All speak all 10 languages. | `--style` (written delivery directions), `--seed`, `--language`. **No speed.** |
| `qwen3-tts-1.7b-base` | NVIDIA GPU | the user's voice profiles: `clone`, `designed`, `synthetic` | `--seed`, `--language`. No style, no speed. |
| `qwen3-tts-1.7b-voicedesign` | NVIDIA GPU | one pseudo-voice `designed` | `--style` is **required** and *is* the voice description; `--seed`, `--language`. |
| `chatterbox-english` | NVIDIA GPU | built-in `default` + the user's voice profiles (clone / designed / saved; no transcript needed) | `--emotion` 0.25–2.0 (flat → dramatic), `--pace` 0.1–1.0 (lower = a little slower), `--seed`. English only. Output is watermarked. |
| `chatterbox-multilingual` | NVIDIA GPU | same | as English plus `--language` (23: `english`, `german`, `french`, `spanish`, `italian`, `polish`, `japanese`, `chinese`, …) |
| `chatterbox-turbo` | NVIDIA GPU | same | `--seed` only (no emotion/pace: the model ignores them). About 2× faster than real time. Watermarked. |
| `qwen3-tts-0.6b-*` | NVIDIA GPU | as the 1.7B models | smaller download (not installed or measured on Tom's PC yet); 0.6B CustomVoice has no `--style` |

- Qwen languages: `auto`, `english`, `chinese`, `japanese`, `korean`, `german`, `french`, `russian`, `portuguese`, `spanish`, `italian`.
- Kokoro voice prefixes: `a` American English, `b` British English, `j` Japanese, `z` Mandarin, `e` Spanish, `f` French, `h` Hindi,
  `i` Italian, `p` Brazilian Portuguese; second letter `f`/`m` = female/male.
- Speed on Tom's PC (RTX 3080): Kokoro CPU about 6× faster than real time (about 15× on CUDA). Qwen about 2.5× once loaded.
  Chatterbox English/Multilingual about real time, Turbo about 2×.
  **A Qwen model takes 1–6 minutes to load from the hard drive**, so load it once with a persistent worker (§6).
- Output is always a 24 kHz mono 16-bit WAV (assembly can resample to 44.1/48 kHz).

## 5. One line

```powershell
# Kokoro
$r = Invoke-NoorVox synth --model kokoro-82m --voice bm_george --speed 1.0 `
       --text "It started, as so many terrible ideas do, with a single image." `
       --out "E:\AI\NoorVox\Exports\demo\line01.wav" --manifest
$r.outputPath; $r.durationMs          # absolute path; measured duration

# Qwen with delivery directions and a reproducible seed
$q = Invoke-NoorVox synth --model qwen3-tts-1.7b-customvoice --voice aiden --language english `
       --style "Calm, warm documentary narration with a hint of humour. Speak slowly." `
       --text "It started, as so many terrible ideas do, with a single image." --out "E:\AI\NoorVox\Exports\demo\qwen01.wav"
$q.seed                               # same seed + text + settings on this PC = byte-identical WAV
```

- Input text: `--text "…"`, `--text-file FILE` or `--stdin`. Output: `--out FILE.wav` (default: the exports folder).
- `--manifest` also writes `<out>.noorvox.json` (text hash, voice, model sha256, timings: provenance).
- The JSON says how it went: `data.timings.realTimeFactor`, `data.backend.executionProvider` (`CPU`/`CUDA`) and `data.voiceKind`.

## 6. Many lines: keep a worker warm

Without a worker, each `synth` loads the model, which takes seconds for Kokoro and minutes for Qwen. For more than one line:

```powershell
Invoke-NoorVox workers start --worker qwen --model qwen3-tts-1.7b-customvoice     # loads once (minutes for Qwen)
foreach ($line in $lines) {
    Invoke-NoorVox synth --worker qwen --model qwen3-tts-1.7b-customvoice --voice aiden --style "…" --text $line.text --out $line.out
}
Invoke-NoorVox workers stop --worker qwen       # frees ~5 GB of GPU memory; workers also exit by themselves after 15 min idle
```

`workers list` shows every NoorVox worker, its state, loaded model and measured memory, including GPU memory for Qwen.

## 7. Projects: anything longer than a few lines

A project gives stable segment ids, multiple takes per line, approvals, resumable jobs, subtitles and a timing manifest.

**Script format** (Markdown): `#` = section (episode/chapter), `##` = scene, each paragraph = one segment (1–3 sentences).
`<!-- comments -->` are never spoken and attach to the **next** paragraph (use them for visual cues and notes).
Write text the way it should *read* (subtitles show it exactly); fix how words are *spoken* with pronunciation rules.

```markdown
# Episode 1 – The Terrible Idea
## The triangle
<!-- visual: empty editor window -->
It started, as so many terrible ideas do, with a single image.

A window, a Vulkan device, and a triangle. Nothing more.
```

Dialogue/game lines can be a CSV instead: `line_id,character,text[,set,notes]`.

```powershell
$p = "E:\AI\NoorVox\Projects\My Episode"
Invoke-NoorVox projects create --path $p --title "My Episode" --kind documentary --voice bm_george      # kinds: documentary, audiobook, narration, education, dialogue, custom
Invoke-NoorVox projects import --project $p --file "E:\…\episode.md"
Invoke-NoorVox pronunciation set --project $p --match NoorEngine --replace "Noor Engine"
Invoke-NoorVox pronunciation set --project $p --match "0.3.1" --replace "three point one" --partial      # non-words need --partial
# optional: a different model/voice/style for the whole project or one character
Invoke-NoorVox projects cast --project $p --model qwen3-tts-1.7b-customvoice --voice aiden --style "Warm documentary narration."
Invoke-NoorVox projects cast --project $p --speaker vendor --voice ryan --style "Gruff market trader."   # dialogue projects

$job = & $noorvox generate --project $p --approve --json --quiet | ConvertFrom-Json
if ($LASTEXITCODE -eq 11) { Invoke-NoorVox jobs resume --project $p --job $job.data.jobId }               # interrupted: resume, don't restart
$s = Invoke-NoorVox projects show --project $p
"$($s.counts.approved) of $($s.segments) segments approved, $($s.approvedDurationMs) ms"
```

- `generate` JSON: on success the job is `data.job` (`jobId`, `state`, `total`, `done`, `failed`, …); when interrupted (exit 11) `data`
  is `{ jobId, resume }`, where `resume` is the exact command to continue.
- `generate` options: `--section ID`, `--scene ID`, `--segment ID[,ID…]`, `--policy missing|all` (`missing` reuses matching takes),
  `--approve` (approves new takes only where nothing is approved yet), `--worker ID --keep-worker`.
- Segment status: `missing`, `generated`, `approved`, `stale` (text/voice changed after approval; regenerate, then let the user approve).
- Takes: `takes list --project $p --segment ID`, `takes approve --project $p --segment ID --take ID` (or `--latest`), `takes unapprove`.
- Edit structure: `projects split --segment ID --before "text"`, `projects merge --segment ID`, `projects validate`.

**Export:**

```powershell
# One WAV per section + SRT/VTT subtitles + a timing manifest (segment startMs/endMs, text, speaker, notes) for video work
$a = Invoke-NoorVox export assemble --project $p --rate 48000 --normalize --out "$p\exports\episode"
$a.sections | Select-Object audio, srt, vtt, manifest, durationMs, missing, stale

# Or one WAV per segment, named by segment/line id, plus clips.json (game dialogue, editors)
Invoke-NoorVox export clips --project $p --out "$p\exports\clips"
```

**Video:** NoorVox makes no video itself. `tools\make-video.ps1 -Manifest <manifest.json> -Out video.mp4` (in the NoorVox repo) builds an
MP4 with ffmpeg: one picture per segment from the visual cues, subtitles burned in or as a track, timed by the manifest.

## 8. Voices: blends, clones, designed voices

```powershell
# Kokoro blend: 2–4 voices mixed into a new, immutable voice
Invoke-NoorVox voices blend --name "Documentary Narrator" --mix bm_george:0.6,bm_fable:0.4            # → voice id bm_documentarynarrator

# Designed voice (no real person, no consent needed): describe it, NoorVox keeps a sample as a voice for the Base model
Invoke-NoorVox voices design --name "Margaret" --description "A warm British woman in her fifties, calm audiobook narrator." --language english
Invoke-NoorVox synth --model qwen3-tts-1.7b-base --voice margaret --text "…" --out "…\margaret.wav"

# Cloned voice: ONLY with the user's explicit consent statement (own-voice | speaker-permission), from a 2–60 s WAV + exact transcript
Invoke-NoorVox voices clone --name "Tom calm" --reference "E:\AI\NoorVox\Recordings\tom-calm.wav" --transcript "Exact words spoken." --consent own-voice --language english

Invoke-NoorVox voices remove-profile --voice margaret        # removes a clone/designed/saved voice (takes made with it stay)
```

- Each `voices design` run invents a new voice; keep the `seed` from the JSON to reproduce it. A designed voice copies the sample's
  mood, so describe the delivery you want too.
- The user's microphone recordings (made in the studio's Voice lab) are listed by `recordings list`, with paths and transcripts.

## 9. Engines, GPU and installing (ask first)

```powershell
Invoke-NoorVox models install kokoro-82m                       # ~390 MB
Invoke-NoorVox models install qwen3-tts-1.7b-customvoice       # ~4.5 GB + the qwen-tts engine (~5.2 GB, once) if missing
Invoke-NoorVox models install chatterbox-turbo                 # ~3 GB + the chatterbox engine (~5.5 GB, once) if missing
Invoke-NoorVox engines install onnxruntime-cuda                # ~330 MB: lets Kokoro use the GPU
Invoke-NoorVox gpu set --kokoro cuda                           # user setting; falls back to CPU (with a reason) if unavailable
```

Installs are resumable and sha256-verified, and the Qwen engine's Python packages are hash-locked. Never run `pip install` for NoorVox yourself.

## 10. When something fails

| Exit | `code` | What to do |
|---:|---|---|
| 2 | `invalid_arguments`, `unsupported` | Fix the command; `unsupported` = that model lacks the control (e.g. `--speed` on Qwen: use `--style`). |
| 4 | `model_not_installed`, `voice_not_found`, `engine_not_installed` | Check `models list` / `voices list --model …`; ask before installing. |
| 5 | `invalid_input` | Empty or unusable text. |
| 6 | `worker_busy`, `worker_already_running` | Wait, or use another `--worker` id. Don't kill anything. |
| 7 | `model_load_failed`, `engine_broken`, `gpu_required`, `worker_error` | Read `message`; `gpu_required` = no usable NVIDIA GPU (use Kokoro). |
| 8 | `synthesis_failed`, `gpu_out_of_memory` | Text with nothing pronounceable fails; for GPU memory, ask the user to close GPU-heavy apps. Don't retry in a loop. |
| 9 | `output_exists`, `io_error` | Choose another `--out` (or `--overwrite` only if the user agreed). |
| 10 | `download_failed`, `verification_failed` | Re-run the same install: it resumes. |
| 11 | `cancelled` | A generation was interrupted: `jobs resume`. |

`noorvox <command> --help` prints usage. `noorvox diagnostics bundle` writes a support zip (no scripts or audio) if the user wants help.

## 11. What NoorVox doesn't do

No cloud, accounts or telemetry. No video rendering (use the manifest with ffmpeg). No MP3 export (WAV only), no LUFS loudness
normalisation, no word-level timings (subtitles are per segment), no Polish voices. No speed control on Qwen or Chatterbox, no style control on Kokoro or Chatterbox, and no emotion/pace on Chatterbox Turbo.
