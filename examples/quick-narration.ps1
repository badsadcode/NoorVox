# NoorVox example: headless narration of a few lines with a warm worker, verified output, clean shutdown.
# Usage:  .\examples\quick-narration.ps1 [-Noorvox <path to noorvox.exe>] [-OutDir <folder>]
# Tested with NoorVox 0.1.0. Exits non-zero on any failure.
param(
    [string]$Noorvox = "noorvox",
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) "noorvox-example"),
    [string]$Voice = "bm_george"
)
$ErrorActionPreference = "Stop"

function Invoke-NoorVox {
    $json = & $Noorvox @args --json --quiet
    $code = $LASTEXITCODE
    $r = $json | ConvertFrom-Json
    if ($code -ne 0) { throw "noorvox $($args -join ' ') failed (exit $code, $($r.code)): $($r.message)" }
    return $r.data
}

# 1. Health check. Install the recommended model if missing (needs internet once).
$doctor = Invoke-NoorVox doctor
if (-not ($doctor.models | Where-Object { $_.model -like "kokoro-82m@*" })) {
    Write-Host "Installing kokoro-82m..."
    Invoke-NoorVox models install kokoro-82m | Out-Null
}

# 2. Lines with stable IDs (another agent writes and approves these; NoorVox only speaks them).
$lines = @(
    @{ id = "ep01_s03_p01"; text = "It started, as so many terrible ideas do, with a single image." },
    @{ id = "ep01_s03_p02"; text = "A window, a Vulkan device, and a triangle. Nothing more." },
    @{ id = "ep01_s03_p03"; text = "The first spark of NoorEngine." }
)
New-Item -ItemType Directory -Force $OutDir | Out-Null

# 3. Was a worker already running? Then we must leave it alone afterwards.
$before = (Invoke-NoorVox workers status --worker default).state
if ($before -eq "stopped") { Invoke-NoorVox workers start --model kokoro-82m --idle-timeout 300 | Out-Null }

try {
    $results = foreach ($l in $lines) {
        $out = Join-Path $OutDir "$($l.id).wav"
        $d = Invoke-NoorVox synth --text $l.text --voice $Voice --out $out --overwrite --manifest
        if (-not (Test-Path $d.outputPath)) { throw "missing $($d.outputPath)" }
        [pscustomobject]@{ id = $l.id; path = $d.outputPath; durationMs = $d.durationMs; reusedWorker = $d.worker.reused; text = $l.text }
    }
}
finally {
    if ($before -eq "stopped") { Invoke-NoorVox workers stop --worker default | Out-Null }
}

# 4. Hand-off list for a video agent (actual measured durations).
$total = ($results | Measure-Object durationMs -Sum).Sum
$results | Format-Table id, durationMs, reusedWorker, path -AutoSize
Write-Host ("Total narration: {0:0.00} s in {1} files" -f ($total / 1000), $results.Count)
$results | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $OutDir "narration.json")
