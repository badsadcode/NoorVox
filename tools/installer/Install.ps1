# NoorVox installer. Run from the extracted release folder:
#   powershell -ExecutionPolicy Bypass -File .\Install.ps1                      (asks where to install)
#   .\Install.ps1 -Destination E:\Apps\NoorVox -Portable                        (unattended)
# C: is allowed; any drive works. Installs per user (no admin rights, no registry changes).
param(
    [string]$Destination,
    [switch]$Portable,            # keep settings next to the app (data\) instead of %APPDATA%\NoorVox
    [switch]$NoDesktopShortcut,
    [switch]$NoStartMenu
)
$ErrorActionPreference = "Stop"
$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source "NoorVoxStudio.exe"))) { throw "Run Install.ps1 from the extracted NoorVox folder (NoorVoxStudio.exe not found next to it)." }

if (-not $Destination) {
    $default = Join-Path $env:LOCALAPPDATA "Programs\NoorVox"
    Write-Host "Where should NoorVox be installed? Any drive is fine (models and projects are chosen separately on first run)."
    $answer = Read-Host "Folder [$default]"
    $Destination = if ([string]::IsNullOrWhiteSpace($answer)) { $default } else { $answer.Trim('"') }
    if (-not $PSBoundParameters.ContainsKey("Portable")) {
        $p = Read-Host "Keep NoorVox settings inside that folder (portable mode)? [y/N]"
        $Portable = $p -match '^(y|yes|t|tak)$'
    }
}
$Destination = [IO.Path]::GetFullPath($Destination)
if ($Destination -eq [IO.Path]::GetFullPath($source)) { throw "Choose a folder other than the one you extracted to." }

$running = Get-Process NoorVoxStudio, NoorVox.Worker, noorvox -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Destination\*" }
if ($running) { throw "NoorVox is running from $Destination. Close it first." }

New-Item -ItemType Directory -Force $Destination | Out-Null
Write-Host "Copying NoorVox to $Destination ..."
# data\ (settings) in the destination is never overwritten; the release zip doesn't contain one.
Get-ChildItem $source | Where-Object { $_.Name -ne "data" } | ForEach-Object {
    Copy-Item $_.FullName -Destination $Destination -Recurse -Force
}
if ($Portable -and -not (Test-Path (Join-Path $Destination "noorvox.portable"))) {
    Set-Content -Path (Join-Path $Destination "noorvox.portable") -Value "NoorVox portable mode: settings live in .\data" -Encoding utf8
}

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($path) {
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = Join-Path $Destination "NoorVoxStudio.exe"
    $s.WorkingDirectory = $Destination
    $s.Description = "NoorVox Studio - local voice studio"
    $s.Save()
}
$made = @()
if (-not $NoStartMenu) {
    $start = Join-Path ([Environment]::GetFolderPath("Programs")) "NoorVox Studio.lnk"
    New-Shortcut $start; $made += $start
}
if (-not $NoDesktopShortcut) {
    $desk = Join-Path ([Environment]::GetFolderPath("Desktop")) "NoorVox Studio.lnk"
    New-Shortcut $desk; $made += $desk
}
@{ installedAt = (Get-Date).ToString("o"); destination = $Destination; portable = [bool]$Portable; shortcuts = $made } |
    ConvertTo-Json | Set-Content -Path (Join-Path $Destination "install.json") -Encoding utf8

Write-Host ""
Write-Host "NoorVox installed to $Destination$(if ($Portable) { ' (portable mode)' })."
$made | ForEach-Object { Write-Host "Shortcut: $_" }
$how = if ($made.Count -gt 0) { "from the shortcut" } else { "with $Destination\NoorVoxStudio.exe" }
Write-Host "Start NoorVox Studio $how. To remove it later: $Destination\Uninstall.ps1"
