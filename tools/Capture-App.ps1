# Capture-App.ps1 — screenshot an app window, disambiguated by install folder.
# Usage: pwsh -File Capture-App.ps1 -Variant bartels|jitbit -Out shot.png
param(
    [Parameter(Mandatory)][ValidateSet('bartels','jitbit','ours')][string]$Variant,
    [Parameter(Mandatory)][string]$Out,
    [int]$WaitSeconds = 3
)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$exe = if ($Variant -eq 'bartels') {
    "C:\Program Files (x86)\MacroRecorder\MacroRecorder.exe"
} elseif ($Variant -eq 'jitbit') {
    "C:\Program Files (x86)\MacroRecorder1\MacroRecorder.exe"
} else {
    "d:\AI Coding\AutoClicker\publish\AutoClicker.exe"
}

# Both installs ship MacroRecorder.exe — resolve by PID delta when starting,
# then fall back to path (Jitbit's .Path can come back empty from Get-Process).
# Both macro apps ship MacroRecorder.exe — resolve by PID delta when starting,
$procName = if ($Variant -eq 'ours') { 'AutoClicker' } else { 'MacroRecorder' }

function Resolve-Target {
    $all = @(Get-Process $procName -ErrorAction SilentlyContinue |
             Where-Object { $_.MainWindowHandle -ne 0 })
    $byPath = $all | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
    if ($byPath) { return $byPath }
    # Unreadable-path leftovers, excluding the OTHER variant's exe (Jitbit's
    # .Path comes back empty from Get-Process, Bartels' reads fine). When we're
    # LOOKING FOR bartels its path reads fine, so byPath already succeeded.
    $bartelsExe = "C:\Program Files (x86)\MacroRecorder\MacroRecorder.exe"
    if ($exe -eq $bartelsExe) { return $all | Select-Object -First 1 }
    return $all | Where-Object { $_.Path -ne $bartelsExe } | Select-Object -First 1
}

$p = Resolve-Target
if (-not $p) {
    Write-Host "not running; starting $exe..."
    Start-Process -FilePath $exe
    Start-Sleep -Seconds $WaitSeconds
    $p = Resolve-Target
}
if (-not $p) { Write-Error "no window for $Variant"; exit 1 }

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    // MainWindowHandle for these apps points at a HIDDEN 0x0 proxy window; the
    // real UI is the largest VISIBLE top-level window of the process.
    public static readonly List<IntPtr> Visible = new();
    public static readonly List<RECT> VisibleRects = new();
    public static void FindVisible(uint targetPid) {
        Visible.Clear(); VisibleRects.Clear();
        EnumWindows((h, _) => {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == targetPid && IsWindowVisible(h) && GetWindowRect(h, out RECT r)
                && (r.Right - r.Left) > 100 && (r.Bottom - r.Top) > 80) {
                Visible.Add(h); VisibleRects.Add(r);
            }
            return true;
        }, IntPtr.Zero);
    }
}
"@
$h = [IntPtr]::Zero
$r = New-Object Win32+RECT

function Use-Rect([IntPtr]$hwnd, $rect) {
    $script:h = $hwnd
    $script:r = $rect
}

[Win32]::ShowWindow($p.MainWindowHandle, 9) | Out-Null   # SW_RESTORE (best effort)
[Win32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 900

[Win32]::FindVisible([uint32]$p.Id)
if ([Win32]::Visible.Count -eq 0) { Write-Error "no visible window for $Variant (pid $($p.Id))"; exit 1 }
# Largest visible window = the app's main UI.
$best = 0; $bestArea = -1
for ($i = 0; $i -lt [Win32]::Visible.Count; $i++) {
    $rr = [Win32]::VisibleRects[$i]
    $area = ($rr.Right - $rr.Left) * ($rr.Bottom - $rr.Top)
    if ($area -gt $bestArea) { $bestArea = $area; $best = $i }
}
$hwnd = [Win32]::Visible[$best]
$r = [Win32]::VisibleRects[$best]
[void][Win32]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)

# PrintWindow first: works even when the window is occluded or on another
# monitor (PW_RENDERFULLCONTENT = 2 captures DirectX content too).
$hdc = $g.GetHdc()
$ok = [Win32]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
if (-not $ok) {
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $Out ($w x $h) pid=$($p.Id) hwnd=0x$($hwnd.ToInt64())"
