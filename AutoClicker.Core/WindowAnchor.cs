using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Ties a point to the window it was recorded in, so a sequence keeps working after
/// the target window is moved, resized or reopened — the usual reason a saved
/// <c>.acseq</c> silently starts clicking the wrong thing.
/// </summary>
internal static class WindowAnchor
{
    /// <summary>
    /// Identifies the top-level window under a screen point and converts the point to
    /// that window's client coordinates. Returns false when there is no usable window.
    /// </summary>
    public static bool Capture(POINT screenPt, out string cls, out string title, out POINT clientPt)
    {
        cls = title = "";
        clientPt = screenPt;

        IntPtr hwnd = WindowFromPoint(screenPt);
        if (hwnd == IntPtr.Zero) return false;

        // WindowFromPoint can land on a child control; anchor to the top-level window,
        // whose class and title are the parts that stay stable.
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        var pt = screenPt;
        if (!ScreenToClient(root, ref pt)) return false;

        cls = ClassNameOf(root);
        title = TitleOf(root);
        clientPt = pt;
        return cls.Length > 0 || title.Length > 0;
    }

    /// <summary>
    /// Finds the live window matching a stored class/title and maps client coordinates
    /// back to the screen. Returns false when the window is gone.
    /// </summary>
    public static bool Resolve(SeqAction a, int clientX, int clientY, out POINT screenPt)
    {
        screenPt = default;

        IntPtr hwnd = a.ResolvedWindow;
        if (!StillUsable(hwnd, a.WindowClass))
        {
            hwnd = Find(a.WindowClass, a.WindowTitle);
            a.ResolvedWindow = hwnd;
        }
        if (hwnd == IntPtr.Zero) return false;

        var pt = new POINT { X = clientX, Y = clientY };
        if (!ClientToScreen(hwnd, ref pt)) return false;

        screenPt = pt;
        return true;
    }

    /// <summary>
    /// A cached handle is only trusted while the window still exists, is still visible
    /// and still has the class it was found by — handles get recycled.
    /// </summary>
    private static bool StillUsable(IntPtr hwnd, string cls)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        return cls.Length == 0 || string.Equals(ClassNameOf(hwnd), cls, StringComparison.Ordinal);
    }

    /// <summary>
    /// Best visible top-level window for a class/title pair. Titles change constantly
    /// (documents, tab names, unsaved markers), so an exact match is preferred but a
    /// prefix or substring match — all still title-based signals that the window really
    /// is the recorded one — is accepted before giving up.
    /// </summary>
    /// <remarks>
    /// Deliberately NO class-only fallback when a title was recorded: resolving "Chrome,
    /// Gmail — Inbox" to whatever Chrome window happens to be topmost after that window
    /// closes would send clicks to the wrong window, which for an auto-clicker is a
    /// data-destruction bug, not an inconvenience. The step now fails loudly instead
    /// (the runner reports "anchor window not open"). Class-only matching remains only
    /// for anchors that never recorded a title in the first place — there it is the
    /// primary signal, not a fallback.
    /// </remarks>
    public static IntPtr Find(string cls, string title)
    {
        IntPtr exact = IntPtr.Zero, prefix = IntPtr.Zero, contains = IntPtr.Zero;
        bool haveCls = !string.IsNullOrEmpty(cls);
        bool haveTitle = !string.IsNullOrEmpty(title);
        if (!haveCls && !haveTitle) return IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            if (haveCls && !string.Equals(ClassNameOf(hwnd), cls, StringComparison.Ordinal))
                return true;

            // No title recorded: the class (plus visibility) is the whole match.
            if (!haveTitle)
            {
                if (exact == IntPtr.Zero) exact = hwnd;
                return true;
            }

            string t = TitleOf(hwnd);
            if (t.Length == 0) return true; // untitled window cannot match a recorded title

            if (string.Equals(t, title, StringComparison.Ordinal))
            {
                exact = hwnd;
                return false; // nothing will beat this
            }
            if (prefix == IntPtr.Zero && t.StartsWith(title, StringComparison.OrdinalIgnoreCase))
                prefix = hwnd;
            else if (contains == IntPtr.Zero && t.Contains(title, StringComparison.OrdinalIgnoreCase))
                contains = hwnd;

            return true;
        }, IntPtr.Zero);

        if (exact != IntPtr.Zero) return exact;
        if (prefix != IntPtr.Zero) return prefix;
        if (contains != IntPtr.Zero) return contains;
        return IntPtr.Zero; // recorded title exists but no window matches it: fail loudly
    }
}
