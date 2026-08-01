using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using static AutoClicker.Native;

namespace AutoClicker;

public partial class Form1 : Form
{
    private const int HOTKEY_TOGGLE = 0xB001;
    private const int HOTKEY_RECEND = 0xB002;
    private const uint VK_F8 = 0x77;

    // ---- Controls ----
    private NumericUpDown numHours = null!, numMins = null!, numSecs = null!, numMs = null!;
    private ComboBox cmbButton = null!, cmbType = null!;
    private RadioButton rbRepeatN = null!, rbRepeatUntil = null!;
    private NumericUpDown numRepeat = null!;
    private RadioButton rbCurrent = null!, rbPick = null!;
    private Button btnPick = null!;
    private NumericUpDown numX = null!, numY = null!;
    private NumericUpDown numJitterPx = null!, numJitterPct = null!;
    private CheckBox chkSequence = null!, chkBackground = null!, chkAnchorPoints = null!;
    private ListView lvPoints = null!;
    private Button btnRecord = null!, btnAddCur = null!, btnAddAction = null!, btnEdit = null!, btnRemovePoint = null!, btnClearPoints = null!;
    private Button btnUp = null!, btnDown = null!, btnSave = null!, btnLoad = null!;
    private Button btnStart = null!, btnStop = null!, btnHotkey = null!;
    private Label lblStatus = null!;

    // ---- State ----
    private List<SeqAction> points = new();
    private Thread? worker;
    private volatile bool running;
    private int busy; // 0 = idle, 1 = a worker owns the engine. Guards start/stop overlap.
    private uint hotkeyVk = 0x75; // F6
    private string hotkeyName = "F6";
    private System.Windows.Forms.Timer? pickTimer;
    private int pickCountdown;
    private Action? pickDone;

    // recording (low-level mouse hook)
    private bool recording;
    private IntPtr mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? hookProc; // kept alive to prevent GC

    public Form1()
    {
        InitializeComponent();
    }

    private void BuildUi()
    {
        Text = "Auto Clicker";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch (ArgumentException) { }
        catch (IOException) { }
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(10);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(BuildIntervalGroup());
        root.Controls.Add(BuildOptionsRepeatRow());
        root.Controls.Add(BuildCursorHumanizeRow());
        root.Controls.Add(BuildSequenceGroup());
        root.Controls.Add(BuildButtonsRow());

        btnHotkey = new Button { Text = "Hotkey setting", Dock = DockStyle.Fill, Height = 36, Margin = new Padding(3, 6, 3, 3) };
        btnHotkey.Click += BtnHotkey_Click;
        root.Controls.Add(btnHotkey);

        lblStatus = new Label
        {
            Text = "Ready. Press " + hotkeyName + " to start/stop.",
            Dock = DockStyle.Fill,
            Height = 56,
            Margin = new Padding(3, 6, 3, 3),
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray
        };
        root.Controls.Add(lblStatus);

        UpdateEnabled();
    }

