param([string]$ProcName = 'AutoClicker')
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class DpiProbe2 {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[void][DpiProbe2]::SetProcessDpiAwarenessContext([IntPtr](-4)) # PER_MONITOR_AWARE_V2
$target = (Get-Process $ProcName -ErrorAction Stop | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1).Id
$script:t = [uint32]$target
$script:pv = [uint32]0
$script:found = 0
$cb = [DpiProbe2+EnumProc]{
    param($h, $l)
    [void][DpiProbe2]::GetWindowThreadProcessId($h, [ref]$script:pv)
    if ($script:pv -eq $script:t -and [DpiProbe2]::IsWindowVisible($h)) {
        $r = New-Object DpiProbe2+RECT
        [void][DpiProbe2]::GetWindowRect($h, [ref]$r)
        $sb = New-Object System.Text.StringBuilder 256
        [void][DpiProbe2]::GetWindowText($h, $sb, 256)
        if (($r.R - $r.L) -gt 50) {
            Write-Host ("h=0x{0:X} rect={1},{2} -> {3},{4}  {5}x{6}  '{7}'" -f `
                $h.ToInt64(), $r.L, $r.T, $r.R, $r.B, ($r.R - $r.L), ($r.B - $r.T), $sb.ToString())
            $script:found++
        }
    }
    return $true
}
[void][DpiProbe2]::EnumWindows($cb, [IntPtr]::Zero)
if ($script:found -eq 0) { Write-Host "no visible windows for pid $target" }
Write-Host "screen: $([System.Windows.Forms.Screen]::PrimaryScreen.Bounds)"
