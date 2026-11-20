# Removes NoorVox program files and shortcuts. Your models, projects and exports are NOT touched; this script
# prints where they are so you can decide what to do with them.
#   .\Uninstall.ps1                 (asks for confirmation)
#   .\Uninstall.ps1 -Yes            (no question)
#   .\Uninstall.ps1 -RemoveSettings (also delete settings in portable data\)
param([switch]$Yes, [switch]$RemoveSettings)
$ErrorActionPreference = "Stop"
$dir = $PSScriptRoot
$exe = Join-Path $dir "NoorVoxStudio.exe"
if (-not (Test-Path $exe)) { throw "Run Uninstall.ps1 from the NoorVox install folder." }
if (Get-Process NoorVoxStudio, NoorVox.Worker, noorvox -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir\*" }) {
    throw "NoorVox is running from $dir. Close it first."
}

# Where the user's data lives (read from settings via the CLI before it is removed).
$storage = $null
try { $storage = (& (Join-Path $dir "noorvox.exe") storage show --json --quiet | ConvertFrom-Json).data } catch { }

if (-not $Yes) {
    $answer = Read-Host "Remove NoorVox program files from $dir and its shortcuts? Models/projects/exports are kept. [y/N]"
    if ($answer -notmatch '^(y|yes|t|tak)$') { Write-Host "Nothing removed."; exit 0 }
}

# Shortcuts that point to this install.
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath("Programs"), [Environment]::GetFolderPath("Desktop")) {
    $lnk = Join-Path $folder "NoorVox Studio.lnk"
    if ((Test-Path $lnk) -and $shell.CreateShortcut($lnk).TargetPath -eq $exe) { Remove-Item $lnk -Force; Write-Host "Removed shortcut $lnk" }
}

# Program files: everything in the install folder except data\ (unless -RemoveSettings).
Get-ChildItem $dir -Force | Where-Object { $RemoveSettings -or $_.Name -ne "data" } | Where-Object { $_.FullName -ne $PSCommandPath } |
    Remove-Item -Recurse -Force
Write-Host "Removed NoorVox program files from $dir."

if ($storage) {
    Write-Host ""
    Write-Host "Your NoorVox data was NOT removed. It lives here (delete it yourself if you no longer need it):"
    $storage.paths | Where-Object { $_.exists -and -not ($RemoveSettings -and $_.path -like "$dir\*") } | ForEach-Object { Write-Host ("  {0,-9} {1}" -f $_.kind, $_.path) }
    if (-not ($RemoveSettings -and $storage.settingsFile -like "$dir\*")) { Write-Host "  settings  $($storage.settingsFile)" }
}
Remove-Item $PSCommandPath -Force
