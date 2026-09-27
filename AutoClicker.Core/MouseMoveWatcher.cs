using System.Runtime.InteropServices;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Watches for genuine user mouse movement during a run, for the opt-in "stop when I move
/// the mouse" safety option. A WH_MOUSE_LL hook fires <see cref="Moved"/> once the cursor
/// travels more than <see cref="ThresholdPx"/> from where it was when the hook was armed.
/// Deliberately minimal — it is not the recorder's hook, and it only observes movement.
///
/// Self-triggering note: the macro moves the cursor with SetCursorPos, which is not
/// delivered to low-level mouse hooks; injected SendInput events (flagged LLMHF_INJECTED)
/// are skipped too. So this only ever fires for real user movement, never for the macro's
/// own clicks.
/// </summary>
internal sealed class MouseMoveWatcher
{
    internal const int ThresholdPx = 15;

    private IntPtr hook = IntPtr.Zero;
    private LowLevelMouseProc? proc; // kept alive to prevent GC
    private POINT anchor;
    private bool armed;
    private bool fired;

    /// <summary>Raised (on the installing thread) when the user has moved the mouse past the threshold.</summary>
    public event Action? Moved;

    /// <summary>True when the hook is currently installed.</summary>
    public bool IsArmed => armed;

    /// <summary>Installs the hook, anchoring at the cursor's current position. Returns false if the hook cannot install.</summary>
    public bool Start()
    {
        if (armed) return true;
        GetCursorPos(out anchor);
        proc = HookCallback;
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            proc = null;
            return false;
        }
        armed = true;
        fired = false;
        return true;
    }

    public void Stop()
    {
        if (!armed) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        proc = null;
        armed = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && armed && !fired && wParam.ToInt32() == WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // Injected events are our own (or another app's) SendInput; ignore them.
            if ((data.flags & LLMHF_INJECTED) == 0
                && MovedBeyondThreshold(anchor.X, anchor.Y, data.pt.X, data.pt.Y, ThresholdPx))
            {
                fired = true;
                Moved?.Invoke();
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <summary>True when the cursor has travelled more than <c>thresholdPx</c> pixels from (x1,y1).</summary>
    internal static bool MovedBeyondThreshold(int x1, int y1, int x2, int y2, int thresholdPx)
    {
        long dx = x2 - x1, dy = y2 - y1;
        return dx * dx + dy * dy > (long)thresholdPx * thresholdPx;
    }
}
