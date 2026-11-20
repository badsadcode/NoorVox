# NoorVox for AI agents (Codex & friends)

You can produce narration with NoorVox **without any UI automation**. Everything below runs headless and offline once a model is
installed, and every command block in this file has been run against v0.1.0-dev. `examples/quick-narration.ps1` is the same flow
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
| 11 `cancelled` | Nothing was published to `--out` |

Retries are safe. A failed `synth` never leaves a partial file at `--out`, and installs resume and verify.

## 6. Lifecycle and cleanup

```powershell
noorvox workers list --json      # what's running, PID, model, memory, current job
noorvox workers stop --all --json
```

Note for sandboxed agents: if your command runner kills its whole process tree or job when a command ends, a persistent worker
may be terminated with it. Persistent workers try to leave the caller's job, but some sandboxes forbid that.
One-shot `synth` always works.

## 7. Handing audio to a video agent

Pass along the list of `{id, path, durationMs, text}` you collected (or the `.noorvox.json` manifests). Don't re-derive durations from
the text: use `durationMs`. Episode-level assembly, subtitles (SRT/VTT) and timing manifests are planned for the project and export
commands. They don't exist in v0.1.

## Reference

Exact flags, fields and exit codes: [CLI_REFERENCE.md](CLI_REFERENCE.md).
