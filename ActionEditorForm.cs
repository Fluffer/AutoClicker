using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Add/edit dialog for one <see cref="SeqAction"/>. Shows only the fields that the
/// selected action kind actually uses.
/// </summary>
internal sealed class ActionEditorForm : Form
{
    private readonly SeqAction action;

    private readonly ComboBox cmbKind = NewCombo(150);
    private readonly NumericUpDown numX = NewNum(-100000, 100000);
    private readonly NumericUpDown numY = NewNum(-100000, 100000);
    private readonly Button btnPick = new() { Text = "Pick (3s)…", AutoSize = true, Margin = new Padding(6, 3, 3, 3) };
    private readonly CheckBox chkAnchor = new() { Text = "Anchor to the window it's in", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label lblAnchor = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 3) };
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

    private readonly List<(Control label, Control field, Func<ActionKind, bool> visibleFor)> rows = new();
    private readonly Button btnOk;

    private System.Windows.Forms.Timer? pickTimer;
    private int pickCountdown;

    // Window anchoring keeps both representations so toggling the checkbox can convert
    // between them instead of throwing the coordinates away.
    private string anchorClass;
    private string anchorTitle;

    public SeqAction Result => action;

    public ActionEditorForm(SeqAction source, string title)
    {
        action = source.Clone();
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

        cmbKind.Items.AddRange(new object[] { "Click", "Drag", "Scroll", "Key press", "Type text", "Wait only" });
        cmbKind.SelectedIndex = (int)action.Kind;
        cmbKind.SelectedIndexChanged += (_, _) => ApplyKindVisibility();

        var posFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        posFlow.Controls.Add(numX);
        posFlow.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        posFlow.Controls.Add(numY);
        posFlow.Controls.Add(btnPick);
        btnPick.Click += (_, _) => StartPick();

        var endFlow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        endFlow.Controls.Add(numEndX);
        endFlow.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(6, 7, 3, 3) });
        endFlow.Controls.Add(numEndY);

        AddRow(root, "Action:", cmbKind, _ => true);
        AddRow(root, "Position  X", posFlow, k => UsesPosition(k));
        AddRow(root, "", chkAnchor, k => UsesPosition(k));
        AddRow(root, "", lblAnchor, k => UsesPosition(k) && chkAnchor.Checked);
        AddRow(root, "Mouse button:", cmbButton, k => k is ActionKind.Click or ActionKind.Drag);
        AddRow(root, "Click type:", cmbClickType, k => k is ActionKind.Click);
        AddRow(root, "Hold button (ms):", numHold, k => k is ActionKind.Click);
        AddRow(root, "Drag to  X", endFlow, k => k is ActionKind.Drag);
        AddRow(root, "Drag duration (ms):", numDragMs, k => k is ActionKind.Drag);
        AddRow(root, "Wheel notches:", numNotches, k => k is ActionKind.Scroll);
        AddRow(root, "", chkHorizontal, k => k is ActionKind.Scroll);
        AddRow(root, "Key combo:", txtCombo, k => k is ActionKind.Key);
        AddRow(root, "", lblComboHint, k => k is ActionKind.Key);
        AddRow(root, "Text:", txtText, k => k is ActionKind.Text);
        AddRow(root, "Wait after (ms):", numDelay, _ => true);

        numX.Value = action.X;
        numY.Value = action.Y;
        chkAnchor.Checked = action.WindowRelative;
        cmbButton.SelectedIndex = Math.Clamp(action.Button, 0, 2);
        cmbClickType.SelectedIndex = action.DoubleClick ? 1 : 0;
        numHold.Value = action.HoldMs;
        numEndX.Value = action.EndX;
        numEndY.Value = action.EndY;
        numDragMs.Value = action.DragMs;
        numNotches.Value = Math.Clamp(action.ScrollNotches, -1000, 1000);
        chkHorizontal.Checked = action.Horizontal;
        txtCombo.Text = action.KeyCombo;
        txtText.Text = action.Text;
        numDelay.Value = action.DelayMs;

        chkAnchor.CheckedChanged += (_, _) => AnchorToggled();
        txtCombo.TextChanged += (_, _) => ValidateCombo();

        btnOk = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(80, 30), DialogResult = DialogResult.OK, Margin = new Padding(3) };
        var btnCancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 30), DialogResult = DialogResult.Cancel, Margin = new Padding(3) };
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnOk);
        root.Controls.Add(buttons, 0, root.RowCount);
        root.SetColumnSpan(buttons, 2);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
        FormClosing += OnClosingValidate;

        UpdateAnchorLabel();
        ValidateCombo();
        ApplyKindVisibility();
    }

    private static bool UsesPosition(ActionKind k) => k is ActionKind.Click or ActionKind.Drag or ActionKind.Scroll;

    private ActionKind SelectedKind => (ActionKind)Math.Max(0, cmbKind.SelectedIndex);

    private void AddRow(TableLayoutPanel root, string labelText, Control field, Func<ActionKind, bool> visibleFor)
    {
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };
        int row = root.RowCount;
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

        action.Kind = kind;
        action.X = (int)numX.Value;
        action.Y = (int)numY.Value;
        action.WindowRelative = chkAnchor.Checked;
        action.WindowClass = anchorClass;
        action.WindowTitle = anchorTitle;
        action.Button = cmbButton.SelectedIndex;
        action.DoubleClick = cmbClickType.SelectedIndex == 1;
        action.HoldMs = (int)numHold.Value;
        action.EndX = (int)numEndX.Value;
        action.EndY = (int)numEndY.Value;
        action.DragMs = (int)numDragMs.Value;
        action.ScrollNotches = (int)numNotches.Value;
        action.Horizontal = chkHorizontal.Checked;
        action.KeyCombo = txtCombo.Text.Trim();
        action.Text = txtText.Text;
        action.DelayMs = (int)numDelay.Value;
        action.Normalize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pickTimer?.Stop();
            pickTimer?.Dispose();
            pickTimer = null;
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
