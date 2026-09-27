# Debug-Windows.ps1 — list visible top-level windows for given process names with raw rects.
param([string[]]$Name = @('MacroRecorder'))
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Wdbg2 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$pids = @{}
foreach ($n in $Name) { Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { $pids[$_.Id] = $_.ProcessName } }
Write-Host "target pids: $($pids.Keys -join ', ')"

$cb = [Wdbg2+EnumProc]{
    param($h, $l)
    $procId = [uint32]0
    [void][Wdbg2]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($script:pids.ContainsKey([int]$procId) -and [Wdbg2]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 512
        [void][Wdbg2]::GetWindowText($h, $sb, 512)
        $r = New-Object Wdbg2+RECT
        $ok = [Wdbg2]::GetWindowRect($h, [ref]$r)
        Write-Host ("pid={0} h=0x{1:X} ok={2} rect={3},{4} -> {5},{6} ({7}x{8}) '{9}'" -f `
            $procId, $h.ToInt64(), $ok, $r.Left, $r.Top, $r.Right, $r.Bottom, `
            ($r.Right - $r.Left), ($r.Bottom - $r.Top), $sb.ToString())
    }
    return $true
}
[void][Wdbg2]::EnumWindows($cb, [IntPtr]::Zero)
