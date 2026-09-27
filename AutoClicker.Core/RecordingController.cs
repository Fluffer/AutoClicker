using System.Diagnostics;
using System.Runtime.InteropServices;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Owns the WH_MOUSE_LL and WH_KEYBOARD_LL hooks that turn raw input into recorded sequence
/// actions. Kept free of WinForms: callbacks are surfaced as events, and the caller supplies a
/// marshaller so the events are delivered on the UI thread exactly where the form's
/// BeginInvoke used to. All the event→action logic lives in <see cref="RecordingAssembler"/>;
/// this class only translates hook messages into assembler calls and decides what to swallow.
/// </summary>
internal sealed class RecordingController
{
    private const int VK_F8 = 0x77;

    private IntPtr mouseHook = IntPtr.Zero;
    private IntPtr keyboardHook = IntPtr.Zero;
    private LowLevelMouseProc? mouseProc;       // kept alive to prevent GC
    private LowLevelKeyboardProc? keyboardProc; // kept alive to prevent GC
    private IntPtr excludeHwnd;
    private Action<Action> uiMarshal = null!;
    private bool recording;
    private readonly Stopwatch clock = new();
    private RecordingAssembler? assembler;
    // Buttons whose down landed outside our own window and was therefore fed to the assembler;
    // their matching up is recorded wherever it lands.
    private readonly bool[] armedButton = new bool[3];
    // Hotkeys that must never become recorded actions: the main toggle (added in Start)
    // plus every profile hotkey, which the caller lists here because only the form knows
    // which profile keys are currently armed.
    private readonly HashSet<uint> filteredVks = new();

    public bool IsRecording => recording;

    /// <summary>
    /// Extra virtual keys to never record — the caller fills this with the currently
    /// registered profile hotkeys before each Start (only the form knows which are armed).
    /// </summary>
    public List<uint> AdditionalFilteredVks { get; } = new();

    /// <summary>Time since recording started, in milliseconds (still valid after Stop).</summary>
    public long ElapsedMs => clock.ElapsedMilliseconds;

    /// <summary>
    /// Raised on the UI thread for each recorded action. The bool is true when the action
    /// replaces the previously recorded one (double-click chaining) rather than appending.
    /// </summary>
    public event Action<SeqAction, bool>? ActionRecorded;

    /// <summary>Raised on the UI thread when the user right-clicks or presses F8 to finish.</summary>
    public event Action? EndRecordingRequested;

    /// <summary>Installs both hooks. Returns false (and reports why) when either cannot be installed.</summary>
    /// <param name="toggleVk">The main start/stop hotkey, filtered out of the recording.</param>
    public bool Start(IntPtr excludeHwnd, uint toggleVk, Action<string> status, Action<Action> uiMarshal)
    {
        this.excludeHwnd = excludeHwnd;
        // Profile hotkeys are hotkeys too: pressing one mid-recording stops the recording
        // through WndProc, but the keydown would still reach the assembler and leave a
        // stray Key action (the profile switch it triggers never gets recorded anyway).
        filteredVks.Clear();
        foreach (uint vk in AdditionalFilteredVks) filteredVks.Add(vk);
        filteredVks.Add(toggleVk);
        this.uiMarshal = uiMarshal;
        Array.Clear(armedButton);
        assembler = new RecordingAssembler();
        mouseProc = MouseHookCallback;
        keyboardProc = KeyboardHookCallback;
        clock.Restart();

        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero)
        {
            Cleanup();
            clock.Stop();
            status("Could not install mouse hook.");
            return false;
        }
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
        if (keyboardHook == IntPtr.Zero)
        {
            Cleanup();
            clock.Stop();
            status("Could not install keyboard hook.");
            return false;
        }

