using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Add/edit dialog for one <see cref="SeqAction"/>. Shows only the fields that the
/// selected action kind actually uses.
/// </summary>
internal sealed class ActionEditorForm : Form
{
    private readonly SeqAction action;
    private readonly List<SeqAction>? sequence;

    private readonly ComboBox cmbKind = NewCombo(150);
    private readonly NumericUpDown numX = NewNum(-100000, 100000);
    private readonly NumericUpDown numY = NewNum(-100000, 100000);
    private readonly Button btnPick = new() { Text = "Pick (3s)…", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
    private readonly Button btnProbe = new() { Text = "What's here?", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
    private readonly CheckBox chkAnchor = new() { Text = "Anchor to the window it's in", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label lblAnchor = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 3) };
    private readonly ComboBox cmbClickMethod = NewCombo(280,
        "Real input (moves the cursor)",
        "Background messages (no cursor movement)",
        "UI Automation invoke (no cursor movement)");
    private readonly Label lblClickMethodHint = new() { AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };
    private readonly ComboBox cmbButton = NewCombo(150, "Left", "Right", "Middle");
    private readonly ComboBox cmbClickType = NewCombo(150, "Single", "Double");
    private readonly NumericUpDown numHold = NewNum(0, 600000);
    private readonly NumericUpDown numEndX = NewNum(-100000, 100000);
    private readonly NumericUpDown numEndY = NewNum(-100000, 100000);
    private readonly NumericUpDown numDragMs = NewNum(0, 600000);
    private readonly NumericUpDown numNotches = NewNum(-1000, 1000);
    private readonly CheckBox chkHorizontal = new() { Text = "Horizontal (shift-wheel)", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly TextBox txtCombo = new() { Width = 200, Margin = new Padding(3, 4, 3, 3) };
    private readonly Label lblComboHint = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };
    private readonly TextBox txtText = new() { Width = 260, Height = 80, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Margin = new Padding(3, 4, 3, 3) };
    private readonly NumericUpDown numDelay = NewNum(0, int.MaxValue);
    private readonly TextBox txtComment = new() { Width = 260, Height = 40, Multiline = true, ScrollBars = ScrollBars.Vertical, Margin = new Padding(3, 4, 3, 3) };

    // ---- Self-healing selector (Click only) ----
    private readonly TextBox txtSelector = new() { ReadOnly = true, Width = 320, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox chkPreferSelector = new() { Text = "Self-heal: find this control by its selector if the point drifts", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    // ---- Pixel condition / WaitPixel ----
    private readonly ComboBox cmbCondition = NewCombo(260);
    private readonly NumericUpDown numCondX = NewNum(-100000, 100000);
    private readonly NumericUpDown numCondY = NewNum(-100000, 100000);
    private readonly Button btnPickPixel = new() { Text = "Pick pixel (3s)…", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
    private readonly Panel pnlSwatch = new() { Size = new Size(24, 24), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3, 4, 3, 3) };
    private readonly Label lblSwatchText = new() { AutoSize = true, Margin = new Padding(6, 7, 3, 3) };
    private readonly NumericUpDown numTolerance = NewNum(0, 255);
    private readonly Label lblToleranceHint = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };
    private readonly NumericUpDown numPixelTimeout = NewNum(0, 3600000);
    private readonly CheckBox chkAbortOnTimeout = new() { Text = "Stop the whole run if it times out", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly NumericUpDown numPollInterval = NewNum(10, 60000);

    // ---- Control flow ----
    private readonly NumericUpDown numRepeatCount = NewNum(0, 1_000_000);
    private readonly Label lblRepeatHint = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };
    private readonly TextBox txtCondition = new() { Width = 260, Margin = new Padding(3, 4, 3, 3) };
    private readonly Label lblConditionHint = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };
    private readonly TextBox txtVarName = new() { Width = 200, Margin = new Padding(3, 4, 3, 3) };
    private readonly TextBox txtValueExpr = new() { Width = 260, Margin = new Padding(3, 4, 3, 3) };
    private readonly Label lblVarHint = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };
    private readonly ComboBox cmbLabel = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 200, Margin = new Padding(3, 4, 3, 3) };

    // ---- Visual targeting (FindImage / FindText) ----
    private readonly Button btnCaptureTemplate = new() { Text = "Capture template (3s)…", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
    private readonly Button btnBrowseTemplate = new() { Text = "Browse image…", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
    private readonly PictureBox picTemplate = new() { SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Size = new Size(96, 96), Margin = new Padding(3, 4, 3, 3) };
    private readonly NumericUpDown numTemplateSize = NewNum(8, 256);
    private readonly NumericUpDown numThreshold = new() { Minimum = 0.5m, Maximum = 1.0m, DecimalPlaces = 2, Increment = 0.05m, Width = 90, Margin = new Padding(3, 4, 3, 3) };
    private readonly NumericUpDown numSearchX = NewNum(-100000, 100000);
    private readonly NumericUpDown numSearchY = NewNum(-100000, 100000);
    private readonly NumericUpDown numSearchW = NewNum(0, 100000);
    private readonly NumericUpDown numSearchH = NewNum(0, 100000);
    private readonly Label lblSearchHint = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };
    private readonly CheckBox chkClickOnFound = new() { Text = "Click the found target", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly NumericUpDown numClickOffsetX = NewNum(-100000, 100000);
    private readonly NumericUpDown numClickOffsetY = NewNum(-100000, 100000);
    private readonly TextBox txtTextQuery = new() { Width = 260, Margin = new Padding(3, 4, 3, 3) };
    private readonly CheckBox chkRegexQuery = new() { Text = "Treat as regular expression", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label lblVisualHint = new() { AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.DimGray, Margin = new Padding(3, 2, 3, 3) };

    private readonly List<(Control label, Control field, Func<ActionKind, bool> visibleFor)> rows = new();
    private readonly Button btnOk;

    private System.Windows.Forms.Timer? pickTimer;
    private int pickCountdown;
    private System.Windows.Forms.Timer? pickPixelTimer;
    private int pickPixelCountdown;
    // Second phase of the pick: cursor parked away from the target, waiting for it to
    // repaint its resting colour before the sample is taken.
    private bool pickPixelSettling;
    private System.Windows.Forms.Timer? captureTemplateTimer;
    private int captureTemplateCountdown;

    // The FindImage template held by the editor. Kept as PNG bytes (not a Bitmap) so it
    // round-trips through the .acseq format exactly; the PictureBox shows a decoded preview.
    private byte[]? currentTemplate;

    // Window anchoring keeps both representations so toggling the checkbox can convert
    // between them instead of throwing the coordinates away.
    private string anchorClass;
    private string anchorTitle;

    public SeqAction Result => action;

    public ActionEditorForm(SeqAction source, string title, List<SeqAction>? sequence = null)
    {
        action = source.Clone();
        this.sequence = sequence;
        anchorClass = action.WindowClass;
        anchorTitle = action.WindowTitle;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(12);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(root);

        cmbKind.Items.AddRange(new object[]
        {
            "Click", "Drag", "Scroll", "Key press", "Type text", "Wait only", "Wait for pixel",
            "Repeat (loop)", "End block", "If (conditional)", "Set variable", "Break loop",
            "Goto label", "Label", "Find image", "Find text",
        });
        cmbKind.SelectedIndex = (int)action.Kind;
        cmbKind.SelectedIndexChanged += (_, _) =>
        {
            // The condition wording means something different for WaitPixel (it's the wait
            // itself) versus every other kind (it's a gate), so the combo text is rebuilt
            // whenever the kind crosses that line.
            RebuildConditionLabels();
            ApplyKindVisibility();
            UpdateClickMethodHint();
        };

        var posFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        posFlow.Controls.Add(numX);
        posFlow.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        posFlow.Controls.Add(numY);
        posFlow.Controls.Add(btnPick);
        posFlow.Controls.Add(btnProbe);
        btnPick.Click += (_, _) => StartPick();
        btnProbe.Click += (_, _) => ProbeAt();

        var endFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        endFlow.Controls.Add(numEndX);
        endFlow.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        endFlow.Controls.Add(numEndY);

        var condPosFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        condPosFlow.Controls.Add(numCondX);
        condPosFlow.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        condPosFlow.Controls.Add(numCondY);
        condPosFlow.Controls.Add(btnPickPixel);
        btnPickPixel.Click += (_, _) => StartPickPixel();

        var swatchFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        swatchFlow.Controls.Add(pnlSwatch);
        swatchFlow.Controls.Add(lblSwatchText);

        var templateFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        templateFlow.Controls.Add(btnCaptureTemplate);
        templateFlow.Controls.Add(btnBrowseTemplate);
        btnCaptureTemplate.Click += (_, _) => StartCaptureTemplate();
        btnBrowseTemplate.Click += (_, _) => BrowseTemplate();

        var searchPosFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        searchPosFlow.Controls.Add(numSearchX);
        searchPosFlow.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        searchPosFlow.Controls.Add(numSearchY);

        var searchSizeFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        searchSizeFlow.Controls.Add(numSearchW);
        searchSizeFlow.Controls.Add(new Label { Text = "H", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        searchSizeFlow.Controls.Add(numSearchH);

        var clickOffsetFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        clickOffsetFlow.Controls.Add(numClickOffsetX);
        clickOffsetFlow.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        clickOffsetFlow.Controls.Add(numClickOffsetY);

        AddRow(root, "Action:", cmbKind, _ => true);
        AddRow(root, "Position  X", posFlow, k => UsesPosition(k));
        AddRow(root, "", chkAnchor, k => UsesPosition(k));
        AddRow(root, "", lblAnchor, k => UsesPosition(k) && chkAnchor.Checked);
        AddRow(root, "Click method:", cmbClickMethod, k => UsesPosition(k));
        AddRow(root, "", lblClickMethodHint, k => UsesPosition(k));
        AddRow(root, "Mouse button:", cmbButton, k => k is ActionKind.Click or ActionKind.Drag);
        AddRow(root, "Click type:", cmbClickType, k => k is ActionKind.Click);
        AddRow(root, "Hold button (ms):", numHold, k => k is ActionKind.Click);
        AddRow(root, "Recorded selector:", txtSelector, k => k == ActionKind.Click);
        AddRow(root, "", chkPreferSelector, k => k == ActionKind.Click);
        AddRow(root, "Drag to  X", endFlow, k => k is ActionKind.Drag);
        AddRow(root, "Drag duration (ms):", numDragMs, k => k is ActionKind.Drag);
        AddRow(root, "Wheel notches:", numNotches, k => k is ActionKind.Scroll);
        AddRow(root, "", chkHorizontal, k => k is ActionKind.Scroll);
        AddRow(root, "Key combo:", txtCombo, k => k is ActionKind.Key);
        AddRow(root, "", lblComboHint, k => k is ActionKind.Key);
        AddRow(root, "Text:", txtText, k => k is ActionKind.Text);

        // Pixel condition rows apply to every input kind: any action can be gated on a pixel
        // colour, and a WaitPixel action IS the wait built from these same fields. Control-flow
        // kinds ignore gates entirely, so the rows hide for them (a gate there would do nothing);
        // FindImage/FindText are their own wait and hide them too.
        AddRow(root, "Pixel condition:", cmbCondition, UsesPixelCondition);
        AddRow(root, "Pixel point  X", condPosFlow, UsesPixelCondition);
        AddRow(root, "Pixel color:", swatchFlow, UsesPixelCondition);
        AddRow(root, "Tolerance:", numTolerance, UsesPixelCondition);
        AddRow(root, "", lblToleranceHint, UsesPixelCondition);
        AddRow(root, "Wait timeout (ms, 0 = forever):", numPixelTimeout, k => SeqAction.IsWaitKind(k));
        AddRow(root, "", chkAbortOnTimeout, k => SeqAction.IsWaitKind(k));
        AddRow(root, "Check every (ms):", numPollInterval, k => SeqAction.IsWaitKind(k));

        // Control-flow rows: each new kind shows only the fields it uses.
        AddRow(root, "Repeat count:", numRepeatCount, k => k == ActionKind.Repeat);
        AddRow(root, "", lblRepeatHint, k => k == ActionKind.Repeat);
        AddRow(root, "Condition:", txtCondition, k => k == ActionKind.IfElse);
        AddRow(root, "", lblConditionHint, k => k == ActionKind.IfElse);
        AddRow(root, "Variable name:", txtVarName, k => k == ActionKind.SetVar);
        AddRow(root, "Value:", txtValueExpr, k => k == ActionKind.SetVar);
        AddRow(root, "", lblVarHint, k => k == ActionKind.SetVar);
        AddRow(root, "Label:", cmbLabel, k => k is ActionKind.GotoLabel or ActionKind.Label);

        // Visual-targeting rows.
        AddRow(root, "Template:", templateFlow, k => k == ActionKind.FindImage);
        AddRow(root, "Template size (px):", numTemplateSize, k => k == ActionKind.FindImage);
        AddRow(root, "", picTemplate, k => k == ActionKind.FindImage);
        AddRow(root, "", lblVisualHint, k => k == ActionKind.FindImage);
        AddRow(root, "Match threshold:", numThreshold, k => k == ActionKind.FindImage);
        AddRow(root, "Find text:", txtTextQuery, k => k == ActionKind.FindText);
        AddRow(root, "", chkRegexQuery, k => k == ActionKind.FindText);
        AddRow(root, "Search area  X", searchPosFlow, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow(root, "Search size  W", searchSizeFlow, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow(root, "", lblSearchHint, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow(root, "", chkClickOnFound, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow(root, "Click offset  X", clickOffsetFlow, k => k is ActionKind.FindImage or ActionKind.FindText);

        AddRow(root, "Wait after (ms):", numDelay, _ => true);
        AddRow(root, "Comment (optional):", txtComment, _ => true);

        numX.Value = action.X;
        numY.Value = action.Y;
        chkAnchor.Checked = action.WindowRelative;
        cmbClickMethod.SelectedIndex = Math.Clamp(action.ClickMethod, 0, 2);
        cmbButton.SelectedIndex = Math.Clamp(action.Button, 0, 2);
        cmbClickType.SelectedIndex = action.DoubleClick ? 1 : 0;
        numHold.Value = action.HoldMs;
        txtSelector.Text = SelectorSummary(action);
        chkPreferSelector.Checked = action.PreferSelector;
        numEndX.Value = action.EndX;
        numEndY.Value = action.EndY;
        numDragMs.Value = action.DragMs;
        numNotches.Value = Math.Clamp(action.ScrollNotches, -1000, 1000);
        chkHorizontal.Checked = action.Horizontal;
        txtCombo.Text = action.KeyCombo;
        txtText.Text = action.Text;
        numDelay.Value = action.DelayMs;
        txtComment.Text = action.Comment;

        cmbCondition.Items.AddRange(ConditionLabels(SelectedKind));
        cmbCondition.SelectedIndex = (int)action.Condition;
        numCondX.Value = action.CondX;
        numCondY.Value = action.CondY;
        pnlSwatch.BackColor = PixelSampler.FromRgb(action.CondColor);
        lblSwatchText.Text = action.DescribeColor();
        numTolerance.Value = action.CondTolerance;
        lblToleranceHint.Text = "0 = exact match, higher allows more drift.\r\n"
                             + "Picking moves the cursor aside before sampling, so the colour is the\r\n"
                             + "target's resting state rather than its hover highlight.";
        numPixelTimeout.Value = Math.Clamp(action.PixelTimeoutMs, numPixelTimeout.Minimum, numPixelTimeout.Maximum);
        chkAbortOnTimeout.Checked = action.AbortRunOnTimeout;
        numPollInterval.Value = action.PollIntervalMs;

        numRepeatCount.Value = Math.Clamp(action.RepeatCount, numRepeatCount.Minimum, numRepeatCount.Maximum);
        lblRepeatHint.Text = "0 = loop until Break.";
        txtCondition.Text = action.ConditionExpr;
        lblConditionHint.Text = "e.g. counter < 5";
        txtVarName.Text = action.VarName;
        txtValueExpr.Text = action.ValueExpr;
        lblVarHint.Text = "e.g. counter = counter + 1";
        PopulateLabels();
        cmbLabel.Text = action.Label;

        // Visual targeting.
        currentTemplate = action.TemplatePng;
        numTemplateSize.Value = 60;
        numThreshold.Value = Math.Clamp((decimal)action.MatchThreshold, numThreshold.Minimum, numThreshold.Maximum);
        numSearchX.Value = action.SearchX;
        numSearchY.Value = action.SearchY;
        numSearchW.Value = action.SearchW;
        numSearchH.Value = action.SearchH;
        chkClickOnFound.Checked = action.ClickOnFound;
        numClickOffsetX.Value = action.ClickOffsetX;
        numClickOffsetY.Value = action.ClickOffsetY;
        txtTextQuery.Text = action.TextQuery;
        chkRegexQuery.Checked = action.RegexQuery;
        lblSearchHint.Text = "0×0 = search the full screen. Screen coordinates; a monitor left of the primary is negative.";
        lblVisualHint.Text = "Captures a fixed-size square centred on the cursor when the countdown ends.\r\n"
                          + "The template must be captured at the same display scale it is replayed at.";
        UpdateTemplatePreview();

        chkAnchor.CheckedChanged += (_, _) => AnchorToggled();
        cmbClickMethod.SelectedIndexChanged += (_, _) => UpdateClickMethodHint();
        txtCombo.TextChanged += (_, _) => ValidateCombo();
        // Hand-editing the coordinates must re-sample, or the swatch would silently
        // disagree with what CondX/CondY now point at.
        numCondX.ValueChanged += (_, _) => UpdateColorSwatch();
        numCondY.ValueChanged += (_, _) => UpdateColorSwatch();

        btnOk = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(80, 30), DialogResult = DialogResult.OK, Margin = new Padding(3) };
        var btnCancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 30), DialogResult = DialogResult.Cancel, Margin = new Padding(3) };
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnOk);
        int buttonRow = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(buttons, 0, buttonRow);
        root.SetColumnSpan(buttons, 2);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
        FormClosing += OnClosingValidate;

        UpdateAnchorLabel();
        ValidateCombo();
        ApplyKindVisibility();
        UpdateClickMethodHint();
    }

    private static bool UsesPosition(ActionKind k) => k is ActionKind.Click or ActionKind.Drag or ActionKind.Scroll;

    /// <summary>
    /// Whether a kind shows the pixel-condition rows. WaitPixel uses them (it IS the wait),
    /// but FindImage/FindText do not — their own success criterion replaces the gate.
    /// </summary>
    private static bool UsesPixelCondition(ActionKind k) =>
        !SeqAction.IsControlKind(k) && k is not ActionKind.FindImage and not ActionKind.FindText;

    private ActionKind SelectedKind => (ActionKind)Math.Max(0, cmbKind.SelectedIndex);

    private void AddRow(TableLayoutPanel root, string labelText, Control field, Func<ActionKind, bool> visibleFor)
    {
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };

        // RowCount must be grown explicitly and a matching AutoSize RowStyle added. Reading
        // RowCount without incrementing it hands every row the same index, and the panel then
        // scatters the overflow into arbitrary cells instead of stacking label/field pairs.
        // The AutoSize style is also what lets a row collapse to nothing once every control
        // in it is hidden, which is how the dialog shrinks to the selected action kind.
        int row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(label, 0, row);
        root.Controls.Add(field, 1, row);
        rows.Add((label, field, visibleFor));
    }

    private void ApplyKindVisibility()
    {
        var kind = SelectedKind;
        foreach (var (label, field, visibleFor) in rows)
        {
            bool show = visibleFor(kind);
            label.Visible = show;
            field.Visible = show;
        }
        // An AutoSize row collapses only once every control in it is hidden, which is
        // what makes the dialog shrink to the selected kind.
        PerformLayout();
    }

    // ---- Click method wording ----

    /// <summary>
    /// Rebuilds the click-method hint for the current method + kind. The wording differs
    /// per method because the trade-offs are genuinely confusing and a wrong choice looks
    /// like "the app is broken" rather than "this window doesn't support that method".
    /// </summary>
    private void UpdateClickMethodHint()
    {
        lblClickMethodHint.Text = cmbClickMethod.SelectedIndex switch
        {
            0 => "Works everywhere, but moves the physical cursor.",
            1 => "No cursor movement; works on classic Win32 windows but is ignored by " +
                 "Chrome/Electron, WPF, UWP/WinUI and elevated windows.",
            2 => BuildUiaHint(SelectedKind),
            _ => "",
        };
    }

    private static string BuildUiaHint(ActionKind kind)
    {
        if (!UiaInvoker.IsAvailable)
            return "UI Automation is not available on this machine — the engine will fall back to real input.";

        if (kind is ActionKind.Drag or ActionKind.Scroll)
            return "UI Automation has no drag/scroll equivalent, so the engine falls back to background messages for this action.";

        return "No cursor movement; works on WPF, UWP/WinUI and Chrome. A UIA invoke " +
               "activates the control directly, so button choice, double-click and hold time are ignored.";
    }

    /// <summary>The recorded self-healing selector as one readable line, or a note that none exists.</summary>
    private static string SelectorSummary(SeqAction a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.SelAutomationId)) parts.Add("AutomationId=" + a.SelAutomationId);
        if (!string.IsNullOrEmpty(a.SelName)) parts.Add("Name=" + a.SelName);
        if (!string.IsNullOrEmpty(a.SelClass)) parts.Add("Class=" + a.SelClass);
        return parts.Count == 0 ? "(none — record a click to capture one)" : string.Join("   ", parts);
    }

    /// <summary>
    /// Probes what UI Automation sees at the action's current target, before ever running
    /// it — otherwise whether a target is reachable by UIA is pure trial and error.
    /// </summary>
    private void ProbeAt()
    {
        int x = (int)numX.Value;
        int y = (int)numY.Value;

        if (chkAnchor.Checked)
        {
            var probe = new SeqAction { WindowClass = anchorClass, WindowTitle = anchorTitle };
            if (!WindowAnchor.Resolve(probe, x, y, out POINT screenPt))
            {
                lblClickMethodHint.Text = "Window not currently open — can't probe.";
                return;
            }
            x = screenPt.X;
            y = screenPt.Y;
        }

        lblClickMethodHint.Text = UiaInvoker.DescribeAt(x, y) ?? "Nothing found there.";
    }

    // ---- Pixel condition wording ----

    /// <summary>Rebuilds the condition combo's wording for the current kind, keeping the selection.</summary>
    private void RebuildConditionLabels()
    {
        int keep = Math.Max(cmbCondition.SelectedIndex, 0);
        cmbCondition.Items.Clear();
        cmbCondition.Items.AddRange(ConditionLabels(SelectedKind));
        cmbCondition.SelectedIndex = keep;
    }

    private static object[] ConditionLabels(ActionKind kind) => kind == ActionKind.WaitPixel
        ? new object[] { "(nothing — no-op)", "Wait until the pixel matches", "Wait until the pixel stops matching" }
        : new object[] { "Always run this action", "Only if the pixel matches", "Only if the pixel does NOT match" };

    /// <summary>Keeps the swatch honest with CondX/CondY: called after either changes.</summary>
    private void UpdateColorSwatch()
    {
        if (PixelSampler.TrySample((int)numCondX.Value, (int)numCondY.Value, out Color c))
        {
            pnlSwatch.BackColor = c;
            lblSwatchText.Text = PixelSampler.DescribeRgb(PixelSampler.ToRgb(c));
        }
        else
        {
            // Point is off every display: keep showing the last known colour rather than
            // guessing, and say so instead of crashing on the failed sample.
            lblSwatchText.Text = "#—— (off-screen)";
        }
    }

    // ---- Position picking ----

    private void StartPick()
    {
        pickCountdown = 3;
        btnPick.Enabled = false;
        pickTimer?.Dispose();
        pickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        btnPick.Text = $"{pickCountdown}…";
        pickTimer.Tick += (_, _) =>
        {
            if (--pickCountdown > 0) { btnPick.Text = $"{pickCountdown}…"; return; }

            pickTimer!.Stop();
            pickTimer.Dispose();
            pickTimer = null;
            btnPick.Text = "Pick (3s)…";
            btnPick.Enabled = true;

            GetCursorPos(out POINT p);
            CaptureAt(p);
        };
        pickTimer.Start();
    }

    /// <summary>
    /// Same countdown idiom as <see cref="StartPick"/>, but captures a point AND its colour
    /// in one grab: the user hovers the thing they care about and gets both at once.
    /// </summary>
    private void StartPickPixel()
    {
        pickPixelCountdown = 3;
        pickPixelSettling = false;
        btnPickPixel.Enabled = false;
        pickPixelTimer?.Dispose();
        pickPixelTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        btnPickPixel.Text = $"{pickPixelCountdown}…";
        pickPixelTimer.Tick += (_, _) =>
        {
            if (!pickPixelSettling)
            {
                if (--pickPixelCountdown > 0) { btnPickPixel.Text = $"{pickPixelCountdown}…"; return; }

                // The coordinates come from where the user is pointing...
                GetCursorPos(out POINT p);
                numCondX.Value = Math.Clamp(p.X, numCondX.Minimum, numCondX.Maximum);
                numCondY.Value = Math.Clamp(p.Y, numCondY.Minimum, numCondY.Maximum);

                // ...but the COLOUR must not be sampled while the cursor is still sitting on
                // it. Anything that highlights on hover — buttons, links, menu items, list
                // rows — would be captured in its hover colour, which never recurs at run
                // time when the cursor is elsewhere, so the condition could never match. A
                // themed button measures #E0EEF9 hovered against #FDFDFD at rest: far
                // outside any sane tolerance. Park the cursor on this dialog and let the
                // target repaint its resting state before sampling.
                SetCursorPos(Left + (Width / 2), Top + (Height / 2));
                pickPixelSettling = true;
                btnPickPixel.Text = "sampling…";
                pickPixelTimer!.Interval = SettleMs;
                return;
            }

            pickPixelTimer!.Stop();
            pickPixelTimer.Dispose();
            pickPixelTimer = null;
            pickPixelSettling = false;
            btnPickPixel.Text = "Pick pixel (3s)…";
            btnPickPixel.Enabled = true;

            UpdateColorSwatch();
        };
        pickPixelTimer.Start();
    }

    /// <summary>
    /// How long to wait after moving the cursor away before sampling. The target repaints on
    /// its own message loop, in another process, so this only has to outlast a redraw.
    /// </summary>
    private const int SettleMs = 250;

    private void CaptureAt(POINT screenPt)
    {
        if (chkAnchor.Checked && WindowAnchor.Capture(screenPt, out string cls, out string title, out POINT clientPt))
        {
            anchorClass = cls;
            anchorTitle = title;
            SetXY(clientPt);
        }
        else
        {
            SetXY(screenPt);
        }
        UpdateAnchorLabel();
    }

    private void SetXY(POINT p)
    {
        numX.Value = Math.Clamp(p.X, numX.Minimum, numX.Maximum);
        numY.Value = Math.Clamp(p.Y, numY.Minimum, numY.Maximum);
    }

    // ---- Template capture / load ----

    /// <summary>
    /// Same countdown idiom as <see cref="StartPick"/>/<see cref="StartPickPixel"/>. When it
    /// ends, a fixed-size square centred on the cursor is captured as the template — the
    /// pragmatic alternative to a drag-select overlay (a full selection rectangle needs a
    /// borderless overlay form to be usable, which is out of scope for this phase). The size
    /// is adjustable, and a PNG can also be loaded instead.
    /// </summary>
    private void StartCaptureTemplate()
    {
        captureTemplateCountdown = 3;
        btnCaptureTemplate.Enabled = false;
        captureTemplateTimer?.Dispose();
        captureTemplateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        btnCaptureTemplate.Text = $"{captureTemplateCountdown}…";
        captureTemplateTimer.Tick += (_, _) =>
        {
            if (--captureTemplateCountdown > 0) { btnCaptureTemplate.Text = $"{captureTemplateCountdown}…"; return; }

            captureTemplateTimer!.Stop();
            captureTemplateTimer.Dispose();
            captureTemplateTimer = null;
            btnCaptureTemplate.Text = "Capture template (3s)…";
            btnCaptureTemplate.Enabled = true;

            GetCursorPos(out POINT p);
            CaptureTemplateAt(p);
        };
        captureTemplateTimer.Start();
    }

    private void CaptureTemplateAt(POINT cursor)
    {
        int size = (int)numTemplateSize.Value;
        int half = size / 2;
        var region = new Rectangle(cursor.X - half, cursor.Y - half, size, size);
        try
        {
            using Bitmap bmp = ImageMatcher.CaptureRegion(region);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            currentTemplate = ms.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                   or System.ComponentModel.Win32Exception or ExternalException)
        {
            MessageBox.Show(this, ex.Message, "Template capture failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        UpdateTemplatePreview();
    }

    /// <summary>Loads a PNG from disk instead of capturing, for templates the user already has.</summary>
    private void BrowseTemplate()
    {
        using var ofd = new OpenFileDialog { Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*", Title = "Load template image" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            byte[] bytes = File.ReadAllBytes(ofd.FileName);
            using var probe = new Bitmap(new MemoryStream(bytes)); // validate it decodes
            currentTemplate = bytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Template load failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        UpdateTemplatePreview();
    }

    private void UpdateTemplatePreview()
    {
        if (currentTemplate is null || currentTemplate.Length == 0)
        {
            picTemplate.Image = null;
            picTemplate.BackColor = SystemColors.Control;
            return;
        }
        try
        {
            using var ms = new MemoryStream(currentTemplate);
            using var bmp = new Bitmap(ms);
            picTemplate.Image = new Bitmap(bmp); // keep a copy: the source stream is disposed
            picTemplate.BackColor = SystemColors.Control;
        }
        catch (ArgumentException)
        {
            picTemplate.Image = null;
            picTemplate.BackColor = SystemColors.Control;
        }
    }

    /// <summary>Converts the coordinates already on screen rather than discarding them.</summary>
    private void AnchorToggled()
    {
        var current = new POINT { X = (int)numX.Value, Y = (int)numY.Value };

        if (chkAnchor.Checked)
        {
            if (WindowAnchor.Capture(current, out string cls, out string title, out POINT clientPt))
            {
                anchorClass = cls;
                anchorTitle = title;
                SetXY(clientPt);
            }
            else
            {
                lblAnchor.Text = "No window at that point — pick a position first.";
            }
        }
        else if (anchorClass.Length > 0 || anchorTitle.Length > 0)
        {
            var probe = new SeqAction { WindowClass = anchorClass, WindowTitle = anchorTitle };
            if (WindowAnchor.Resolve(probe, current.X, current.Y, out POINT screenPt)) SetXY(screenPt);
        }

        UpdateAnchorLabel();
        ApplyKindVisibility();
    }

    private void UpdateAnchorLabel()
    {
        if (!chkAnchor.Checked) { lblAnchor.Text = ""; return; }
        string label = anchorTitle.Length > 0 ? anchorTitle : anchorClass;
        lblAnchor.Text = label.Length > 0
            ? $"X/Y are relative to: {label}"
            : "No window captured yet — use Pick.";
    }

    // ---- Validation ----

    /// <summary>
    /// Fills the label combo with the labels already present in the sequence, so a Goto can
    /// pick its target from the list instead of retyping it. The combo stays editable, so a
    /// brand-new label (or a sequence-less caller) is still free text.
    /// </summary>
    private void PopulateLabels()
    {
        string keep = cmbLabel.Text;
        cmbLabel.Items.Clear();
        if (sequence is not null)
        {
            foreach (SeqAction a in sequence)
            {
                if (a.Kind != ActionKind.Label) continue;
                string label = a.Label?.Trim() ?? "";
                if (label.Length > 0 && !cmbLabel.Items.Contains(label)) cmbLabel.Items.Add(label);
            }
        }
        cmbLabel.Text = keep;
    }

    private static bool IsValidIdentifier(string name)
    {
        if (name.Length == 0 || char.IsDigit(name[0])) return false;
        foreach (char c in name)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    private void ValidateCombo()
    {
        if (SelectedKind != ActionKind.Key) { lblComboHint.Text = ""; return; }

        string? problem = InputSender.DescribeComboProblem(txtCombo.Text);
        lblComboHint.ForeColor = problem is null ? Color.DimGray : Color.Firebrick;
        lblComboHint.Text = problem ?? "e.g. Ctrl+C · Alt+Tab · Shift+F5 · Enter · Esc";
    }

    private void OnClosingValidate(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;

        var kind = SelectedKind;
        if (kind == ActionKind.Key && InputSender.DescribeComboProblem(txtCombo.Text) is string problem)
        {
            MessageBox.Show(this, problem, "Invalid key combo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == ActionKind.Text && txtText.Text.Length == 0)
        {
            MessageBox.Show(this, "Nothing to type.", "Empty text", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == ActionKind.WaitPixel && (PixelCondition)cmbCondition.SelectedIndex == PixelCondition.None)
        {
            MessageBox.Show(this, "Set what to wait for.", "Nothing to wait for", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == ActionKind.IfElse && txtCondition.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Type a condition, e.g. counter < 5.", "Empty condition", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == ActionKind.SetVar)
        {
            string name = txtVarName.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Name the variable to set.", "Empty variable name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            if (!IsValidIdentifier(name))
            {
                MessageBox.Show(this, "Variable names can only use letters, digits and underscores, and can't start with a digit.",
                    "Invalid variable name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            if (txtValueExpr.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "Type a value, e.g. counter + 1.", "Empty value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
        }
        if (kind is ActionKind.GotoLabel or ActionKind.Label && cmbLabel.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Name the label.", "Empty label", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == ActionKind.FindImage && (currentTemplate is null || currentTemplate.Length == 0))
        {
            MessageBox.Show(this, "Capture or load a template image first.", "No template",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == ActionKind.FindText && txtTextQuery.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Type the text to find.", "Empty text", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }

        action.Kind = kind;
        action.X = (int)numX.Value;
        action.Y = (int)numY.Value;
        action.WindowRelative = chkAnchor.Checked;
        action.WindowClass = anchorClass;
        action.WindowTitle = anchorTitle;
        action.ClickMethod = cmbClickMethod.SelectedIndex;
        action.Button = cmbButton.SelectedIndex;
        action.DoubleClick = cmbClickType.SelectedIndex == 1;
        action.HoldMs = (int)numHold.Value;
        action.PreferSelector = chkPreferSelector.Checked;
        action.EndX = (int)numEndX.Value;
        action.EndY = (int)numEndY.Value;
        action.DragMs = (int)numDragMs.Value;
        action.ScrollNotches = (int)numNotches.Value;
        action.Horizontal = chkHorizontal.Checked;
        action.KeyCombo = txtCombo.Text.Trim();
        action.Text = txtText.Text;
        action.DelayMs = (int)numDelay.Value;
        action.Comment = txtComment.Text;
        action.Condition = (PixelCondition)cmbCondition.SelectedIndex;
        action.CondX = (int)numCondX.Value;
        action.CondY = (int)numCondY.Value;
        action.CondColor = PixelSampler.ToRgb(pnlSwatch.BackColor);
        action.CondTolerance = (int)numTolerance.Value;
        action.PixelTimeoutMs = (int)numPixelTimeout.Value;
        action.AbortRunOnTimeout = chkAbortOnTimeout.Checked;
        action.PollIntervalMs = (int)numPollInterval.Value;
        action.RepeatCount = (int)numRepeatCount.Value;
        action.ConditionExpr = txtCondition.Text.Trim();
        action.VarName = txtVarName.Text.Trim();
        action.ValueExpr = txtValueExpr.Text.Trim();
        action.Label = cmbLabel.Text.Trim();
        action.TemplatePng = currentTemplate;
        action.MatchThreshold = (double)numThreshold.Value;
        action.SearchX = (int)numSearchX.Value;
        action.SearchY = (int)numSearchY.Value;
        action.SearchW = (int)numSearchW.Value;
        action.SearchH = (int)numSearchH.Value;
        action.ClickOnFound = chkClickOnFound.Checked;
        action.ClickOffsetX = (int)numClickOffsetX.Value;
        action.ClickOffsetY = (int)numClickOffsetY.Value;
        action.TextQuery = txtTextQuery.Text;
        action.RegexQuery = chkRegexQuery.Checked;
        action.Normalize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pickTimer?.Stop();
            pickTimer?.Dispose();
            pickTimer = null;
            pickPixelTimer?.Stop();
            pickPixelTimer?.Dispose();
            pickPixelTimer = null;
            captureTemplateTimer?.Stop();
            captureTemplateTimer?.Dispose();
            captureTemplateTimer = null;
        }
        base.Dispose(disposing);
    }

    // ---- Small factories ----

    private static NumericUpDown NewNum(decimal min, decimal max) =>
        new() { Minimum = min, Maximum = max, Width = 90, Margin = new Padding(3, 4, 3, 3) };

    private static ComboBox NewCombo(int width, params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(3, 4, 3, 3) };
        if (items.Length > 0)
        {
            c.Items.AddRange(items);
            c.SelectedIndex = 0;
        }
        return c;
    }
}
