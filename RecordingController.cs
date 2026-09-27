using System.Runtime.InteropServices;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Owns the WH_MOUSE_LL hook that turns raw mouse clicks into recorded sequence actions.
/// Kept free of WinForms so it can be reasoned about (and, in principle, driven) without a
/// form: callbacks are surfaced as events, and the caller supplies a marshaller so the
/// events can be delivered on the UI thread exactly where the form's BeginInvoke used to.
/// </summary>
internal sealed class RecordingController
{
    private IntPtr mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? hookProc; // kept alive to prevent GC
    private IntPtr excludeHwnd;
    private Action<Action> uiMarshal = null!;
    private bool recording;

    public bool IsRecording => recording;

    /// <summary>Raised on the UI thread for each recorded left-click, with the screen point.</summary>
    public event Action<POINT>? PointRecorded;

    /// <summary>Raised on the UI thread when the user right-clicks to finish recording.</summary>
    public event Action? EndRecordingRequested;

    /// <summary>
    /// Installs the hook. Returns false (and reports why) when the hook cannot be installed.
    /// </summary>
    public bool Start(IntPtr excludeHwnd, Action<string> status, Action<Action> uiMarshal)
    {
        this.excludeHwnd = excludeHwnd;
        this.uiMarshal = uiMarshal;
        hookProc = HookCallback;
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero) { status("Could not install mouse hook."); return false; }
        recording = true;
        status("RECORDING: left-click each target. Right-click or F8 to finish.");
        return true;
    }

    public void Stop()
    {
        recording = false;
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        hookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && recording)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_RBUTTONDOWN_LL)
            {
                uiMarshal(() => EndRecordingRequested?.Invoke());
                return (IntPtr)1; // swallow the right-click that ends recording
            }
            if (msg == WM_LBUTTONDOWN_LL)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = data.pt;
                // ignore clicks landing on our own window
                if (GetWindowRect(excludeHwnd, out RECT r) &&
                    pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                {
                    return CallNextHookEx(mouseHook, nCode, wParam, lParam);
                }
                // Re-check `recording` on the UI thread: the marshal is async, so recording
                // may already have been stopped by the time this runs.
                uiMarshal(() => { if (recording) PointRecorded?.Invoke(pt); });
                return (IntPtr)1; // swallow so the target isn't actually clicked during recording
            }
        }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }
}