        recording = true;
        return true;
    }

    public void Stop()
    {
        recording = false;
        clock.Stop();
        // Flush any coalesced typed text still pending, so a word typed right before F8 /
        // right-click becomes its Text action instead of being lost. Stop runs on the UI
        // thread (button, F8 hotkey and right-click all originate there), so reporting here
        // is synchronous and on the same thread as every other ActionRecorded delivery.
        if (assembler is not null && assembler.Flush() is { } e)
            ActionRecorded?.Invoke(e, false);
        Cleanup();
    }

    private void Cleanup()
    {
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        if (keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
        mouseProc = null;
        keyboardProc = null;
        assembler = null;
    }

    // ---- Mouse hook ----

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && recording)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // Right-click ends recording (existing behaviour, kept for muscle memory). It is
            // swallowed so the context menu it would have opened never appears, and because
            // the recorder never swallows anything else.
            if (msg == WM_RBUTTONDOWN_LL)
            {
                uiMarshal(() => { if (recording) EndRecordingRequested?.Invoke(); });
                return (IntPtr)1;
            }

            long ts = clock.ElapsedMilliseconds;
            switch (msg)
            {
                case WM_LBUTTONDOWN_LL: return MouseDown(0, data, ts, nCode, wParam, lParam);
                case WM_LBUTTONUP_LL: return MouseUp(0, data, ts, nCode, wParam, lParam);
                case WM_MBUTTONDOWN_LL: return MouseDown(2, data, ts, nCode, wParam, lParam);
                case WM_MBUTTONUP_LL: return MouseUp(2, data, ts, nCode, wParam, lParam);
                case WM_MOUSEWHEEL_LL: return Wheel(data, horizontal: false, ts, nCode, wParam, lParam);
                case WM_MOUSEHWHEEL_LL: return Wheel(data, horizontal: true, ts, nCode, wParam, lParam);
            }
        }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    private IntPtr MouseDown(int button, MSLLHOOKSTRUCT data, long ts, int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Clicks landing on our own window are the user working the app (record/stop buttons,
        // the list) — pass them through and don't record them.
        if (InOwnWindow(data.pt)) return CallNextHookEx(mouseHook, nCode, wParam, lParam);

        armedButton[button] = true;
        uiMarshal(() => { if (recording) assembler!.MouseDown(button, data.pt.X, data.pt.Y, ts); });
        return CallNextHookEx(mouseHook, nCode, wParam, lParam); // recording does NOT swallow
    }

    private IntPtr MouseUp(int button, MSLLHOOKSTRUCT data, long ts, int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (!armedButton[button]) return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        armedButton[button] = false;
        uiMarshal(() =>
        {
            if (!recording) return;
            var e = assembler!.MouseUp(button, data.pt.X, data.pt.Y, ts);
            foreach (RecordingEmission f in assembler.TakeFlushedEmissions())
                ActionRecorded?.Invoke(f.Action, f.ReplacesLast);
            if (e is { } em)
                ActionRecorded?.Invoke(em.Action, em.ReplacesLast);
        });
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    private IntPtr Wheel(MSLLHOOKSTRUCT data, bool horizontal, long ts, int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (InOwnWindow(data.pt)) return CallNextHookEx(mouseHook, nCode, wParam, lParam);

        // mouseData's high word is the signed wheel delta (a multiple of WHEEL_DELTA).
        int delta = unchecked((short)(data.mouseData >> 16));
        uiMarshal(() =>
        {
            if (!recording) return;
            var e = assembler!.MouseWheel(delta, horizontal, data.pt.X, data.pt.Y, ts);
            foreach (RecordingEmission f in assembler.TakeFlushedEmissions())
                ActionRecorded?.Invoke(f.Action, f.ReplacesLast);
            if (e is { } em)
                ActionRecorded?.Invoke(em.Action, em.ReplacesLast);
        });
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    // ---- Keyboard hook ----

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && recording)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;
            bool isUp = (data.flags & LLKHF_UP) != 0;

            // F8 always ends recording: swallow both halves so the stop key itself is never
            // recorded and never reaches the target app.
            if (vk == VK_F8)
            {
                if (!isUp) uiMarshal(() => { if (recording) EndRecordingRequested?.Invoke(); });
                return (IntPtr)1;
            }

            // Hotkeys that stop or divert the recording (main toggle, profile hotkeys)
            // are never recorded: don't emit an action, but pass them through so
            // RegisterHotKey still sees them.
            if (filteredVks.Contains((uint)vk)) return CallNextHookEx(keyboardHook, nCode, wParam, lParam);

            long ts = clock.ElapsedMilliseconds;
            if (isUp)
            {
                uiMarshal(() => { if (recording) assembler!.KeyUp(vk); });
            }
            else
            {
                uiMarshal(() =>
                {
                    if (!recording) return;
                    var e = assembler!.KeyDown(vk, ts);
                    foreach (RecordingEmission f in assembler.TakeFlushedEmissions())
                        ActionRecorded?.Invoke(f.Action, f.ReplacesLast);
                    if (e is { } em)
                        ActionRecorded?.Invoke(em.Action, em.ReplacesLast);
                });
            }
            return CallNextHookEx(keyboardHook, nCode, wParam, lParam); // recording does NOT swallow keys
        }
        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private bool InOwnWindow(POINT pt) =>
        GetWindowRect(excludeHwnd, out RECT r) &&
        pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
}
