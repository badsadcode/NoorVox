# NoorVox example: what a Codex-style agent does to turn a Markdown script into a finished, verified episode,
# with no UI: create project → import → generate (resume once if interrupted) → assemble 48 kHz + SRT/VTT + manifest
# → verify every path and every timing in the manifest → print the manifest for the video agent.
#
# Usage: .\examples\agent-episode.ps1 -Script <episode.md> -ProjectDir <new project folder> [-Voice bm_george] [-Noorvox noorvox]
# Exit code 0 = verified episode. Tested with NoorVox 0.1.0.
param(
    [Parameter(Mandatory)] [string]$Script,
    [Parameter(Mandatory)] [string]$ProjectDir,
    [string]$Voice = "bm_george",
    [string]$Title = "",
    [string]$Noorvox = "noorvox"
)
$ErrorActionPreference = "Stop"

function Invoke-NoorVox {
    $out = & $Noorvox @args --json --quiet
    $code = $LASTEXITCODE
    $r = $out | ConvertFrom-Json
    [pscustomobject]@{ Code = $code; Ok = ($code -eq 0); Result = $r; Data = $r.data }
}
function Require($r, $what) { if (-not $r.Ok) { throw "$what failed (exit $($r.Code), $($r.Result.code)): $($r.Result.message)" }; $r.Data }

# 1. Environment: a model and the requested voice must exist (install only if the user allows downloads).
$doctor = Require (Invoke-NoorVox doctor) "doctor"
if (-not ($doctor.models | Where-Object { $_.model -like "kokoro-82m@*" })) { throw "No voice model installed. Ask the user, then run: noorvox models install kokoro-82m" }
$voices = (Require (Invoke-NoorVox voices list) "voices list").voices.id
if ($voices -notcontains $Voice) { throw "Voice '$Voice' is not installed. Available: $($voices -join ', ')" }

# 2. Project + import (NoorVox speaks the script exactly as written; it never rewrites it).
if (-not $Title) { $Title = (Get-Content $Script -TotalCount 1) -replace '^#\s*', '' }
if (-not (Test-Path (Join-Path $ProjectDir "noorvox.project.json"))) {
    Require (Invoke-NoorVox projects create --path $ProjectDir --title $Title --kind documentary --voice $Voice) "projects create" | Out-Null
}
$import = Require (Invoke-NoorVox projects import --project $ProjectDir --file $Script) "projects import"
$section = $import.sections[0].sectionId
Write-Host "Imported $section with $($import.sections[0].segments) segments."

# 3. Generate with auto-approve; on interruption or partial failure, resume the same job once.
$gen = Invoke-NoorVox generate --project $ProjectDir --section $section --approve
if (-not $gen.Ok) {
    $jobId = if ($gen.Data.jobId) { $gen.Data.jobId } else { $gen.Data.job.jobId }
    if (-not $jobId) { throw "generate failed (exit $($gen.Code)): $($gen.Result.message)" }
    Write-Host "Generation stopped ($($gen.Result.code)); resuming $jobId ..."
    Require (Invoke-NoorVox jobs resume --project $ProjectDir --job $jobId) "jobs resume" | Out-Null
}
$show = Require (Invoke-NoorVox projects show --project $ProjectDir) "projects show"
$sec = $show.sections | Where-Object { $_.sectionId -eq $section }
if ($sec.counts.approved -ne $sec.segments) { throw "Not every segment is approved: $($sec.counts | ConvertTo-Json -Compress)" }

# 4. Assemble for video: 48 kHz, peak −1 dBFS, SRT/VTT, timing manifest.
$out = Join-Path $ProjectDir "exports\episode_$section"
$asm = Require (Invoke-NoorVox export assemble --project $ProjectDir --section $section --rate 48000 --normalize --out $out --overwrite) "export assemble"
$manifestPath = $asm.sections[0].manifest

# 5. Verify the manifest against reality before handing it to anyone.
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
foreach ($f in @($m.audio.file, $m.subtitles.srt, $m.subtitles.vtt)) { if (-not (Test-Path $f)) { throw "Missing output: $f" } }
$wav = Get-Item $m.audio.file
$expectedBytes = 44 + $m.audio.samples * 2
if ($wav.Length -ne $expectedBytes) { throw "WAV size $($wav.Length) != 44 + 2 x $($m.audio.samples) samples" }
if ([math]::Abs($m.audio.samples / $m.audio.sampleRate * 1000 - $m.audio.durationMs) -gt 1) { throw "durationMs does not match samples" }
$prevEnd = 0
foreach ($s in $m.segments) {
    if ($s.startMs -lt $prevEnd -or $s.endMs -le $s.startMs -or $s.endMs -gt $m.audio.durationMs) { throw "Bad timing for $($s.segmentId): $($s.startMs)-$($s.endMs)" }
    $prevEnd = $s.endMs
}
if ($m.missing.Count -gt 0 -or $m.stale.Count -gt 0) { throw "Manifest reports missing/stale segments: $($m.missing) $($m.stale)" }
$cues = (Select-String -Path $m.subtitles.srt -Pattern ' --> ').Count
if ($cues -ne $m.segments.Count) { throw "SRT has $cues cues for $($m.segments.Count) segments" }

Write-Host ("Verified: {0} segments, {1:0.0} s at {2} Hz, {3} cues, {4} visual notes." -f $m.segments.Count, ($m.audio.durationMs / 1000), $m.audio.sampleRate, $cues, @($m.segments | Where-Object { $_.notes }).Count)
Write-Host "Manifest for the video agent: $manifestPath"
$m.segments | Select-Object index, segmentId, startMs, endMs, notes | Format-Table -AutoSize
