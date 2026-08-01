using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Synthesises mouse and keyboard input. Every method has a foreground variant
/// (SendInput — real input, moves the cursor, works everywhere) and, where the idea
/// makes sense, a background variant (PostMessage — no cursor movement, but only
/// classic Win32 targets honour it).
/// </summary>
internal static class InputSender
{
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // ---- Low-level plumbing ----

    private static void Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        uint sent = SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "SendInput was blocked. The target window is probably running elevated — restart Auto Clicker as administrator.");
    }

    private static INPUT Mouse(uint flags, uint mouseData = 0)
    {
        var i = new INPUT { type = INPUT_MOUSE };
        i.u.mi.dwFlags = flags;
        i.u.mi.mouseData = mouseData;
        return i;
    }

    private static INPUT Key(ushort vk, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = vk;
        i.u.ki.wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        i.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return i;
    }

    private static INPUT Unicode(char ch, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = 0;
        i.u.ki.wScan = ch;
        i.u.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
        return i;
    }

    /// <summary>Keys that live on the extended half of the keyboard scan-code map.</summary>
    private static bool IsExtended(ushort vk) => (Keys)vk switch
    {
        Keys.Insert or Keys.Delete or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
            or Keys.Up or Keys.Down or Keys.Left or Keys.Right
            or Keys.NumLock or Keys.PrintScreen or Keys.Divide
            or Keys.RControlKey or Keys.RMenu or Keys.Apps or Keys.LWin or Keys.RWin => true,
        _ => false,
    };

    private static (uint down, uint up) MouseFlags(int button) => button switch
    {
        1 => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        2 => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    private static (uint down, uint up, uint dbl, int mk) MessageFlags(int button) => button switch
    {
        1 => (WM_RBUTTONDOWN, WM_RBUTTONUP, WM_RBUTTONDBLCLK, MK_RBUTTON),
        2 => (WM_MBUTTONDOWN, WM_MBUTTONUP, WM_MBUTTONDBLCLK, MK_MBUTTON),
        _ => (WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, MK_LBUTTON),
    };

    // ---- Clicking ----

    public static void Click(int button, bool dbl, int holdMs = 0, Func<bool>? keepGoing = null)
    {
        var (down, up) = MouseFlags(button);
        Send(new[] { Mouse(down) });
        try
        {
            HoldFor(holdMs, keepGoing);
        }
        finally
        {
            Send(new[] { Mouse(up) });
        }

        if (dbl)
        {
            Thread.Sleep(15);
            Send(new[] { Mouse(down), Mouse(up) });
        }
    }

    /// <summary>
    /// Presses a button and keeps it down until <paramref name="keepGoing"/> returns false.
    /// The release is in a finally block: leaving a button physically down is a
    /// system-wide mess the user has to clear by hand.
    /// </summary>
    public static void HoldUntil(int button, Func<bool> keepGoing)
    {
        var (down, up) = MouseFlags(button);
        Send(new[] { Mouse(down) });
        try
        {
            while (keepGoing()) Thread.Sleep(20);
        }
        finally
        {
            Send(new[] { Mouse(up) });
        }
    }

    /// <summary>
    /// Panic-stop safety net: unconditionally releases all three mouse buttons. A run can be
    /// aborted while a button is physically down — mid-<see cref="HoldUntil"/>, mid-<see cref="Drag"/>,
    /// or mid-click — and a stuck button is a system-wide mess the user has to clear by hand.
    /// Sending an "up" for a button that was never down is harmless, so there is no attempt to
    /// track which buttons are actually held; unconditional release is the entire point.
    /// </summary>
    public static void ReleaseAllButtons()
    {
        try
        {
            Send(new[] { Mouse(MOUSEEVENTF_LEFTUP), Mouse(MOUSEEVENTF_RIGHTUP), Mouse(MOUSEEVENTF_MIDDLEUP) });
        }
        catch (Win32Exception)
        {
            // This is the panic path itself: swallowing here is correct, unlike everywhere
            // else in this file, because a caller relying on the safety net must never see
            // it throw (SendInput can be blocked by an elevated foreground window).
        }
    }

    /// <summary>
    /// Sleeps for a click's hold time in short slices, checking <paramref name="keepGoing"/>
    /// between them. A single Thread.Sleep(holdMs) would make Stop and the panic key wait out
    /// the whole hold — the only blocking path in the engine that ignored a stop request.
    /// </summary>
    private static void HoldFor(int holdMs, Func<bool>? keepGoing)
    {
        if (holdMs <= 0) return;
        if (keepGoing is null) { Thread.Sleep(holdMs); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (keepGoing())
        {
            long left = holdMs - sw.ElapsedMilliseconds;
            if (left <= 0) return;
            Thread.Sleep((int)Math.Min(10, left));
        }
    }

    /// <summary>Posts click messages to the window under a screen point; cursor never moves.</summary>
    public static void BackgroundClick(int screenX, int screenY, int button, bool dbl, int holdMs = 0, Func<bool>? keepGoing = null)
    {
        if (!TargetAt(screenX, screenY, out IntPtr hwnd, out POINT cp)) return;
        var (down, up, dblMsg, mk) = MessageFlags(button);
        IntPtr lParam = MakeLParam(cp.X, cp.Y);

        PostMessage(hwnd, down, (IntPtr)mk, lParam);
        try
        {
            HoldFor(holdMs, keepGoing);
        }
        finally
        {
            PostMessage(hwnd, up, IntPtr.Zero, lParam);
        }

        if (dbl)
        {
            PostMessage(hwnd, dblMsg, (IntPtr)mk, lParam);
            PostMessage(hwnd, up, IntPtr.Zero, lParam);
        }
    }

    // ---- Scrolling ----

    public static void Scroll(int notches, bool horizontal)
    {
        if (notches == 0) return;
        uint data = unchecked((uint)(notches * WHEEL_DELTA));
        Send(new[] { Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, data) });
    }

    public static void BackgroundScroll(int screenX, int screenY, int notches, bool horizontal)
    {
        if (notches == 0) return;
        if (!TargetAt(screenX, screenY, out IntPtr hwnd, out _)) return;

        // WM_MOUSEWHEEL is the odd one out: its lParam carries SCREEN coordinates,
        // not client coordinates like the button messages.
        IntPtr wParam = MakeWParam(0, notches * WHEEL_DELTA);
        IntPtr lParam = MakeLParam(screenX, screenY);
        PostMessage(hwnd, horizontal ? WM_MOUSEHWHEEL : WM_MOUSEWHEEL, wParam, lParam);
    }

    // ---- Dragging ----

    /// <summary>
    /// Press at the start point, travel to the end point, release. <paramref name="keepGoing"/>
    /// is polled during the travel so a stop request doesn't have to wait it out — the
    /// button is released either way.
    /// </summary>
    public static void Drag(int x1, int y1, int x2, int y2, int button, int dragMs, Func<bool> keepGoing)
    {
        var (down, up) = MouseFlags(button);
        SetCursorPos(x1, y1);
        Thread.Sleep(10); // let the target register the hover before the press
        Send(new[] { Mouse(down) });
        try
        {
            int steps = Math.Clamp(dragMs / 10, 8, 200);
            int stepMs = Math.Max(0, dragMs / steps);
            for (int s = 1; s <= steps && keepGoing(); s++)
            {
                SetCursorPos(Lerp(x1, x2, s, steps), Lerp(y1, y2, s, steps));
                if (stepMs > 0) Thread.Sleep(stepMs);
            }
            SetCursorPos(x2, y2);
            Thread.Sleep(10); // ...and to register the final position before the release
        }
        finally
        {
            Send(new[] { Mouse(up) });
        }
    }

    public static void BackgroundDrag(int x1, int y1, int x2, int y2, int button, int dragMs, Func<bool> keepGoing)
    {
        if (!TargetAt(x1, y1, out IntPtr hwnd, out POINT start)) return;
        var (down, up, _, mk) = MessageFlags(button);

        PostMessage(hwnd, down, (IntPtr)mk, MakeLParam(start.X, start.Y));

        // The release goes in a finally, not a catch. An early return is not an exception,
        // so a catch would let the ScreenToClient bail-out below post DOWN and never post
        // UP — leaving the target application believing the button is still held, with no
        // physical button stuck for the user to notice and clear.
        POINT release = start;
        try
        {
            // The end point is mapped into the SAME window: a drag that crosses a window
            // boundary still belongs to the window that captured the mouse.
            var endScreen = new POINT { X = x2, Y = y2 };
            if (!ScreenToClient(hwnd, ref endScreen)) return;
            release = endScreen;

            int steps = Math.Clamp(dragMs / 10, 8, 200);
            int stepMs = Math.Max(0, dragMs / steps);
            for (int s = 1; s <= steps && keepGoing(); s++)
            {
                PostMessage(hwnd, WM_MOUSEMOVE, (IntPtr)mk,
                    MakeLParam(Lerp(start.X, endScreen.X, s, steps), Lerp(start.Y, endScreen.Y, s, steps)));
                if (stepMs > 0) Thread.Sleep(stepMs);
            }
        }
        finally
        {
            PostMessage(hwnd, up, IntPtr.Zero, MakeLParam(release.X, release.Y));
        }
    }

    private static int Lerp(int from, int to, int step, int steps) =>
        from + (int)((long)(to - from) * step / steps);

    // ---- Keyboard ----

    /// <summary>Presses a combo such as <c>Ctrl+Shift+Esc</c> as one atomic input batch.</summary>
    public static void SendCombo(string combo)
    {
        if (!TryParseCombo(combo, out var mods, out ushort vk, out string error))
            throw new InvalidOperationException(error);

        var batch = new List<INPUT>(mods.Count * 2 + 2);
        foreach (ushort m in mods) batch.Add(Key(m, up: false));
        batch.Add(Key(vk, up: false));
        batch.Add(Key(vk, up: true));
        for (int i = mods.Count - 1; i >= 0; i--) batch.Add(Key(mods[i], up: true));
        Send(batch.ToArray());
    }

    /// <summary>Types literal text. Sent as Unicode, so the keyboard layout is irrelevant.</summary>
    public static void TypeText(string text, Func<bool> keepGoing)
    {
        foreach (char ch in text)
        {
            if (!keepGoing()) return;
            if (ch == '\n')
            {
                Send(new[] { Key((ushort)Keys.Return, false), Key((ushort)Keys.Return, true) });
                continue;
            }
            if (ch == '\r') continue; // CRLF: the LF above already produced the Return
            Send(new[] { Unicode(ch, false), Unicode(ch, true) });
        }
    }

    /// <summary>
    /// Posts keystrokes to a window instead of the focused one. Far less reliable than
    /// the foreground path — most modern frameworks ignore synthesised WM_KEY* — but it
    /// works for classic Win32 edit controls.
    /// </summary>
    public static void BackgroundCombo(int screenX, int screenY, string combo)
    {
        if (!TryParseCombo(combo, out var mods, out ushort vk, out string error))
            throw new InvalidOperationException(error);
        if (!TargetAt(screenX, screenY, out IntPtr hwnd, out _)) return;

        foreach (ushort m in mods) PostMessage(hwnd, WM_KEYDOWN, (IntPtr)m, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
        for (int i = mods.Count - 1; i >= 0; i--) PostMessage(hwnd, WM_KEYUP, (IntPtr)mods[i], IntPtr.Zero);
    }

    public static void BackgroundTypeText(int screenX, int screenY, string text, Func<bool> keepGoing)
    {
        if (!TargetAt(screenX, screenY, out IntPtr hwnd, out _)) return;
        foreach (char ch in text)
        {
            if (!keepGoing()) return;
            if (ch == '\r') continue;
            PostMessage(hwnd, WM_CHAR, (IntPtr)(ch == '\n' ? '\r' : ch), IntPtr.Zero);
        }
    }

    // ---- Combo parsing ----

    /// <summary>
    /// Parses <c>Ctrl+Shift+F5</c>-style strings. Accepts the usual aliases people
    /// actually type (Esc, Enter, PgUp, Del, Win, …).
    /// </summary>
    public static bool TryParseCombo(string combo, out List<ushort> modifiers, out ushort vk, out string error)
    {
        modifiers = new List<ushort>();
        vk = 0;
        error = "";

        if (string.IsNullOrWhiteSpace(combo))
        {
            error = "No key specified.";
            return false;
        }

        string? keyToken = null;
        foreach (string raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers.Add(VK_CONTROL); continue;
                case "ALT": modifiers.Add(VK_MENU); continue;
                case "SHIFT": modifiers.Add(VK_SHIFT); continue;
                case "WIN" or "META" or "CMD": modifiers.Add(VK_LWIN); continue;
            }

            if (keyToken is not null)
            {
                error = $"'{combo}' has more than one non-modifier key.";
                return false;
            }
            keyToken = raw;
        }

        if (keyToken is null)
        {
            error = "A combo needs a key, not only modifiers.";
            return false;
        }

        if (!TryResolveKey(keyToken, out vk))
        {
            error = $"Unrecognised key '{keyToken}'.";
            return false;
        }
        return true;
    }

    private static bool TryResolveKey(string token, out ushort vk)
    {
        vk = 0;
        string name = Aliases.TryGetValue(token, out string? alias) ? alias : token;

        // Bare digits are Keys.D0..D9; a bare letter parses on its own.
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) name = "D" + name;

        if (!Enum.TryParse(name, ignoreCase: true, out Keys key) || !Enum.IsDefined(key))
            return false;

        vk = (ushort)key;
        return true;
    }

    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["esc"] = "Escape",
            ["enter"] = "Return",
            ["return"] = "Return",
            ["pgup"] = "PageUp",
            ["pageup"] = "PageUp",
            ["pgdn"] = "PageDown",
            ["pagedown"] = "PageDown",
            ["ins"] = "Insert",
            ["del"] = "Delete",
            ["bksp"] = "Back",
            ["backspace"] = "Back",
            ["caps"] = "CapsLock",
            ["prtsc"] = "PrintScreen",
            ["menu"] = "Apps",
            ["plus"] = "Oemplus",
            ["minus"] = "OemMinus",
            ["space"] = "Space",
            ["spacebar"] = "Space",
        };

    // ---- Shared ----

    /// <summary>Window under a screen point, plus that point in the window's client space.</summary>
    private static bool TargetAt(int screenX, int screenY, out IntPtr hwnd, out POINT clientPt)
    {
        clientPt = default;
        var sp = new POINT { X = screenX, Y = screenY };
        hwnd = WindowFromPoint(sp);
        if (hwnd == IntPtr.Zero) return false;

        var cp = sp;
        if (!ScreenToClient(hwnd, ref cp)) return false;
        clientPt = cp;
        return true;
    }

    /// <summary>Validation helper for the action editor.</summary>
    public static string? DescribeComboProblem(string combo) =>
        TryParseCombo(combo, out _, out _, out string error) ? null : error;

    public static string FormatNotches(int n) => n.ToString(CultureInfo.InvariantCulture);
}