    // ===== Click interval =====
    private GroupBox BuildIntervalGroup()
    {
        var grp = NewGroup("Click interval (default wait between clicks)");
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = false };
        numHours = NewNum(0, 999, 0);
        numMins = NewNum(0, 59, 0);
        numSecs = NewNum(0, 59, 0);
        numMs = NewNum(0, 999, 100);
        flow.Controls.Add(numHours); flow.Controls.Add(Lbl("hours"));
        flow.Controls.Add(numMins); flow.Controls.Add(Lbl("mins"));
        flow.Controls.Add(numSecs); flow.Controls.Add(Lbl("secs"));
        flow.Controls.Add(numMs); flow.Controls.Add(Lbl("milliseconds"));
        grp.Controls.Add(flow);
        return grp;
    }

    // ===== Options + Repeat side by side =====
    private TableLayoutPanel BuildOptionsRepeatRow()
    {
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var grpOpt = NewGroup("Click options");
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill };
        t.Controls.Add(Lbl("Mouse button:"), 0, 0);
        cmbButton = NewCombo("Left", "Right", "Middle");
        t.Controls.Add(cmbButton, 1, 0);
        t.Controls.Add(Lbl("Click type:"), 0, 1);
        cmbType = NewCombo("Single", "Double", "Hold (press & hold)");
        cmbType.SelectedIndexChanged += (_, _) => UpdateEnabled();
        t.Controls.Add(cmbType, 1, 1);
        grpOpt.Controls.Add(t);
        row.Controls.Add(grpOpt, 0, 0);

        var grpRep = NewGroup("Click repeat");
        var r = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Dock = DockStyle.Fill };
        rbRepeatN = new RadioButton { Text = "Repeat", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        rbRepeatN.CheckedChanged += (_, _) => UpdateEnabled();
        r.Controls.Add(rbRepeatN, 0, 0);
        numRepeat = NewNum(1, 1000000, 1);
        r.Controls.Add(numRepeat, 1, 0);
        r.Controls.Add(Lbl("times"), 2, 0);
        rbRepeatUntil = new RadioButton { Text = "Repeat until stopped", AutoSize = true, Checked = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        r.Controls.Add(rbRepeatUntil, 0, 1);
        r.SetColumnSpan(rbRepeatUntil, 3);
        grpRep.Controls.Add(r);
        row.Controls.Add(grpRep, 1, 0);

        return row;
    }

    // ===== Cursor position + Humanize side by side =====
    private TableLayoutPanel BuildCursorHumanizeRow()
    {
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(BuildCursorGroup(), 0, 0);
        row.Controls.Add(BuildHumanizeGroup(), 1, 0);
        return row;
    }

    private GroupBox BuildCursorGroup()
    {
        var grp = NewGroup("Cursor position (single-point mode)");
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = false };
        rbCurrent = new RadioButton { Text = "Current location", AutoSize = true, Checked = true, Margin = new Padding(3, 6, 12, 3) };
        rbCurrent.CheckedChanged += (_, _) => UpdateEnabled();
        rbPick = new RadioButton { Text = "Pick:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        rbPick.CheckedChanged += (_, _) => UpdateEnabled();
        btnPick = new Button { Text = "Pick location", AutoSize = true, Margin = new Padding(3) };
        btnPick.Click += BtnPick_Click;
        // Negative coordinates are legal: a secondary monitor placed left of / above the
        // primary one lives at negative virtual-screen coordinates.
        numX = NewNum(-100000, 100000, 0); numX.Width = 80;
        numY = NewNum(-100000, 100000, 0); numY.Width = 80;
        flow.Controls.Add(rbCurrent);
        flow.Controls.Add(rbPick);
        flow.Controls.Add(btnPick);
        flow.Controls.Add(Lbl("X"));
        flow.Controls.Add(numX);
        flow.Controls.Add(Lbl("Y"));
        flow.Controls.Add(numY);
        grp.Controls.Add(flow);
        return grp;
    }

    // ===== Humanize (randomised position / timing) =====
    private GroupBox BuildHumanizeGroup()
    {
        var grp = NewGroup("Humanize");
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Dock = DockStyle.Fill };
        t.Controls.Add(Lbl("Position jitter:"), 0, 0);
        numJitterPx = NewNum(0, 500, 0);
        t.Controls.Add(numJitterPx, 1, 0);
        t.Controls.Add(Lbl("± px"), 2, 0);
        t.Controls.Add(Lbl("Timing jitter:"), 0, 1);
        numJitterPct = NewNum(0, 100, 0);
        t.Controls.Add(numJitterPct, 1, 1);
        t.Controls.Add(Lbl("± %"), 2, 1);
        var hint = Lbl("0 = perfectly regular. Applies to clicks and waits.");
        hint.ForeColor = Color.DimGray;
        t.Controls.Add(hint, 0, 2);
        t.SetColumnSpan(hint, 3);
        grp.Controls.Add(t);
        return grp;
    }

    // ===== Click sequence (multiple actions) =====
    private GroupBox BuildSequenceGroup()
    {
        var grp = NewGroup("Sequence (clicks, drags, scrolls, keystrokes)");
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        chkSequence = new CheckBox { Text = "Use sequence (runs each action in order, then repeats)", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
        chkSequence.CheckedChanged += (_, _) => UpdateEnabled();
        t.Controls.Add(chkSequence, 0, 0);
        t.SetColumnSpan(chkSequence, 2);

        chkBackground = new CheckBox { Text = "Background mode — send input to the window under each point, don't move my cursor (experimental)", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
        t.Controls.Add(chkBackground, 0, 1);
        t.SetColumnSpan(chkBackground, 2);

        chkAnchorPoints = new CheckBox { Text = "Anchor new points to their window — survives the window being moved or reopened", AutoSize = true, Margin = new Padding(3, 0, 3, 6) };
        t.Controls.Add(chkAnchorPoints, 0, 2);
        t.SetColumnSpan(chkAnchorPoints, 2);

        lvPoints = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            Width = 500,
            Height = 200,
            Margin = new Padding(3)
        };
        lvPoints.Columns.Add("#", 30);
        lvPoints.Columns.Add("Action", 190);
        lvPoints.Columns.Add("Target", 200);
        lvPoints.Columns.Add("Wait ms", 70);
        lvPoints.DoubleClick += (_, _) => EditSelectedPoint();
        t.Controls.Add(lvPoints, 0, 3);

        var btns = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(6, 3, 3, 3) };
        btnRecord = SeqBtn("● Record clicks");
        btnRecord.Click += (_, _) => ToggleRecording();
        btnAddCur = SeqBtn("Add cursor pos");
        btnAddCur.Click += (_, _) => AddPoint(CursorPos());
        btnAddAction = SeqBtn("Add action…");
        btnAddAction.Click += (_, _) => AddCustomAction();
        btnEdit = SeqBtn("Edit…");
        btnEdit.Click += (_, _) => EditSelectedPoint();
        btnRemovePoint = SeqBtn("Remove");
        btnRemovePoint.Click += (_, _) => RemoveSelectedPoint();
        btnClearPoints = SeqBtn("Clear all");
        btnClearPoints.Click += (_, _) => { points.Clear(); RefreshList(); };
        btns.Controls.Add(btnRecord);
        btns.Controls.Add(btnAddCur);
        btns.Controls.Add(btnAddAction);
        btns.Controls.Add(btnEdit);
        btns.Controls.Add(btnRemovePoint);
        btns.Controls.Add(btnClearPoints);
        t.Controls.Add(btns, 1, 3);

        var bottom = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        btnUp = SeqBtn("Move up ▲");
        btnUp.Click += (_, _) => MovePoint(-1);
        btnDown = SeqBtn("Move down ▼");
        btnDown.Click += (_, _) => MovePoint(1);
        btnSave = SeqBtn("Save sequence…");
        btnLoad = SeqBtn("Load sequence…");
        btnSave.Click += (_, _) => SaveSequence();
        btnLoad.Click += (_, _) => LoadSequence();
        bottom.Controls.Add(btnUp);
        bottom.Controls.Add(btnDown);
        bottom.Controls.Add(btnSave);
        bottom.Controls.Add(btnLoad);
        t.Controls.Add(bottom, 0, 4);
        t.SetColumnSpan(bottom, 2);

        var hint = Lbl("Record: click each target (right-click or F8 to finish). Double-click a row to edit it. "
                     + "\"Add action…\" adds drags, scroll and keystrokes.");
        hint.ForeColor = Color.DimGray;
        t.Controls.Add(hint, 0, 5);
        t.SetColumnSpan(hint, 2);

        grp.Controls.Add(t);
        return grp;
    }

    // ===== Start / Stop =====
    private TableLayoutPanel BuildButtonsRow()
    {
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnStart = new Button { Text = "Start (F6)", Dock = DockStyle.Fill, Height = 50, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        btnStart.Click += (_, _) => StartClicking();
        btnStop = new Button { Text = "Stop (F6)", Dock = DockStyle.Fill, Height = 50, Enabled = false, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        btnStop.Click += (_, _) => StopClicking();
        row.Controls.Add(btnStart, 0, 0);
        row.Controls.Add(btnStop, 1, 0);
        return row;
    }

    // ---- UI helpers ----
    private static GroupBox NewGroup(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Padding = new Padding(8, 4, 8, 8),
        Margin = new Padding(3)
    };

    private static NumericUpDown NewNum(int min, int max, int val) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = val,
        Width = 60,
        Margin = new Padding(3, 4, 6, 3)
    };

    private static ComboBox NewCombo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(3, 4, 3, 3) };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }

    private static Button SeqBtn(string text) => new() { Text = text, Width = 138, Height = 30, Margin = new Padding(2) };
    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

    private void UpdateEnabled()
    {
        bool seq = chkSequence.Checked;
        bool hold = cmbType.SelectedIndex == 2;

        rbRepeatN.Enabled = !hold;
        rbRepeatUntil.Enabled = !hold;
        numRepeat.Enabled = (!hold || seq) && rbRepeatN.Checked;

        rbCurrent.Enabled = !seq;
        rbPick.Enabled = !seq;
        btnPick.Enabled = !seq;
        numX.Enabled = !seq && rbPick.Checked;
        numY.Enabled = !seq && rbPick.Checked;
    }

    private static POINT CursorPos() { GetCursorPos(out POINT p); return p; }

    // ---- Sequence list ----
    private void AddPoint(POINT p)
    {
        var a = new SeqAction
        {
            Kind = ActionKind.Click,
            X = p.X,
            Y = p.Y,
            Button = cmbButton.SelectedIndex,
            DoubleClick = cmbType.SelectedIndex == 1,
            DelayMs = IntervalMs()
        };

        if (chkAnchorPoints.Checked &&
            WindowAnchor.Capture(p, out string cls, out string title, out POINT clientPt))
        {
            a.WindowRelative = true;
            a.WindowClass = cls;
            a.WindowTitle = title;
            a.X = clientPt.X;
            a.Y = clientPt.Y;
        }

        points.Add(a);
        RefreshList();
        if (!chkSequence.Checked) chkSequence.Checked = true;
        lblStatus.Text = $"Added action #{points.Count}: {a.Describe()} at {a.DescribeTarget()}";
    }

    private void AddCustomAction()
    {
        var seed = new SeqAction { DelayMs = IntervalMs(), Button = cmbButton.SelectedIndex };
        var cur = CursorPos();
        seed.X = cur.X;
        seed.Y = cur.Y;

        using var dlg = new ActionEditorForm(seed, "Add action");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        points.Add(dlg.Result);
        RefreshList();
        if (!chkSequence.Checked) chkSequence.Checked = true;
        lvPoints.Items[^1].Selected = true;
        lblStatus.Text = $"Added action #{points.Count}: {dlg.Result.Describe()}";
    }

    private void RemoveSelectedPoint()
    {
        int i = SelectedIndex();
        if (i < 0) return;
        points.RemoveAt(i);
        RefreshList();
    }

    private void MovePoint(int dir)
    {
        int i = SelectedIndex();
        int j = i + dir;
        if (i < 0 || j < 0 || j >= points.Count) return;
        (points[i], points[j]) = (points[j], points[i]);
        RefreshList();
        lvPoints.Items[j].Selected = true;
    }

    private int SelectedIndex() => lvPoints.SelectedIndices.Count > 0 ? lvPoints.SelectedIndices[0] : -1;

    private void RefreshList()
    {
        lvPoints.BeginUpdate();
        lvPoints.Items.Clear();
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var item = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(p.Describe());
            item.SubItems.Add(p.DescribeTarget());
            item.SubItems.Add(p.DelayMs.ToString(CultureInfo.InvariantCulture));
            lvPoints.Items.Add(item);
        }
        lvPoints.EndUpdate();
    }

    // ---- Edit an action ----
    private void EditSelectedPoint()
    {
        int i = SelectedIndex();
        if (i < 0) { lblStatus.Text = "Select an action first, then Edit."; return; }

        using var dlg = new ActionEditorForm(points[i], $"Edit action #{i + 1}");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        points[i] = dlg.Result;
        RefreshList();
        lvPoints.Items[i].Selected = true;
    }

    // ---- Save / Load sequence (JSON) ----
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private void SaveSequence()
    {
        if (points.Count == 0) { lblStatus.Text = "Nothing to save — sequence is empty."; return; }
        using var sfd = new SaveFileDialog { Filter = "Auto Clicker sequence (*.acseq)|*.acseq|JSON (*.json)|*.json", DefaultExt = "acseq", FileName = "sequence.acseq" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(sfd.FileName, JsonSerializer.Serialize(points, JsonOpts));
            lblStatus.Text = $"Saved {points.Count} actions to {Path.GetFileName(sfd.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            lblStatus.Text = "Save failed: " + ex.Message;
        }
    }

    private void LoadSequence()
    {
        using var ofd = new OpenFileDialog { Filter = "Auto Clicker sequence (*.acseq;*.json)|*.acseq;*.json|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<SeqAction>>(File.ReadAllText(ofd.FileName));
            if (loaded == null) { lblStatus.Text = "Load failed: empty file."; return; }
            foreach (var a in loaded) a.Normalize();
            points = loaded;
            RefreshList();
            chkSequence.Checked = points.Count > 0;
            lblStatus.Text = $"Loaded {points.Count} actions from {Path.GetFileName(ofd.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            lblStatus.Text = "Load failed: " + ex.Message;
        }
    }

    // ---- Recording via low-level mouse hook ----
    private void ToggleRecording()
    {
        if (recording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (running) { lblStatus.Text = "Stop clicking before recording."; return; }
        hookProc = HookCallback;
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero) { lblStatus.Text = "Could not install mouse hook."; return; }
        recording = true;
        chkSequence.Checked = true;
        btnRecord.Text = "■ Stop recording";
        lblStatus.Text = "RECORDING: left-click each target. Right-click or F8 to finish.";
    }

    private void StopRecording()
    {
        recording = false;
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        hookProc = null;
        btnRecord.Text = "● Record clicks";
        lblStatus.Text = $"Recording finished. {points.Count} actions total.";
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && recording)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_RBUTTONDOWN_LL)
            {
                BeginInvoke(StopRecording);
                return (IntPtr)1; // swallow the right-click that ends recording
            }
            if (msg == WM_LBUTTONDOWN_LL)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = data.pt;
                // ignore clicks landing on our own window
                if (GetWindowRect(Handle, out RECT r) &&
                    pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                {
                    return CallNextHookEx(mouseHook, nCode, wParam, lParam);
                }
                // Re-check `recording` on the UI thread: BeginInvoke is async, so recording
                // may already have been stopped by the time this runs.
                BeginInvoke(() => { if (recording) AddPoint(pt); });
                return (IntPtr)1; // swallow so the target isn't actually clicked during recording
            }
        }
        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    // ---- Pick location (single-point): countdown then capture cursor ----
    private void BtnPick_Click(object? sender, EventArgs e)
    {
        rbPick.Checked = true;
        StartPick(() =>
        {
            GetCursorPos(out POINT p);
            numX.Value = Math.Clamp(p.X, (int)numX.Minimum, (int)numX.Maximum);
            numY.Value = Math.Clamp(p.Y, (int)numY.Minimum, (int)numY.Maximum);
            lblStatus.Text = $"Captured X={p.X} Y={p.Y}.";
            UpdateEnabled();
        });
    }

    private void StartPick(Action done)
    {
        pickDone = done;
        pickCountdown = 3;
        pickTimer?.Dispose();
        pickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        lblStatus.Text = $"Move mouse to target... capturing in {pickCountdown}s";
        pickTimer.Tick += (_, _) =>
        {
            pickCountdown--;
            if (pickCountdown > 0) { lblStatus.Text = $"Move mouse to target... capturing in {pickCountdown}s"; return; }
            pickTimer!.Stop(); pickTimer.Dispose(); pickTimer = null;
            pickDone?.Invoke(); pickDone = null;
        };
        pickTimer.Start();
    }

    // ---- Hotkey rebind ----
    private void BtnHotkey_Click(object? sender, EventArgs e)
    {
        using var dlg = new Form
        {
            Text = "Hotkey setting",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(300, 120),
            MaximizeBox = false,
            MinimizeBox = false
        };
        var lbl = new Label { Text = "Start/Stop hotkey:", Location = new Point(16, 20), AutoSize = true };
        var cmb = new ComboBox { Location = new Point(160, 16), Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        for (int i = 1; i <= 12; i++) cmb.Items.Add("F" + i);
        cmb.SelectedItem = hotkeyName;
        if (cmb.SelectedIndex < 0) cmb.SelectedIndex = 5;
        var ok = new Button { Text = "OK", Location = new Point(120, 68), Size = new Size(75, 32), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(203, 68), Size = new Size(82, 32), DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[] { lbl, cmb, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        if (dlg.ShowDialog(this) == DialogResult.OK && cmb.SelectedItem is string name)
        {
            int fn = int.Parse(name.AsSpan(1), CultureInfo.InvariantCulture);
            hotkeyVk = (uint)(0x70 + (fn - 1));
            hotkeyName = name;
            btnStart.Text = $"Start ({hotkeyName})";
            btnStop.Text = $"Stop ({hotkeyName})";
            lblStatus.Text = $"Ready. Press {hotkeyName} to start/stop.";
            RegisterHotkeys();
        }
    }

    private void RegisterHotkeys()
    {
        UnregisterHotKey(Handle, HOTKEY_TOGGLE);
        UnregisterHotKey(Handle, HOTKEY_RECEND);
        bool okToggle = RegisterHotKey(Handle, HOTKEY_TOGGLE, 0, hotkeyVk);
        bool okRecEnd = hotkeyVk == VK_F8 || RegisterHotKey(Handle, HOTKEY_RECEND, 0, VK_F8);

        if (lblStatus is null) return; // the handle can be created before BuildUi finishes
        if (!okToggle)
            lblStatus.Text = $"{hotkeyName} is already claimed by another app — global hotkey OFF.\r\nUse the Start/Stop buttons, or pick a different key.";
        else if (!okRecEnd)
            lblStatus.Text = $"Ready. Press {hotkeyName} to start/stop. (F8 is taken — right-click ends recording.)";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        running = false;
        if (recording) StopRecording();

        pickTimer?.Stop();
        pickTimer?.Dispose();
        pickTimer = null;
        pickDone = null;

        // Wait for the worker to unwind. The worker is a background thread, so without this
        // the process can exit mid-"hold" and leave the mouse button physically stuck down.
        // It polls `running` every <= 20 ms, so this returns almost immediately.
        worker?.Join(2000);

        UnregisterHotKey(Handle, HOTKEY_TOGGLE);
        UnregisterHotKey(Handle, HOTKEY_RECEND);
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_TOGGLE)
            {
                if (recording) { StopRecording(); return; }
                if (running) StopClicking(); else StartClicking();
                return;
            }
            if (id == HOTKEY_RECEND)
            {
                if (recording) StopRecording();
                return;
            }
        }
        base.WndProc(ref m);
    }

    // ---- Click engine ----
    private int IntervalMs()
    {
        long ms = (long)numHours.Value * 3600000
                + (long)numMins.Value * 60000
                + (long)numSecs.Value * 1000
                + (long)numMs.Value;
        return (int)Math.Min(ms, int.MaxValue);
    }

    // Waits `ms`, but bails out early once `running` goes false.
    // Measured against a Stopwatch rather than by accumulating fixed steps: the old
    // "sleep 25 ms until you've slept enough" loop overshot every interval that wasn't
    // a multiple of 25 (a 30 ms interval actually waited 50 ms).
    private void InterruptibleSleep(int ms)
    {
        if (ms <= 0) return; // 0 means "as fast as possible", not "sleep a tick"
        var sw = Stopwatch.StartNew();
        while (running)
        {
            long left = ms - sw.ElapsedMilliseconds;
            if (left <= 0) return;
            if (left > 20) Thread.Sleep(10);   // coarse; keeps stop latency <= 10 ms
            else if (left > 2) Thread.Sleep(1);
            else Thread.SpinWait(100);         // final approach, Sleep is too granular here
        }
    }

    // ---- Humanize ----
    private static int JitterMs(int ms, int percent)
    {
        if (ms <= 0 || percent <= 0) return ms;
        double factor = 1 + ((Random.Shared.NextDouble() * 2) - 1) * percent / 100.0;
        return (int)Math.Max(0, Math.Round(ms * factor));
    }

    private static int JitterPx(int maxPx) => maxPx <= 0 ? 0 : Random.Shared.Next(-maxPx, maxPx + 1);

    private void StartClicking()
    {
        if (recording) return;

        // Claim the engine. `running` alone is not enough: after Stop, the previous worker
        // is still unwinding and its finally-block sets running = false — which would
        // immediately kill a run started in that window. busy is only released by Finish(),
        // once the old worker is genuinely done.
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;

        bool seq = chkSequence.Checked && points.Count > 0;
        bool limited = rbRepeatN.Checked;
        int limit = (int)numRepeat.Value;
        int jitterPx = (int)numJitterPx.Value;
        int jitterPct = (int)numJitterPct.Value;

        running = true;
        btnStart.Enabled = false;
        btnStop.Enabled = true;

        if (seq)
        {
            bool bg = chkBackground.Checked;
            var acts = points.Select(p => p.Clone()).ToList();
            lblStatus.Text = $"Running {acts.Count} actions{(bg ? " (background)" : "")}... press {hotkeyName} to stop.";
            worker = new Thread(() => RunSequence(acts, bg, limited, limit, jitterPx, jitterPct)) { IsBackground = true };
        }
        else
        {
            bool hold = cmbType.SelectedIndex == 2;
            bool dbl = cmbType.SelectedIndex == 1;
            bool useFixedPos = rbPick.Checked;
            int px = (int)numX.Value, py = (int)numY.Value;
            int interval = IntervalMs();
            int button = cmbButton.SelectedIndex;
            lblStatus.Text = hold ? $"Holding {cmbButton.Text} button... press {hotkeyName} to stop."
                                  : $"Clicking... press {hotkeyName} to stop.";
            worker = new Thread(() => RunSingle(hold, dbl, button, useFixedPos, px, py, interval, limited && !hold, limit, jitterPx, jitterPct)) { IsBackground = true };
        }

        try
        {
            worker.Start();
        }
        catch (Exception ex)
        {
            running = false;
            Interlocked.Exchange(ref busy, 0);
            OnStopped();
            lblStatus.Text = "Could not start: " + ex.Message;
        }
    }

    private void RunSingle(bool hold, bool dbl, int button, bool useFixedPos, int px, int py, int interval, bool limited, int limit, int jitterPx, int jitterPct)
    {
        using var timerRes = new TimerResolutionScope();
        try
        {
            if (useFixedPos) SetCursorPos(px, py);
            if (hold)
            {
                InputSender.HoldUntil(button, KeepGoing);
            }
            else
            {
                int count = 0;
                while (running)
                {
                    if (useFixedPos) SetCursorPos(px + JitterPx(jitterPx), py + JitterPx(jitterPx));
                    InputSender.Click(button, dbl);
                    count++;
                    if (limited && count >= limit) break;
                    InterruptibleSleep(JitterMs(interval, jitterPct));
                }
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally { Finish(); }
    }

    private void RunSequence(List<SeqAction> acts, bool background, bool limited, int limit, int jitterPx, int jitterPct)
    {
        using var timerRes = new TimerResolutionScope();
        try
        {
            int pass = 0;
            // Keyboard actions have no coordinates of their own; in background mode they
            // are posted to the window of the most recent positioned action.
            POINT lastTarget = CursorPos();

            while (running)
            {
                for (int i = 0; i < acts.Count && running; i++)
                {
                    var a = acts[i];
                    Highlight(i);
                    Execute(a, background, jitterPx, ref lastTarget);
                    InterruptibleSleep(JitterMs(a.DelayMs, jitterPct));
                }
                pass++;
                if (limited && pass >= limit) break;
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally { Highlight(-1); Finish(); }
    }

    private void Execute(SeqAction a, bool background, int jitterPx, ref POINT lastTarget)
    {
        bool positioned = a.Kind is ActionKind.Click or ActionKind.Drag or ActionKind.Scroll;
        int x = a.X, y = a.Y, ex = a.EndX, ey = a.EndY;

        if (positioned && a.WindowRelative)
        {
            if (!WindowAnchor.Resolve(a, a.X, a.Y, out POINT sp))
                throw new InvalidOperationException($"Anchor window \"{a.WindowLabel}\" is not open.");
            x = sp.X;
            y = sp.Y;
            if (a.Kind == ActionKind.Drag && WindowAnchor.Resolve(a, a.EndX, a.EndY, out POINT ep))
            {
                ex = ep.X;
                ey = ep.Y;
            }
        }

        if (positioned && jitterPx > 0)
        {
            x += JitterPx(jitterPx);
            y += JitterPx(jitterPx);
            if (a.Kind == ActionKind.Drag) { ex += JitterPx(jitterPx); ey += JitterPx(jitterPx); }
        }

        if (positioned) lastTarget = new POINT { X = x, Y = y };

        switch (a.Kind)
        {
            case ActionKind.Click:
                if (background) InputSender.BackgroundClick(x, y, a.Button, a.DoubleClick, a.HoldMs);
                else { SetCursorPos(x, y); InputSender.Click(a.Button, a.DoubleClick, a.HoldMs); }
                break;

            case ActionKind.Drag:
                if (background) InputSender.BackgroundDrag(x, y, ex, ey, a.Button, a.DragMs, KeepGoing);
                else InputSender.Drag(x, y, ex, ey, a.Button, a.DragMs, KeepGoing);
                break;

            case ActionKind.Scroll:
                if (background) InputSender.BackgroundScroll(x, y, a.ScrollNotches, a.Horizontal);
                else { SetCursorPos(x, y); InputSender.Scroll(a.ScrollNotches, a.Horizontal); }
                break;

            case ActionKind.Key:
                if (background) InputSender.BackgroundCombo(lastTarget.X, lastTarget.Y, a.KeyCombo);
                else InputSender.SendCombo(a.KeyCombo);
                break;

            case ActionKind.Text:
                if (background) InputSender.BackgroundTypeText(lastTarget.X, lastTarget.Y, a.Text, KeepGoing);
                else InputSender.TypeText(a.Text, KeepGoing);
                break;

            case ActionKind.Wait:
                break; // the DelayMs after the action is the whole point
        }
    }

    private bool KeepGoing() => running;

    private void Highlight(int index)
    {
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                foreach (ListViewItem it in lvPoints.Items) it.BackColor = lvPoints.BackColor;
                if (index >= 0 && index < lvPoints.Items.Count)
                {
                    lvPoints.Items[index].BackColor = Color.FromArgb(255, 230, 160);
                    lvPoints.Items[index].EnsureVisible();
                }
            });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // Surfaces worker-thread failures instead of swallowing them.
    private void ReportError(Exception ex)
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(() => lblStatus.Text = "Stopped — " + ex.Message); }
        catch (ObjectDisposedException) { /* form closed while we were unwinding */ }
        catch (InvalidOperationException) { /* handle destroyed between the check and the post */ }
    }

    private void Finish()
    {
        running = false;
        if (IsHandleCreated)
        {
            try { BeginInvoke(OnStopped); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        // Released last: StartClicking must not be able to claim the engine until this
        // worker has finished touching `running`.
        Interlocked.Exchange(ref busy, 0);
    }

    private void StopClicking() => running = false;

    private void OnStopped()
    {
        btnStart.Enabled = true;
        btnStop.Enabled = false;
        lblStatus.Text = $"Stopped. Press {hotkeyName} to start.";
    }
}
