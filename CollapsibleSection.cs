namespace AutoClicker;

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
        // Force the AutoSize rows to re-measure now that the body is hidden…
        if (headerHost.Parent is TableLayoutPanel tlp) tlp.PerformLayout();
        if (headerHost.TopLevelControl is not Form f) return;
        f.PerformLayout();

        // …and re-fit the window itself: OnShown froze AutoSize (see that method), so
        // the form no longer follows the table on its own — without this, collapsing a
        // section left a large empty shell at the old frozen size. Form1 owns the
        // natural-size math (shared with OnShown); it no-ops until the freeze and when
        // the window is maximized/minimized.
        if (f is Form1 form1) form1.ReflowNaturalSize();
    }

    private void PositionGlyph()
    {
        Size pref = glyph.PreferredSize;
        // Anchor the glyph right after the HEADER TITLE, never to the live right edge:
        // pinning to ClientSize.Width inside an AutoSize/Dock=Fill GroupBox feeds the
        // glyph's own position back into the header's preferred width (+4 px per cycle),
        // which inflated every section to full form width (the +720 px regression) and
        // pushed full-row glyphs past the window edge where they were unclickable.
        int titleEnd = 8;
        if (headerHost is GroupBox gb && gb.Text.Length > 0)
            titleEnd = 8 + TextRenderer.MeasureText(gb.Text, gb.Font).Width + 8;
        glyph.Location = new Point(Math.Max(0, Math.Min(titleEnd, Math.Max(0, headerHost.ClientSize.Width - pref.Width))), 0);
    }
}
