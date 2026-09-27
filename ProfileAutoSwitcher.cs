namespace AutoClicker;

/// <summary>
/// Pure matching for per-app profile auto-switching (backlog #8): given the foreground
/// process name and each profile's target process, pick which profile to switch to. No
/// WinForms or process-walking here — the form's timer does the GetForegroundWindow /
/// Process.GetProcessById glue and calls this to decide.
/// </summary>
internal static class ProfileAutoSwitcher
{
    /// <summary>Normalizes a process name: trims, and drops a trailing ".exe" (case-insensitive).</summary>
    public static string NormalizeProcessName(string? name)
    {
        string t = (name ?? "").Trim();
        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            t = t[..^4].Trim();
        return t;
    }

    /// <summary>
    /// Index of the first profile whose <paramref name="targets"/> entry matches
    /// <paramref name="foregroundName"/> (case-insensitive, ".exe" ignored), or -1 when the
    /// foreground name is blank or nothing matches. Empty targets are inert.
    /// </summary>
    public static int ChooseTarget(IReadOnlyList<string> targets, string foregroundName)
    {
        string fg = NormalizeProcessName(foregroundName);
        if (fg.Length == 0) return -1;

        for (int i = 0; i < targets.Count; i++)
        {
            string target = NormalizeProcessName(targets[i]);
            if (target.Length == 0) continue;
            if (string.Equals(target, fg, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
