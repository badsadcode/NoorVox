# Qwen3-TTS from the command line (NoorVox 0.3.0, NVIDIA GPU required).
# Installs the engine + 1.7B CustomVoice if needed, keeps one worker warm, renders three directed lines, proves a seed
# reproduces a take, then frees the GPU. See docs/AI_INTEGRATION.md §9.
#   .\examples\qwen-voices.ps1 -Noorvox <path to noorvox.exe> -OutDir E:\AI\NoorVox\Exports\qwen-demo
param(
    [string]$Noorvox = "noorvox",
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = "Stop"
$model = "qwen3-tts-1.7b-customvoice"

function Invoke-Nv([string[]]$NvArgs) {
    $json = & $Noorvox @NvArgs --json --quiet | Out-String
    $r = $json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "noorvox $($NvArgs[0]) failed ($LASTEXITCODE, $($r.code)): $($r.message)" }
    return $r.data
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Invoke-Nv @("models", "install", $model) | Out-Null           # engine (~5.2 GB) + model (~4.5 GB) on first run; no-op after
Invoke-Nv @("workers", "start", "--worker", "qwen", "--model", $model) | Out-Null   # one load for all lines

$lines = @(
    @{ voice = "aiden"; style = "Calm, warm documentary narration with a hint of humour. Speak slowly."; text = "It started, as so many terrible ideas do, with a single image." },
    @{ voice = "ryan";  style = "Excited trailer voice, building energy.";                               text = "A window. A Vulkan device. And a triangle." },
    @{ voice = "aiden"; style = "Quiet, almost whispering, as if sharing a secret.";                       text = "Nothing more. No terrain, no trees, no Alden." }
)
try {
    $i = 0
    foreach ($l in $lines) {
        $i++
        $d = Invoke-Nv @("synth", "--worker", "qwen", "--model", $model, "--voice", $l.voice, "--language", "english",
            "--style", $l.style, "--text", $l.text, "--out", (Join-Path $OutDir ("line{0:00}.wav" -f $i)), "--overwrite", "--manifest")
        "{0}: {1:0.00} s, seed {2}, generated in {3:0.0} s" -f (Split-Path $d.outputPath -Leaf), $d.durationSeconds, $d.seed, ($d.timings.synthesisMs / 1000)
        if ($i -eq 1) { $first = $d }
    }
    # Reproduce line 1 from its seed: the audio must be byte-identical.
    $again = Invoke-Nv @("synth", "--worker", "qwen", "--model", $model, "--voice", "aiden", "--language", "english",
        "--style", $lines[0].style, "--text", $lines[0].text, "--seed", "$($first.seed)", "--out", (Join-Path $OutDir "line01-again.wav"), "--overwrite")
    $same = (Get-FileHash $first.outputPath).Hash -eq (Get-FileHash $again.outputPath).Hash
    "Seed $($first.seed) reproduces line01: $same"
}
finally {
    Invoke-Nv @("workers", "stop", "--worker", "qwen") | Out-Null   # frees ~5 GB of GPU memory
}
