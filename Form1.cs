using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AutoClicker;

public partial class Form1 : Form
{
    // ---- Win32 interop ----
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    // window messages for background (PostMessage) clicking
    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206;
    private const uint WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209;
    private const int MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002, MK_MBUTTON = 0x0010;

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN_LL = 0x0201;
    private const int WM_RBUTTONDOWN_LL = 0x0204;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE = 0xB001;
    private const int HOTKEY_RECEND = 0xB002;
    private const uint VK_F8 = 0x77;

    // ---- A point in a click sequence (JSON-serializable) ----
    public sealed class SeqPoint
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Button { get; set; }   // 0 left, 1 right, 2 middle
        public bool DoubleClick { get; set; }
        public int DelayMs { get; set; }  // wait AFTER this click, before next point
    }

    // ---- Controls ----
    private NumericUpDown numHours = null!, numMins = null!, numSecs = null!, numMs = null!;
    private ComboBox cmbButton = null!, cmbType = null!;
    private RadioButton rbRepeatN = null!, rbRepeatUntil = null!;
    private NumericUpDown numRepeat = null!;
    private RadioButton rbCurrent = null!, rbPick = null!;
    private Button btnPick = null!;
    private NumericUpDown numX = null!, numY = null!;
    private CheckBox chkSequence = null!, chkBackground = null!;
    private ListView lvPoints = null!;
    private Button btnRecord = null!, btnAddCur = null!, btnEdit = null!, btnRemovePoint = null!, btnClearPoints = null!;
    private Button btnUp = null!, btnDown = null!, btnSave = null!, btnLoad = null!;
    private Button btnStart = null!, btnStop = null!, btnHotkey = null!;
    private Label lblStatus = null!;

    // ---- State ----
    private List<SeqPoint> points = new();
    private Thread? worker;
    private volatile bool running;
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
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
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
        root.Controls.Add(BuildCursorGroup());
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

    // ===== Cursor position =====
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
        numX = NewNum(0, 100000, 0); numX.Width = 80;
        numY = NewNum(0, 100000, 0); numY.Width = 80;
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

    // ===== Click sequence (multiple points) =====
    private GroupBox BuildSequenceGroup()
    {
        var grp = NewGroup("Click sequence (multiple points)");
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        chkSequence = new CheckBox { Text = "Use point sequence (clicks each point in order, then repeats)", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
        chkSequence.CheckedChanged += (_, _) => UpdateEnabled();
        t.Controls.Add(chkSequence, 0, 0);
        t.SetColumnSpan(chkSequence, 2);

        chkBackground = new CheckBox { Text = "Background mode — send clicks to the window under each point, don't move my cursor (experimental)", AutoSize = true, Margin = new Padding(3, 0, 3, 6) };
        t.Controls.Add(chkBackground, 0, 1);
        t.SetColumnSpan(chkBackground, 2);

        lvPoints = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            Width = 420,
            Height = 180,
            Margin = new Padding(3)
        };
        lvPoints.Columns.Add("#", 30);
        lvPoints.Columns.Add("X", 60);
        lvPoints.Columns.Add("Y", 60);
        lvPoints.Columns.Add("Button", 70);
        lvPoints.Columns.Add("Type", 70);
        lvPoints.Columns.Add("Wait ms", 90);
        lvPoints.DoubleClick += (_, _) => EditSelectedPoint();
        t.Controls.Add(lvPoints, 0, 2);

        var btns = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(6, 3, 3, 3) };
        btnRecord = SeqBtn("● Record clicks");
        btnRecord.Click += (_, _) => ToggleRecording();
        btnAddCur = SeqBtn("Add cursor pos");
        btnAddCur.Click += (_, _) => AddPoint(CursorPos());
        btnEdit = SeqBtn("Edit point…");
        btnEdit.Click += (_, _) => EditSelectedPoint();
        btnRemovePoint = SeqBtn("Remove");
        btnRemovePoint.Click += (_, _) => RemoveSelectedPoint();
        btnClearPoints = SeqBtn("Clear all");
        btnClearPoints.Click += (_, _) => { points.Clear(); RefreshList(); };
        btns.Controls.Add(btnRecord);
        btns.Controls.Add(btnAddCur);
        btns.Controls.Add(btnEdit);
        btns.Controls.Add(btnRemovePoint);
        btns.Controls.Add(btnClearPoints);
        t.Controls.Add(btns, 1, 2);

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
        t.Controls.Add(bottom, 0, 3);
        t.SetColumnSpan(bottom, 2);

        var hint = Lbl("Record: click each target (right-click or F8 to finish). Double-click a row to edit X/Y/button/type/wait.");
        hint.ForeColor = Color.DimGray;
        t.Controls.Add(hint, 0, 4);
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
        points.Add(new SeqPoint
        {
            X = p.X,
            Y = p.Y,
            Button = cmbButton.SelectedIndex,
            DoubleClick = cmbType.SelectedIndex == 1,
            DelayMs = IntervalMs()
        });
        RefreshList();
        if (!chkSequence.Checked) chkSequence.Checked = true;
        lblStatus.Text = $"Added point #{points.Count}: X={p.X} Y={p.Y}";
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
            string btn = p.Button switch { 1 => "Right", 2 => "Middle", _ => "Left" };
            var item = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(p.X.ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(p.Y.ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(btn);
            item.SubItems.Add(p.DoubleClick ? "Double" : "Single");
            item.SubItems.Add(p.DelayMs.ToString(CultureInfo.InvariantCulture));
            lvPoints.Items.Add(item);
        }
        lvPoints.EndUpdate();
    }

    // ---- Edit a point ----
    private void EditSelectedPoint()
    {
        int i = SelectedIndex();
        if (i < 0) { lblStatus.Text = "Select a point first, then Edit."; return; }
        var p = points[i];

        using var dlg = new Form
        {
            Text = $"Edit point #{i + 1}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(330, 240),
            MaximizeBox = false,
            MinimizeBox = false
        };
        Label L(string t, int y) => new() { Text = t, Location = new Point(16, y + 3), AutoSize = true };
        const int ix = 160, iw = 150;
        var nX = new NumericUpDown { Location = new Point(ix, 16), Width = iw, Minimum = -100000, Maximum = 100000, Value = p.X };
        var nY = new NumericUpDown { Location = new Point(ix, 50), Width = iw, Minimum = -100000, Maximum = 100000, Value = p.Y };
        var cB = new ComboBox { Location = new Point(ix, 84), Width = iw, DropDownStyle = ComboBoxStyle.DropDownList };
        cB.Items.AddRange(new object[] { "Left", "Right", "Middle" }); cB.SelectedIndex = Math.Clamp(p.Button, 0, 2);
        var cT = new ComboBox { Location = new Point(ix, 118), Width = iw, DropDownStyle = ComboBoxStyle.DropDownList };
        cT.Items.AddRange(new object[] { "Single", "Double" }); cT.SelectedIndex = p.DoubleClick ? 1 : 0;
        var nD = new NumericUpDown { Location = new Point(ix, 152), Width = iw, Minimum = 0, Maximum = int.MaxValue, Value = p.DelayMs };
        var ok = new Button { Text = "OK", Location = new Point(150, 196), Size = new Size(75, 32), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(233, 196), Size = new Size(82, 32), DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[]
        {
            L("X:", 16), nX, L("Y:", 50), nY, L("Button:", 84), cB,
            L("Type:", 118), cT, L("Wait after (ms):", 152), nD, ok, cancel
        });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            p.X = (int)nX.Value; p.Y = (int)nY.Value;
            p.Button = cB.SelectedIndex; p.DoubleClick = cT.SelectedIndex == 1;
            p.DelayMs = (int)nD.Value;
            RefreshList();
            lvPoints.Items[i].Selected = true;
        }
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
            lblStatus.Text = $"Saved {points.Count} points to {Path.GetFileName(sfd.FileName)}";
        }
        catch (Exception ex) { lblStatus.Text = "Save failed: " + ex.Message; }
    }

    private void LoadSequence()
    {
        using var ofd = new OpenFileDialog { Filter = "Auto Clicker sequence (*.acseq;*.json)|*.acseq;*.json|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<SeqPoint>>(File.ReadAllText(ofd.FileName));
            if (loaded == null) { lblStatus.Text = "Load failed: empty file."; return; }
            points = loaded;
            RefreshList();
            chkSequence.Checked = points.Count > 0;
            lblStatus.Text = $"Loaded {points.Count} points from {Path.GetFileName(ofd.FileName)}";
        }
        catch (Exception ex) { lblStatus.Text = "Load failed: " + ex.Message; }
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
        lblStatus.Text = $"Recording finished. {points.Count} points total.";
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
                BeginInvoke(() => AddPoint(pt));
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
            numX.Value = Math.Clamp(p.X, 0, (int)numX.Maximum);
            numY.Value = Math.Clamp(p.Y, 0, (int)numY.Maximum);
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
            RegisterHotkeys();
            lblStatus.Text = $"Ready. Press {hotkeyName} to start/stop.";
        }
    }

    private void RegisterHotkeys()
    {
        UnregisterHotKey(Handle, HOTKEY_TOGGLE);
        UnregisterHotKey(Handle, HOTKEY_RECEND);
        RegisterHotKey(Handle, HOTKEY_TOGGLE, 0, hotkeyVk);
        if (hotkeyVk != VK_F8) RegisterHotKey(Handle, HOTKEY_RECEND, 0, VK_F8);
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

    private static (uint down, uint up) ButtonFlags(int index) => index switch
    {
        1 => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        2 => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    private static void SendMouse(uint flags)
    {
        var inp = new INPUT[1];
        inp[0].type = INPUT_MOUSE;
        inp[0].mi.dwFlags = flags;
        _ = SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    private static void DoClick(int button, bool dbl)
    {
        var (down, up) = ButtonFlags(button);
        SendMouse(down);
        SendMouse(up);
        if (dbl) { Thread.Sleep(15); SendMouse(down); SendMouse(up); }
    }

    // Background click: post messages to the window under the point, no cursor move.
    private static void DoBackgroundClick(int x, int y, int button, bool dbl)
    {
        var sp = new POINT { X = x, Y = y };
        IntPtr hwnd = WindowFromPoint(sp);
        if (hwnd == IntPtr.Zero) return;
        var cp = sp;
        ScreenToClient(hwnd, ref cp);
        IntPtr lParam = (IntPtr)((cp.Y << 16) | (cp.X & 0xFFFF));
        (uint down, uint up, uint dbclk, int mk) = button switch
        {
            1 => (WM_RBUTTONDOWN, WM_RBUTTONUP, WM_RBUTTONDBLCLK, MK_RBUTTON),
            2 => (WM_MBUTTONDOWN, WM_MBUTTONUP, WM_MBUTTONDBLCLK, MK_MBUTTON),
            _ => (WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, MK_LBUTTON),
        };
        PostMessage(hwnd, down, (IntPtr)mk, lParam);
        PostMessage(hwnd, up, IntPtr.Zero, lParam);
        if (dbl)
        {
            PostMessage(hwnd, dbclk, (IntPtr)mk, lParam);
            PostMessage(hwnd, up, IntPtr.Zero, lParam);
        }
    }

    private void InterruptibleSleep(int ms)
    {
        if (ms <= 0) { Thread.Sleep(1); return; }
        int slept = 0, step = Math.Min(25, ms);
        while (running && slept < ms) { Thread.Sleep(step); slept += step; }
    }

    private void StartClicking()
    {
        if (running || recording) return;

        bool seq = chkSequence.Checked && points.Count > 0;
        bool limited = rbRepeatN.Checked;
        int limit = (int)numRepeat.Value;

        running = true;
        btnStart.Enabled = false;
        btnStop.Enabled = true;

        if (seq)
        {
            bool bg = chkBackground.Checked;
            var pts = points.Select(p => new SeqPoint { X = p.X, Y = p.Y, Button = p.Button, DoubleClick = p.DoubleClick, DelayMs = p.DelayMs }).ToList();
            lblStatus.Text = $"Running sequence of {pts.Count} points{(bg ? " (background)" : "")}... press {hotkeyName} to stop.";
            worker = new Thread(() => RunSequence(pts, bg, limited, limit)) { IsBackground = true };
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
            worker = new Thread(() => RunSingle(hold, dbl, button, useFixedPos, px, py, interval, limited && !hold, limit)) { IsBackground = true };
        }
        worker.Start();
    }

    private void RunSingle(bool hold, bool dbl, int button, bool useFixedPos, int px, int py, int interval, bool limited, int limit)
    {
        try
        {
            if (useFixedPos) SetCursorPos(px, py);
            var (down, up) = ButtonFlags(button);
            if (hold)
            {
                SendMouse(down);
                while (running) Thread.Sleep(20);
                SendMouse(up);
            }
            else
            {
                int count = 0;
                while (running)
                {
                    if (useFixedPos) SetCursorPos(px, py);
                    DoClick(button, dbl);
                    count++;
                    if (limited && count >= limit) break;
                    InterruptibleSleep(interval);
                }
            }
        }
        catch { }
        finally { Finish(); }
    }

    private void RunSequence(List<SeqPoint> pts, bool background, bool limited, int limit)
    {
        try
        {
            int pass = 0;
            while (running)
            {
                for (int i = 0; i < pts.Count && running; i++)
                {
                    var p = pts[i];
                    Highlight(i);
                    if (background)
                    {
                        DoBackgroundClick(p.X, p.Y, p.Button, p.DoubleClick);
                    }
                    else
                    {
                        SetCursorPos(p.X, p.Y);
                        DoClick(p.Button, p.DoubleClick);
                    }
                    InterruptibleSleep(p.DelayMs);
                }
                pass++;
                if (limited && pass >= limit) break;
            }
        }
        catch { }
        finally { Highlight(-1); Finish(); }
    }

    private void Highlight(int index)
    {
        if (!IsHandleCreated) return;
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

    private void Finish()
    {
        running = false;
        if (IsHandleCreated) BeginInvoke(OnStopped);
    }

    private void StopClicking() => running = false;

    private void OnStopped()
    {
        btnStart.Enabled = true;
        btnStop.Enabled = false;
        lblStatus.Text = $"Stopped. Press {hotkeyName} to start.";
    }
}
