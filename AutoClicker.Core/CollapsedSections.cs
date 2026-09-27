namespace AutoClicker;

/// <summary>
/// Static parse/serialize for the persisted list of collapsed section keys. The value is a
/// plain semicolon-separated string in <see cref="AppSettings.CollapsedSections"/> so it
/// round-trips through settings.json without a format change, and is kept in this pure
/// class so the round-trip can be unit-tested without any WinForms controls.
/// </summary>
internal static class CollapsedSections
{
    /// <summary>Parses "interval;repeat" into a set of keys. Empty/whitespace segments are dropped.</summary>
    public static HashSet<string> Parse(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value)) return result;

        foreach (string raw in value.Split(';'))
        {
            string key = raw.Trim();
            if (key.Length > 0) result.Add(key);
        }
        return result;
    }

    /// <summary>Serializes a set of collapsed keys, sorted for a stable, diff-friendly file.</summary>
    public static string Serialize(IEnumerable<string> collapsedKeys)
    {
        IEnumerable<string> keys = collapsedKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal);
        return string.Join(';', keys);
    }
}
