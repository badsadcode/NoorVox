# Builds a ready-to-run NoorVox folder: NoorVoxStudio.exe (double-click), noorvox.exe (CLI) and NoorVox.Worker.exe.
# Self-contained win-x64: the target PC needs no .NET install (only the Edge WebView2 Runtime, built into Windows 11).
# Usage:  .\tools\publish.ps1 [-Output E:\Apps\NoorVox] [-Portable] [-Zip]
#   -Portable  adds noorvox.portable: settings, worker registry and defaults live in <Output>\data instead of %APPDATA%.
#              Use it when the app should never touch the user profile, or when several launchers (Explorer, packaged
#              apps with virtualised AppData) must share one configuration. An existing data\ folder is never overwritten.
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\dist\NoorVox"),
    [switch]$Portable,
    [switch]$Zip
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$Output = [IO.Path]::GetFullPath($Output)

if (Test-Path (Join-Path $Output "NoorVoxStudio.exe")) {
    if (Get-Process NoorVoxStudio, NoorVox.Worker -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Output*" }) {
        throw "NoorVox is running from $Output. Close it first."
    }
}
New-Item -ItemType Directory -Force $Output | Out-Null

# Order matters only for shared files (identical runtime files); the studio goes last.
foreach ($proj in "src\NoorVox.Worker", "src\NoorVox.Cli", "src\NoorVox.Studio") {
    Write-Host "Publishing $proj ..."
    dotnet publish (Join-Path $repo $proj) -c Release -r win-x64 --self-contained true -o $Output -p:DebugType=none -p:DebugSymbols=false --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

# KokoroSharp ships its voices as package content; NoorVox installs voices into the models folder instead.
Remove-Item (Join-Path $Output "voices") -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $Output -Filter *.lib | Remove-Item -Force

if ($Portable) {
    $marker = Join-Path $Output "noorvox.portable"
    if (-not (Test-Path $marker)) { Set-Content -Path $marker -Value "NoorVox portable mode: settings live in .\data" -Encoding utf8 }
}

$version = (& (Join-Path $Output "noorvox.exe") --version --json --quiet | ConvertFrom-Json).data.version
@"
NoorVox $version — local voice studio (Bad Ideas Works)

Start:  NoorVoxStudio.exe
CLI:    noorvox.exe --help        (docs: https://github.com/badsadcode/NoorVox/tree/main/docs)

First run asks where to keep models, caches and exports (any drive, C: included), then installs the
recommended voice (~440 MB, one time). After that everything works offline.
Settings live in %APPDATA%\NoorVox\settings.json. Put an empty file named noorvox.portable next to
the exe to keep everything inside this folder instead.

Requires Windows 10/11 x64 and the Microsoft Edge WebView2 Runtime (included in Windows 11).
"@ | Set-Content -Encoding utf8 (Join-Path $Output "README.txt")

$size = [math]::Round(((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB)
Write-Host "NoorVox $version published to $Output ($size MB)"

if ($Zip) {
    $zipPath = Join-Path (Split-Path $Output -Parent) "NoorVox-$version-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    # Never ship personal data: the zip leaves out data\ (settings, worker registry).
    $items = Get-ChildItem $Output | Where-Object { $_.Name -ne "data" }
    Compress-Archive -Path $items.FullName -DestinationPath $zipPath
    Write-Host "Zip: $zipPath"
}
