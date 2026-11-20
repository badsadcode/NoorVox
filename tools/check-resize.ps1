# Checks that NoorVox Studio's window edges really resize (regression check for the custom title bar, 0.3.1).
# Launches the studio with an isolated home, then asks Windows what is under points 2 px inside each edge/corner:
# WindowFromPoint must be the top-level window (not the WebView2 child) and WM_NCHITTEST must say "resize".
# Usage: .\tools\check-resize.ps1 -Studio <NoorVoxStudio.exe>   (the studio window must be unobstructed on screen)
param([Parameter(Mandatory)][string]$Studio)
$ErrorActionPreference = "Stop"
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class NvWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$home_ = Join-Path ([IO.Path]::GetTempPath()) ("noorvox-resize-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory $home_ | Out-Null
$env:NOORVOX_HOME = $home_
$p = Start-Process $Studio -ArgumentList "--page", "settings" -PassThru
try {
    for ($i = 0; $i -lt 120 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
    Start-Sleep -Seconds 6                                            # let the WebView2 child attach and lay out
    $h = $p.MainWindowHandle
    [void][NvWin]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 500
    $r = New-Object NvWin+RECT; [void][NvWin]::GetWindowRect($h, [ref]$r)
    $midX = [int](($r.L + $r.R) / 2); $midY = [int](($r.T + $r.B) / 2)
    # name, x, y, expected hit-test code (HTLEFT 10, HTRIGHT 11, HTTOP 12, HTBOTTOM 15, HTTOPLEFT 13, HTBOTTOMRIGHT 17)
    $points = @(("left", ($r.L + 2), $midY, 10), ("right", ($r.R - 3), $midY, 11), ("top", $midX, ($r.T + 2), 12),
                ("bottom", $midX, ($r.B - 3), 15), ("top-left", ($r.L + 2), ($r.T + 2), 13), ("bottom-right", ($r.R - 3), ($r.B - 3), 17))
    $ok = $true
    foreach ($pt in $points) {
        $q = New-Object NvWin+POINT; $q.X = $pt[1]; $q.Y = $pt[2]
        $under = [NvWin]::WindowFromPoint($q)
        $lparam = [IntPtr](($pt[2] -shl 16) -bor ($pt[1] -band 0xFFFF))
        $hit = [int][NvWin]::SendMessage($under, 0x84, [IntPtr]::Zero, $lparam)   # WM_NCHITTEST on whatever window is there
        $pass = ($under -eq $h) -and ($hit -eq $pt[3])
        if (-not $pass) { $ok = $false }
        "{0,-5} {1,-13} window={2} hit={3} (want {4})" -f $(if ($pass) { "PASS" } else { "FAIL" }), $pt[0], $(if ($under -eq $h) { "studio" } else { "child/other" }), $hit, $pt[3]
    }
    if ($ok) { "resize edges OK" } else { "resize edges BROKEN" }
} finally {
    [void]$p.CloseMainWindow(); [void]$p.WaitForExit(15000)
    Remove-Item -Recurse -Force $home_ -ErrorAction SilentlyContinue
}
