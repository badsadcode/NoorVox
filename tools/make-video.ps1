# Builds an MP4 from a NoorVox assembly manifest: one picture per segment, shown from its startMs to the next segment's startMs,
# over the assembled WAV, with subtitles burned in, as a soft track, or none. See docs/agent-briefs/VOICEOVER_AND_VIDEO.md §5.
#
#   .\tools\make-video.ps1 -Manifest <section.manifest.json> -Out <video.mp4> [-Images <folder>] [-Width 1920] [-Height 1080]
#                          [-Subtitles burn|soft|none] [-Fps 30] [-Background "#12101c"] [-FFmpeg ffmpeg]
#
# Picture per segment: <Images>\<segmentId>.(png|jpg|jpeg|webp), else an existing image path named in the segment's notes
# (<!-- visual: … -->), else the previous segment's picture, else a title card with the section/scene title.
# Needs ffmpeg (and ffprobe for the final check). Never overwrites -Out.
param(
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$Out,
    [string]$Images = "",
    [int]$Width = 1920,
    [int]$Height = 1080,
    [ValidateSet("burn", "soft", "none")][string]$Subtitles = "burn",
    [int]$Fps = 30,
    [string]$Background = "#12101c",
    [string]$FFmpeg = "ffmpeg"
)
$ErrorActionPreference = "Stop"
# Native tools write progress to stderr; in Windows PowerShell 5.1 a redirected stderr line becomes an error record, so the
# ffmpeg calls below run quietly (-nostats -loglevel error) and are judged by their exit codes only.
function Invoke-Native([string]$exe, [string[]]$arguments) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { $text = & $exe @arguments 2>&1 | Out-String } finally { $ErrorActionPreference = $prev }
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text.Trim() }
}
if (-not (Get-Command $FFmpeg -ErrorAction SilentlyContinue)) { throw "ffmpeg not found. Install it (winget install --id Gyan.FFmpeg) or pass -FFmpeg <path>." }
$Out = [IO.Path]::GetFullPath($Out)
if (Test-Path $Out) { throw "$Out already exists; choose another name (this script never overwrites)." }
$manifestPath = (Resolve-Path $Manifest).Path
$m = Get-Content $manifestPath -Raw | ConvertFrom-Json
$baseDir = Split-Path $manifestPath
function Resolve-Rel([string]$p) { if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $baseDir $p } }
$audio = Resolve-Rel $m.audio.file
if (-not (Test-Path $audio)) { throw "Audio not found: $audio" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("noorvox-video-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$exts = ".png", ".jpg", ".jpeg", ".webp"

function Find-SegmentImage($seg) {
    if ($Images) {
        foreach ($e in $exts) { $f = Join-Path $Images ($seg.segmentId + $e); if (Test-Path $f) { return (Resolve-Path $f).Path } }
    }
    if ($seg.notes) {
        foreach ($match in [regex]::Matches([string]$seg.notes, '[A-Za-z]:[\\/][^\s"''<>|]+?\.(png|jpe?g|webp)|[\w.\-\\/]+?\.(png|jpe?g|webp)')) {
            foreach ($cand in @($match.Value, (Join-Path $baseDir $match.Value), $(if ($Images) { Join-Path $Images $match.Value }))) {
                if ($cand -and (Test-Path $cand)) { return (Resolve-Path $cand).Path }
            }
        }
    }
    return $null
}

$font = @("$env:WINDIR\Fonts\segoeuib.ttf", "$env:WINDIR\Fonts\segoeui.ttf", "$env:WINDIR\Fonts\arial.ttf") | Where-Object { Test-Path $_ } | Select-Object -First 1
function New-TitleCard([string]$title, [int]$n) {
    $txt = Join-Path $work "card$n.txt"
    [IO.File]::WriteAllText($txt, $title, [Text.UTF8Encoding]::new($false))      # text from a file: no filter escaping problems
    $png = Join-Path $work "card$n.png"
    Push-Location $work
    try {
        $fontArg = if ($font) { "fontfile='" + ($font -replace '\\', '/' -replace ':', '\:') + "':" } else { "" }
        $r = Invoke-Native $FFmpeg @("-hide_banner", "-nostats", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=$($Background):s=$($Width)x$($Height)",
            "-frames:v", "1", "-vf", "drawtext=$($fontArg)textfile=card$n.txt:fontcolor=white:fontsize=$([int]($Height / 14)):x=(w-text_w)/2:y=(h-text_h)/2", $png)
        if ($r.Code -ne 0) { throw "ffmpeg failed to draw a title card: $($r.Text)" }
    } finally { Pop-Location }
    return $png
}

# Picture timeline from the manifest (exact segment timings; pauses stay on the previous picture).
$segs = @($m.segments | Sort-Object startMs)
if ($segs.Count -eq 0) { throw "The manifest has no segments." }
$lines = [Collections.Generic.List[string]]::new()
$report = [Collections.Generic.List[object]]::new()
$current = $null
$cards = 0
for ($i = 0; $i -lt $segs.Count; $i++) {
    $seg = $segs[$i]
    $start = if ($i -eq 0) { 0 } else { [double]$seg.startMs }
    $end = if ($i + 1 -lt $segs.Count) { [double]$segs[$i + 1].startMs } else { [double]$m.audio.durationMs }
    $img = Find-SegmentImage $seg
    $source = "image"
    if (-not $img) {
        if ($current) { $img = $current; $source = "previous" }
        else { $cards++; $img = New-TitleCard ($(if ($seg.sceneTitle) { $seg.sceneTitle } elseif ($m.section.title) { $m.section.title } else { "NoorVox" })) $cards; $source = "title card" }
    }
    $current = $img
    $lines.Add("file '" + ($img -replace "'", "'\''") + "'")
    $lines.Add("duration " + ((($end - $start) / 1000).ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)))
    $report.Add([pscustomobject]@{ segment = $seg.segmentId; seconds = [math]::Round(($end - $start) / 1000, 2); picture = $source; file = (Split-Path $img -Leaf) })
}
$lines.Add("file '" + ($current -replace "'", "'\''") + "'")            # concat demuxer: the last duration needs a trailing entry
[IO.File]::WriteAllLines((Join-Path $work "pictures.txt"), $lines, [Text.UTF8Encoding]::new($false))

$vf = "scale=$($Width):$($Height):force_original_aspect_ratio=decrease,pad=$($Width):$($Height):(ow-iw)/2:(oh-ih)/2:color=$Background,fps=$Fps,format=yuv420p"
$inputs = @("-f", "concat", "-safe", "0", "-i", "pictures.txt", "-i", $audio)
$maps = @("-map", "0:v", "-map", "1:a")
$srt = if ($m.subtitles.srt) { Resolve-Rel $m.subtitles.srt } else { $null }
if ($Subtitles -ne "none" -and -not ($srt -and (Test-Path $srt))) { Write-Warning "No SRT in the manifest; making the video without subtitles."; $Subtitles = "none" }
if ($Subtitles -eq "burn") {
    Copy-Item $srt (Join-Path $work "subs.srt")                         # relative name: avoids Windows path escaping in the filter
    # libass sizes against a 288-line virtual screen: 12 ≈ 4 % of the picture height. BorderStyle=3 = a translucent box behind the text.
    $vf += ",subtitles=subs.srt:force_style='FontName=Segoe UI,FontSize=12,BorderStyle=3,Outline=1,Shadow=0,OutlineColour=&H8C000000,MarginV=14'"
} elseif ($Subtitles -eq "soft") {
    $inputs += @("-i", $srt)
    $maps += @("-map", "2:s", "-c:s", "mov_text", "-metadata:s:s:0", "language=eng")
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$partial = "$Out.partial"
Push-Location $work
try {
    $seconds = (([double]$m.audio.durationMs) / 1000).ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
    $r = Invoke-Native $FFmpeg (@("-hide_banner", "-nostats", "-loglevel", "error") + $inputs + $maps + @("-vf", $vf, "-c:v", "libx264", "-preset", "medium",
        "-crf", "18", "-c:a", "aac", "-b:a", "192k", "-t", $seconds, "-movflags", "+faststart", "-f", "mp4", $partial))
    if ($r.Code -ne 0) { Remove-Item $partial -ErrorAction SilentlyContinue; throw "ffmpeg failed (exit $($r.Code)): $($r.Text)" }
    Move-Item $partial $Out                                             # only a finished video gets the real name
} finally { Pop-Location }

# Check the result against the manifest (measured, not assumed).
$probe = if (Get-Command ffprobe -ErrorAction SilentlyContinue) { "ffprobe" } else { Join-Path (Split-Path (Get-Command $FFmpeg).Source) "ffprobe.exe" }
$dur = [double]::Parse((Invoke-Native $probe @("-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", $Out)).Text, [Globalization.CultureInfo]::InvariantCulture)
$diffMs = [math]::Abs($dur * 1000 - [double]$m.audio.durationMs)
Remove-Item -Recurse -Force $work
[pscustomobject]@{
    video = $Out; durationSeconds = [math]::Round($dur, 3); audioDurationSeconds = [math]::Round([double]$m.audio.durationMs / 1000, 3)
    durationMatches = ($diffMs -le 150); subtitles = $Subtitles; size = "$($Width)x$($Height)"; segments = $report
} | ConvertTo-Json -Depth 4
if ($diffMs -gt 150) { Write-Error "Video duration differs from the audio by $([int]$diffMs) ms." }
