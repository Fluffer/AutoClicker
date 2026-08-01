using System.Runtime.InteropServices;
using System.Text;

// Every P/Invoke in this app resolves to a System32 library (user32, kernel32, gdi32,
// winmm, ole32). Pinning the search path there closes the DLL-planting angle where a
// same-named DLL dropped next to the executable would be loaded in preference — which
// matters more than usual here, because this app synthesizes input and is the kind of
// thing people download as a loose .exe.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AutoClicker;

/// <summary>
/// Every P/Invoke, struct and constant the app needs. Kept in one place so the
/// UI and engine files stay readable.
/// </summary>
internal static class Native
{
    // ---- Structures ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ---- SendInput ----

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    internal const int WHEEL_DELTA = 120;

    internal const uint MAPVK_VK_TO_VSC = 0;

    // ---- Window messages (background / PostMessage clicking) ----

    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206;
    internal const uint WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209;
    internal const uint WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    internal const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102;

    internal const int MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002, MK_MBUTTON = 0x0010;

    internal const int WM_HOTKEY = 0x0312;

    // ---- Hooks ----

    internal const int WH_MOUSE_LL = 14;
    internal const int WM_LBUTTONDOWN_LL = 0x0201;
    internal const int WM_RBUTTONDOWN_LL = 0x0204;

    // ---- Misc ----

    internal const uint GA_ROOT = 2;
    internal const uint TIMERR_NOERROR = 0;

    // ---- user32 ----

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr hWnd, [Out] char[] buf, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, [Out] char[] buf, int maxCount);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    // ---- gdi32 (pixel sampling) ----

    /// <summary>What GetPixel returns when the point is not on any display.</summary>
    internal const uint CLR_INVALID = 0xFFFFFFFF;

    [DllImport("gdi32.dll")]
    internal static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    // ---- kernel32 / winmm ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? name);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    internal static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    internal static extern uint TimeEndPeriod(uint ms);

    // ---- Helpers ----

    /// <summary>
    /// MAKELPARAM. Masking both halves to 16 bits matters on x64: packing a negative
    /// coordinate the naive way sign-extends and fills the whole upper dword with 1s.
    /// </summary>
    internal static IntPtr MakeLParam(int x, int y)
    {
        long packed = ((long)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF);
#pragma warning disable CA2020 // deliberate wrap-around: MAKELPARAM is a 32-bit bit-packing
        return unchecked((IntPtr)packed);
#pragma warning restore CA2020
    }

    internal static IntPtr MakeWParam(int low, int high)
    {
        long packed = ((long)(high & 0xFFFF) << 16) | (uint)(low & 0xFFFF);
#pragma warning disable CA2020 // deliberate wrap-around: MAKEWPARAM is a 32-bit bit-packing
        return unchecked((IntPtr)packed);
#pragma warning restore CA2020
    }

    internal static string ClassNameOf(IntPtr hWnd)
    {
        var buf = new char[256];
        int len = GetClassName(hWnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }

    internal static string TitleOf(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var buf = new char[len + 1];
        int copied = GetWindowText(hWnd, buf, buf.Length);
        return copied > 0 ? new string(buf, 0, copied) : "";
    }

    /// <summary>
    /// Raises the system timer resolution to 1 ms for the lifetime of the scope.
    /// Without this Thread.Sleep(1) actually sleeps ~15.6 ms, capping the click rate
    /// around 64/s no matter what interval the user asked for.
    /// </summary>
    internal sealed class TimerResolutionScope : IDisposable
    {
        private readonly bool raised;

        public TimerResolutionScope() => raised = TimeBeginPeriod(1) == TIMERR_NOERROR;

        public void Dispose()
        {
            if (raised && TimeEndPeriod(1) != TIMERR_NOERROR)
                System.Diagnostics.Debug.WriteLine("timeEndPeriod(1) failed");
        }
    }
}
