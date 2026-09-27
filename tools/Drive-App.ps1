# Drive-App.ps1 — focus an app window and send keystrokes, for UI exploration.
# Usage: pwsh -File Drive-App.ps1 -Variant bartels|jitbit|ours -Keys "{ESC}" [-Enter] [-Wait 800]
# SendWait syntax: + = Shift, ^ = Ctrl, % = Alt, {ENTER}, {ESC}, {TAB}, {DOWN} etc.
param(
    [Parameter(Mandatory)][ValidateSet('bartels','jitbit','ours')][string]$Variant,
    [Parameter(Mandatory)][string]$Keys,
    [switch]$Enter,
    [int]$Wait = 800
)
Add-Type -AssemblyName System.Windows.Forms
$procName = switch ($Variant) {
    'bartels' { 'MacroRecorder' }
    'jitbit'  { 'MacroRecorder' }
    'ours'    { 'AutoClicker' }
}
$exe = switch ($Variant) {
    'bartels' { "C:\Program Files (x86)\MacroRecorder\MacroRecorder.exe" }
    'jitbit'  { "C:\Program Files (x86)\MacroRecorder1\MacroRecorder.exe" }
    'ours'    { "d:\AI Coding\AutoClicker\publish\AutoClicker.exe" }
}

$all = @(Get-Process $procName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })
$target = $all | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
if (-not $target -and $Variant -eq 'jitbit') {
    $bartels = "C:\Program Files (x86)\MacroRecorder\MacroRecorder.exe"
    $target = $all | Where-Object { $_.Path -ne $bartels } | Select-Object -First 1
}
if (-not $target) { $target = $all | Select-Object -First 1 }
if (-not $target) { Write-Error "no $procName process for $Variant"; exit 1 }

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinDrv {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int n);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public static IntPtr Best;
    public static int BestArea;
    public static void Find(uint p) {
        Best = IntPtr.Zero; BestArea = 0;
        EnumWindows((h, _) => {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == p && IsWindowVisible(h) && GetWindowRect(h, out RECT r)) {
                int a = (r.Right - r.Left) * (r.Bottom - r.Top);
                if (a > BestArea) { BestArea = a; Best = h; }
            }
            return true;
        }, IntPtr.Zero);
    }
}
"@
[WinDrv]::Find([uint32]$target.Id)
if ([WinDrv]::Best -eq [IntPtr]::Zero) { Write-Error "no visible window"; exit 1 }
[void][WinDrv]::ShowWindow([WinDrv]::Best, 9)
[void][WinDrv]::SetForegroundWindow([WinDrv]::Best)
Start-Sleep -Milliseconds 500
if ($Enter) { [System.Windows.Forms.SendKeys]::SendWait("$Keys{ENTER}") }
else { [System.Windows.Forms.SendKeys]::SendWait($Keys) }
Start-Sleep -Milliseconds $Wait
Write-Host "sent '$Keys'$(if($Enter){' +ENTER'}) to $Variant (pid $($target.Id))"
