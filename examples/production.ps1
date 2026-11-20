# NoorVox example: one project per template, generated headlessly, with an interruption + resume, then clips exported.
# Usage:  .\examples\production.ps1 [-Noorvox <noorvox.exe>] [-WorkDir <folder for the three projects>]
# Tested with NoorVox 0.1.0-dev (Phase 3). Requires an installed model (noorvox models install kokoro-82m).
param(
    [string]$Noorvox = "noorvox",
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) "noorvox-production-example")
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Invoke-NoorVox {
    $out = & $Noorvox @args --json --quiet
    $code = $LASTEXITCODE
    $r = $out | ConvertFrom-Json
    if ($code -ne 0) { throw "noorvox $($args -join ' ') -> exit $code ($($r.code)): $($r.message)" }
    $r.data
}

if (Test-Path $WorkDir) { throw "$WorkDir already exists. Pass a new -WorkDir." }
New-Item -ItemType Directory $WorkDir | Out-Null
$report = @()

# --- Documentary: Episode -> Scene -> Segment, British narrator, auto-approve --------------------------------
$doc = Join-Path $WorkDir "NoorEngine Documentary"
Invoke-NoorVox projects create --path $doc --title "NoorEngine Documentary" --kind documentary --voice bm_george | Out-Null
Invoke-NoorVox projects import --project $doc --file (Join-Path $here "projects\documentary.md") | Out-Null
$g = Invoke-NoorVox generate --project $doc --approve
$c = Invoke-NoorVox export clips --project $doc
$report += [pscustomobject]@{ project = "documentary"; generated = $g.job.generated; clips = $c.clips.Count; seconds = [math]::Round(($c.clips | Measure-Object durationMs -Sum).Sum / 1000, 1); out = $c.outputDir }

# --- Audiobook: a partial first run (2 paragraphs), then the chapter job reuses those takes instead of regenerating ---
$book = Join-Path $WorkDir "The Lighthouse Keeper"
Invoke-NoorVox projects create --path $book --title "The Lighthouse Keeper" --kind audiobook --voice af_heart | Out-Null
Invoke-NoorVox projects import --project $book --file (Join-Path $here "projects\audiobook.md") | Out-Null
Invoke-NoorVox generate --project $book --segment "ch01.s01.p001,ch01.s01.p002" --approve | Out-Null
$full = Invoke-NoorVox generate --project $book --section ch01 --approve
if ($full.job.reused -ne 2) { throw "expected the 2 finished paragraphs to be reused, got $($full.job.reused)" }
Invoke-NoorVox generate --project $book --section ch02 --approve | Out-Null
$c = Invoke-NoorVox export clips --project $book --section ch02
$report += [pscustomobject]@{ project = "audiobook"; generated = $full.job.generated; clips = $c.clips.Count; seconds = [math]::Round(($c.clips | Measure-Object durationMs -Sum).Sum / 1000, 1); out = $c.outputDir }

# --- Game dialogue: line ids from CSV, one voice per character, files named by line id ------------------------
$dlg = Join-Path $WorkDir "Market Dialogue"
Invoke-NoorVox projects create --path $dlg --title "Market Dialogue" --kind dialogue | Out-Null
Invoke-NoorVox projects import --project $dlg --file (Join-Path $here "projects\dialogue.csv") | Out-Null
Invoke-NoorVox projects cast --project $dlg --speaker vendor --voice am_michael | Out-Null
Invoke-NoorVox projects cast --project $dlg --speaker alden --voice bm_fable | Out-Null
$g = Invoke-NoorVox generate --project $dlg --approve
$c = Invoke-NoorVox export clips --project $dlg
$names = ($c.clips | ForEach-Object { Split-Path -Leaf $_.file }) -join ", "
if ($names -notmatch "npc_vendor_greeting_001.wav") { throw "dialogue clips are not named by line id: $names" }
$report += [pscustomobject]@{ project = "dialogue"; generated = $g.job.generated; clips = $c.clips.Count; seconds = [math]::Round(($c.clips | Measure-Object durationMs -Sum).Sum / 1000, 1); out = $c.outputDir }

$report | Format-Table -AutoSize
$running = (Invoke-NoorVox workers list).workers.Count
Write-Host "Workers still running: $running"
if ($running -ne 0) { throw "a worker was left running" }
