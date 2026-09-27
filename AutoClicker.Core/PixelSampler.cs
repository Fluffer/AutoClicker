using System.Diagnostics;
using System.Drawing;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Reads screen pixel colours via GDI and compares them against a target colour, so a
/// sequence step can wait for something on screen to change (a button lighting up, a
/// progress bar finishing) instead of just sleeping a fixed amount of time.
/// </summary>
internal static class PixelSampler
{
    /// <summary>
    /// Reads the colour at a virtual-screen coordinate. Negative coordinates are legitimate:
    /// a secondary monitor placed left of or above the primary sits at negative virtual-screen
    /// coordinates, and this app already supports targeting those.
    /// </summary>
    public static bool TrySample(int screenX, int screenY, out Color color)
    {
        color = Color.Empty;

        // A screen DC (hWnd = IntPtr.Zero) covers the whole virtual desktop, not just the
        // primary monitor, which is what makes negative coordinates work.
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;

        try
        {
            uint clr = GetPixel(hdc, screenX, screenY);
            if (clr == CLR_INVALID) return false;

            // COLORREF is 0x00BBGGRR - byte order reversed from the usual RGB - so the low
            // byte is red and the high byte (after the unused top byte) is blue.
            byte r = (byte)(clr & 0xFF);
            byte g = (byte)((clr >> 8) & 0xFF);
            byte b = (byte)((clr >> 16) & 0xFF);
            color = Color.FromArgb(r, g, b);
            return true;
        }
        finally
        {
            // Releasing the screen DC is mandatory: this runs inside a polling loop, and a
            // leaked screen DC is a process-wide GDI handle leak.
            _ = ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>
    /// Per-channel comparison rather than Euclidean distance, because that's what a user
    /// setting "tolerance 10" intuitively expects.
    /// </summary>
    public static bool Matches(Color sampled, int targetRgb, int tolerance)
    {
        tolerance = Math.Clamp(tolerance, 0, 255);
        Color target = FromRgb(targetRgb);
        return Math.Abs(sampled.R - target.R) <= tolerance
            && Math.Abs(sampled.G - target.G) <= tolerance
            && Math.Abs(sampled.B - target.B) <= tolerance;
    }

    /// <summary>
    /// Polls a screen point until its match state against <paramref name="targetRgb"/> equals
    /// <paramref name="wantMatch"/>. A failed sample (window transiently off-screen) counts as
    /// "not matching" rather than aborting the wait.
    /// </summary>
    public static bool WaitUntil(int screenX, int screenY, int targetRgb, int tolerance,
                                 bool wantMatch, int timeoutMs, int pollMs, Func<bool> keepGoing)
    {
        // Floor the poll interval so a bad value (0, negative) can't spin a core.
        pollMs = Math.Max(pollMs, 10);

        // A Stopwatch measures elapsed wall-clock time regardless of how long each sample or
        // sleep actually took; accumulating expected sleep durations drifts under load.
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (!keepGoing()) return false;

            bool isMatch = TrySample(screenX, screenY, out Color sampled) && Matches(sampled, targetRgb, tolerance);
            if (isMatch == wantMatch) return true;

            if (timeoutMs > 0 && sw.ElapsedMilliseconds >= timeoutMs) return false;

            Thread.Sleep(pollMs);
        }
    }

    /// <summary>Converts a <see cref="Color"/> to its <c>0xRRGGBB</c> on-disk representation.</summary>
    public static int ToRgb(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    /// <summary>Converts a <c>0xRRGGBB</c> value back to a <see cref="Color"/>.</summary>
    public static Color FromRgb(int rgb) =>
        Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    /// <summary>Display string for the UI, e.g. <c>#1E90FF</c>.</summary>
    public static string DescribeRgb(int rgb) => $"#{rgb & 0xFFFFFF:X6}";
}
