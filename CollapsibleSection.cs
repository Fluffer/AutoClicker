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

/// <summary>
/// Minimal-footprint collapsible section: overlays a clickable ▼/► glyph on the top-right
/// of a section's header and toggles the visibility of its body controls. The surrounding
/// TableLayoutPanel uses AutoSize rows, so hiding the body re-measures the row and the form
/// shrinks without any manual layout math.
/// </summary>
/// <remarks>
/// Wraps an existing <see cref="Control"/> already living in the root table. For a plain
/// <see cref="GroupBox"/> section that control is the GroupBox itself with one body child;
/// for the two side-by-side flow rows ("Click options+repeat", "Cursor/humanize") the glyph
/// sits on the row's first GroupBox and collapsing hides both GroupBoxes' bodies. The glyph
/// is added AFTER the body controls are captured, so callers pass body references that were
/// resolved before this ctor adds the glyph label.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The glyph Label is parented to headerHost and is disposed with it; the field only tracks it for toggle/position updates.")]
internal sealed class CollapsibleSection
{
    private readonly Control headerHost;
    private readonly Control[] body;
    private readonly Label glyph;

    public string Key { get; }

    /// <summary>True while the section's body controls are visible.</summary>
    public bool Expanded { get; private set; } = true;

    public CollapsibleSection(string key, Control headerHost, params Control[] body)
    {
        Key = key;
        this.headerHost = headerHost;
        this.body = body;

        glyph = new Label
        {
            Text = "▼",
            AutoSize = true,
            Cursor = Cursors.Hand,
            Margin = Padding.Empty,
            Padding = new Padding(2, 0, 2, 0),
        };
        glyph.Click += (_, _) => SetExpanded(!Expanded);

        headerHost.Controls.Add(glyph);
        glyph.BringToFront();
        headerHost.Resize += (_, _) => PositionGlyph();
        PositionGlyph();
    }

    public void SetExpanded(bool expanded)
    {
        if (Expanded == expanded) return;
        Expanded = expanded;
        glyph.Text = expanded ? "▼" : "►";
        foreach (Control c in body) c.Visible = expanded;
        Relayout();
    }

    private void Relayout()
    {
        // Force the AutoSize rows (and the form) to re-measure now that the body is hidden.
        if (headerHost.Parent is TableLayoutPanel tlp) tlp.PerformLayout();
        if (headerHost.TopLevelControl is Form f) f.PerformLayout();
    }

    private void PositionGlyph()
    {
        Size pref = glyph.PreferredSize;
        glyph.Location = new Point(Math.Max(0, headerHost.ClientSize.Width - pref.Width - 4), 0);
    }
}
