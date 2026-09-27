using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using static AutoClicker.Native;

namespace AutoClicker;

public partial class Form1 : Form
{
    private const int HOTKEY_TOGGLE = 0xB001;
    private const int HOTKEY_RECEND = 0xB002;
    private const int HOTKEY_PANIC = 0xB003;
    // Per-profile hotkey ids start well clear of the three fixed ids above so a profile
    // can never collide with the toggle/end-recording/panic hotkeys by id.
    private const int ProfileHotkeyIdBase = 0xB010;
    private const string PanicMessage = "Panic stop — all buttons released.";
    private const uint VK_F8 = 0x77;
    private const uint VK_ESCAPE = 0x1B;

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
    private NumericUpDown numStartDelay = null!;
    private CheckBox chkPanic = null!;
    private CheckBox chkMinimizeToTray = null!;

    // ---- Profiles ----
    private CheckBox chkUseProfiles = null!;
    private ComboBox cmbProfile = null!, cmbProfileHotkey = null!;
    private Button btnProfileNew = null!, btnProfileRename = null!, btnProfileDuplicate = null!, btnProfileDelete = null!, btnProfileSave = null!;

    // ---- Tray icon ----
    private NotifyIcon trayIcon = null!;
    private ContextMenuStrip trayMenu = null!;
    private ToolStripMenuItem miShowHide = null!, miStart = null!, miStop = null!, miExit = null!;

    // ---- State ----
    private readonly SequenceRunner runner;
    private List<SeqAction> points = new();
    private Thread? worker;
    private volatile bool running;
    private int busy; // 0 = idle, 1 = a worker owns the engine. Guards start/stop overlap.
    private uint hotkeyVk = 0x75; // F6
    private string hotkeyName = "F6";
    private System.Windows.Forms.Timer? pickTimer;
    private int pickCountdown;
    private Action? pickDone;
    private AppSettings settings = null!;
    private bool panicKeyRegistered;
    private bool panicStopped;
    private System.Windows.Forms.Timer? startDelayTimer;
    private int startDelayCountdown;

    // Named, hotkey-switchable sequences. Empty by default: existing users have no
    // profiles.json yet, and chkUseProfiles defaults to off, so this never changes
    // behavior until the user opts in.
    private List<Profile> profiles = new();
    // Index into `profiles` of the profile currently loaded into `points`; -1 = none.
    private int activeProfileIndex = -1;
    // Guards programmatic ComboBox/CheckBox updates (restoring settings, refreshing the
    // profile list after New/Rename/Duplicate/Delete) from re-entering the same handlers
    // that respond to the user's own selections.
    private bool suppressProfileEvents;
    // Set when profiles.json existed but could not be read. Every save is then refused for
    // the session: writing an empty collection over a file that merely failed to parse
    // would destroy the user's saved sequences permanently.
    private bool profilesLoadFailed;
    private readonly List<int> registeredProfileHotkeyIds = new();

    // recording (low-level mouse hook)
    private bool recording;
    private IntPtr mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? hookProc; // kept alive to prevent GC

    public Form1()
    {
        // Load before BuildUi (called from InitializeComponent): BuildUi bakes hotkeyName
        // into the Start/Stop captions and the status label, so it must already reflect
        // the persisted hotkey by the time the controls are built.
        settings = AppSettings.Load();
        hotkeyVk = settings.HotkeyVk;
        hotkeyName = settings.HotkeyName;
        runner = new SequenceRunner(KeepGoing, OnRunnerStep, OnRunnerStepStarting);

        InitializeComponent();

        ApplySettings();
        UpdateEnabled();
    }

    // ---- Settings persistence ----
    private void ApplySettings()
    {
        numHours.Value = settings.IntervalHours;
        numMins.Value = settings.IntervalMinutes;
        numSecs.Value = settings.IntervalSeconds;
        numMs.Value = settings.IntervalMilliseconds;
        cmbButton.SelectedIndex = settings.MouseButton;
        cmbType.SelectedIndex = settings.ClickType;
        rbRepeatN.Checked = settings.RepeatLimited;
        rbRepeatUntil.Checked = !settings.RepeatLimited;
        numRepeat.Value = settings.RepeatCount;
        rbPick.Checked = settings.UsePickedPosition;
        rbCurrent.Checked = !settings.UsePickedPosition;
        numX.Value = settings.PickedX;
        numY.Value = settings.PickedY;
        chkSequence.Checked = settings.UseSequence;
        chkBackground.Checked = settings.BackgroundMode;
        chkAnchorPoints.Checked = settings.AnchorNewPoints;
        numJitterPx.Value = settings.JitterPixels;
        numJitterPct.Value = settings.JitterPercent;
        numStartDelay.Value = settings.StartDelaySeconds;
        chkPanic.Checked = settings.PanicKeyEnabled;
        chkMinimizeToTray.Checked = settings.MinimizeToTray;

        RestoreLastSequence();
        RestoreProfiles();
    }

    private void CaptureSettingsFromControls()
    {
        settings.IntervalHours = (int)numHours.Value;
        settings.IntervalMinutes = (int)numMins.Value;
        settings.IntervalSeconds = (int)numSecs.Value;
        settings.IntervalMilliseconds = (int)numMs.Value;
        settings.MouseButton = cmbButton.SelectedIndex;
        settings.ClickType = cmbType.SelectedIndex;
        settings.RepeatLimited = rbRepeatN.Checked;
        settings.RepeatCount = (int)numRepeat.Value;
        settings.UsePickedPosition = rbPick.Checked;
        settings.PickedX = (int)numX.Value;
        settings.PickedY = (int)numY.Value;
        settings.UseSequence = chkSequence.Checked;
        settings.BackgroundMode = chkBackground.Checked;
        settings.AnchorNewPoints = chkAnchorPoints.Checked;
        settings.JitterPixels = (int)numJitterPx.Value;
        settings.JitterPercent = (int)numJitterPct.Value;
        settings.HotkeyVk = hotkeyVk;
        settings.HotkeyName = hotkeyName;
        settings.StartDelaySeconds = (int)numStartDelay.Value;
        settings.PanicKeyEnabled = chkPanic.Checked;
        settings.MinimizeToTray = chkMinimizeToTray.Checked;

        settings.UseProfiles = chkUseProfiles.Checked;
        // ActiveProfileName is kept in sync as profiles are switched, created, renamed or
        // deleted; re-assert it here too and persist whatever is currently on screen, so
        // closing the app while mid-edit on a profile never silently discards that edit.
        if (chkUseProfiles.Checked && activeProfileIndex >= 0 && activeProfileIndex < profiles.Count)
        {
            settings.ActiveProfileName = profiles[activeProfileIndex].Name;
            SaveCurrentPointsToProfile(activeProfileIndex);
        }
    }

    // Restores the sequence that was open last time, with no dialog and no user-visible
    // failure beyond the status line: a missing or corrupt file just means "start empty".
    private void RestoreLastSequence()
    {
        string path = settings.LastSequencePath;
        if (string.IsNullOrEmpty(path)) return;

        if (!File.Exists(path))
        {
            settings.LastSequencePath = "";
            return;
        }

        try
        {
            var loaded = SequenceFile.Deserialize(File.ReadAllText(path));
            points = loaded;
            RefreshList();
            chkSequence.Checked = points.Count > 0;
            lblStatus.Text = $"Restored {points.Count} actions from {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or SequenceFile.UnsupportedVersionException)
        {
            settings.LastSequencePath = "";
            lblStatus.Text = "Could not restore the last sequence — starting empty.";
        }
    }

    private void BuildUi()
    {
        Text = "Auto Clicker";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch (ArgumentException) { }
        catch (IOException) { }
        BuildTrayIcon(); // depends on Icon/Text already being set above
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
        root.Controls.Add(BuildProfilesGroup());
        root.Controls.Add(BuildRunOptionsGroup());
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

    // ===== Profiles (named, switchable sequences) =====
    private GroupBox BuildProfilesGroup()
    {
        var grp = NewGroup("Profiles (switch between several named sequences)");
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };

        chkUseProfiles = new CheckBox { Text = "Use profiles", AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        chkUseProfiles.CheckedChanged += (_, _) => OnUseProfilesChanged();
        t.Controls.Add(chkUseProfiles);

        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        row.Controls.Add(Lbl("Profile:"));
        cmbProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Margin = new Padding(3, 4, 12, 3) };
        cmbProfile.SelectedIndexChanged += CmbProfile_SelectedIndexChanged;
        row.Controls.Add(cmbProfile);

        btnProfileNew = SeqBtn("New");
        btnProfileNew.Click += (_, _) => NewProfile();
        btnProfileRename = SeqBtn("Rename");
        btnProfileRename.Click += (_, _) => RenameProfile();
        btnProfileDuplicate = SeqBtn("Duplicate");
        btnProfileDuplicate.Click += (_, _) => DuplicateProfile();
        btnProfileDelete = SeqBtn("Delete");
        btnProfileDelete.Click += (_, _) => DeleteProfile();
        btnProfileSave = SeqBtn("Save to profile");
        btnProfileSave.Click += (_, _) => SaveToProfile();
        row.Controls.Add(btnProfileNew);
        row.Controls.Add(btnProfileRename);
        row.Controls.Add(btnProfileDuplicate);
        row.Controls.Add(btnProfileDelete);
        row.Controls.Add(btnProfileSave);
        t.Controls.Add(row);

        var hkRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 3, 0, 0) };
        hkRow.Controls.Add(Lbl("Profile hotkey:"));
        cmbProfileHotkey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Margin = new Padding(3, 4, 3, 3) };
        cmbProfileHotkey.Items.Add("None");
        for (int i = 1; i <= 12; i++) cmbProfileHotkey.Items.Add("F" + i);
        cmbProfileHotkey.SelectedIndexChanged += CmbProfileHotkey_SelectedIndexChanged;
        hkRow.Controls.Add(cmbProfileHotkey);
        t.Controls.Add(hkRow);

        grp.Controls.Add(t);
        return grp;
    }

    // ===== Start delay + panic key =====
    private GroupBox BuildRunOptionsGroup()
    {
        var grp = NewGroup("Run options");
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = false };
        flow.Controls.Add(Lbl("Start after"));
        numStartDelay = NewNum(0, 300, 0);
        flow.Controls.Add(numStartDelay);
        flow.Controls.Add(Lbl("seconds (0 = immediately)"));
        chkPanic = new CheckBox { Text = "Esc panic-stops the run", AutoSize = true, Checked = true, Margin = new Padding(18, 6, 3, 3) };
        flow.Controls.Add(chkPanic);
        chkMinimizeToTray = new CheckBox { Text = "Minimize to tray", AutoSize = true, Margin = new Padding(18, 6, 3, 3) };
        flow.Controls.Add(chkMinimizeToTray);
        grp.Controls.Add(flow);
        return grp;
    }

    // ===== System tray =====
    private void BuildTrayIcon()
    {
        trayMenu = new ContextMenuStrip();
        miShowHide = new ToolStripMenuItem("Show/Hide");
        miShowHide.Click += (_, _) => { if (Visible) HideToTray(); else RestoreFromTray(); };
        miStart = new ToolStripMenuItem("Start", null, (_, _) => StartClicking());
        miStop = new ToolStripMenuItem("Stop", null, (_, _) => StopClicking()) { Enabled = false };
        // Routed through Close(), not Environment.Exit: Close() runs OnFormClosing, which is
        // what saves settings and joins the worker thread. Environment.Exit would skip both.
        miExit = new ToolStripMenuItem("Exit", null, (_, _) => Close());
        trayMenu.Items.Add(miShowHide);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(miStart);
        trayMenu.Items.Add(miStop);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(miExit);

        trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = Text,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // Minimizing normally just minimizes to the taskbar; only route to the tray when the
    // user has opted in via chkMinimizeToTray. Guarded with `is { }` rather than a plain
    // null-check because AutoSize layout can fire Resize events while BuildUi is still
    // constructing controls, before the checkbox field is assigned.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && chkMinimizeToTray is { Checked: true })
            HideToTray();
    }

    // ===== Start / Stop =====
    private TableLayoutPanel BuildButtonsRow()
    {
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // Captions follow the configured hotkey, which is restored from settings before
        // BuildUi runs — a hardcoded "F6" here would contradict the status label.
        btnStart = new Button { Text = $"Start ({hotkeyName})", Dock = DockStyle.Fill, Height = 50, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        btnStart.Click += (_, _) => StartClicking();
        btnStop = new Button { Text = $"Stop ({hotkeyName})", Dock = DockStyle.Fill, Height = 50, Enabled = false, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
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

    // AutoSize rather than a fixed width: at higher DPI scalings a fixed 138 px clipped the
    // longer captions ("Add cursor pos" rendered as "Add cursor"). MinimumSize keeps the
    // column of buttons visually even when the captions are short.
    private static Button SeqBtn(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(138, 30),
        Margin = new Padding(2)
    };
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
    private void SaveSequence()
    {
        if (points.Count == 0) { lblStatus.Text = "Nothing to save — sequence is empty."; return; }
        using var sfd = new SaveFileDialog { Filter = "Auto Clicker sequence (*.acseq)|*.acseq|JSON (*.json)|*.json", DefaultExt = "acseq", FileName = "sequence.acseq" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(sfd.FileName, SequenceFile.Serialize(points));
            settings.LastSequencePath = sfd.FileName;
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
            var loaded = SequenceFile.Deserialize(File.ReadAllText(ofd.FileName));
            points = loaded;
            RefreshList();
            chkSequence.Checked = points.Count > 0;
            settings.LastSequencePath = ofd.FileName;
            lblStatus.Text = $"Loaded {points.Count} actions from {Path.GetFileName(ofd.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or SequenceFile.UnsupportedVersionException)
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
        // Registers any profile hotkeys restored from settings. Deferred to here (rather
        // than called directly from RestoreProfiles during the constructor) for the same
        // reason RegisterHotkeys() is: RegisterHotKey needs a real HWND, and this is the
        // first point one is guaranteed to exist.
        RegisterProfileHotkeys();
    }

    // Esc is deliberately NOT registered here alongside F6/F8. RegisterHotKey grabs the key
    // globally, stealing it from every other app on the machine for as long as this process
    // is open. That's fine for F6/F8 (uncommon, user-chosen), but Esc is used constantly
    // elsewhere (closing dialogs, cancelling menus, games). So it is only ever armed for the
    // duration of an actual run — claimed in StartClicking, released in OnStopped and
    // OnFormClosing — never at startup.
    private bool RegisterPanicKey()
    {
        if (!chkPanic.Checked) { panicKeyRegistered = false; return true; } // not requested; not a failure
        panicKeyRegistered = RegisterHotKey(Handle, HOTKEY_PANIC, 0, VK_ESCAPE);
        return panicKeyRegistered;
    }

    private void UnregisterPanicKey()
    {
        if (!panicKeyRegistered) return;
        UnregisterHotKey(Handle, HOTKEY_PANIC);
        panicKeyRegistered = false;
    }

    // ---- Profiles ----

    /// <summary>
    /// Single gate for every profile write. Refuses to save when the file failed to load,
    /// so a parse error can never be promoted into permanent data loss by the next save.
    /// </summary>
    private void SaveProfiles()
    {
        if (profilesLoadFailed) return;
        ProfileStore.Save(profiles);
    }

    // Restores profiles.json and whichever profile (if any) was active last session,
    // without letting the combo box / checkbox events fire mid-restore against state
    // that isn't fully loaded yet. Hotkey registration is deliberately left to
    // OnHandleCreated — see the comment there.
    private void RestoreProfiles()
    {
        profiles = ProfileStore.Load(out profilesLoadFailed);
        if (profilesLoadFailed)
        {
            lblStatus.Text = "Could not read profiles.json — it has been kept as profiles.json.corrupt\r\n"
                           + "and profile saving is disabled this session so it can't be overwritten.";
        }

        suppressProfileEvents = true;
        try
        {
            cmbProfile.Items.Clear();
            foreach (Profile p in profiles) cmbProfile.Items.Add(p.Name);

            chkUseProfiles.Checked = settings.UseProfiles;

            activeProfileIndex = -1;
            if (profiles.Count > 0)
            {
                int idx = profiles.FindIndex(p => p.Name == settings.ActiveProfileName);
                if (idx < 0) idx = 0;
                cmbProfile.SelectedIndex = idx;
                activeProfileIndex = idx;
            }
        }
        finally { suppressProfileEvents = false; }

        // Only load the profile's actions over `points` when profiles are actually in use —
        // otherwise RestoreLastSequence's ad-hoc sequence (already loaded above) must stand.
        if (settings.UseProfiles && activeProfileIndex >= 0)
            LoadProfileIntoPoints(profiles[activeProfileIndex]);

        UpdateProfileHotkeyCombo();
        UpdateProfileControlsEnabled();
    }

    // Repopulates the combo box from `profiles`, optionally re-selecting a profile by name
    // (its index may have shifted). Suppressed so this never re-triggers the switch logic.
    private void RefreshProfileCombo(string? selectName)
    {
        suppressProfileEvents = true;
        try
        {
            cmbProfile.Items.Clear();
            foreach (Profile p in profiles) cmbProfile.Items.Add(p.Name);
            if (selectName != null)
            {
                int idx = profiles.FindIndex(p => p.Name == selectName);
                if (idx >= 0) cmbProfile.SelectedIndex = idx;
            }
        }
        finally { suppressProfileEvents = false; }
    }

    private void LoadProfileIntoPoints(Profile p)
    {
        points = p.Actions.Select(a => a.Clone()).ToList();
        RefreshList();
        chkSequence.Checked = points.Count > 0;
    }

    private void SaveCurrentPointsToProfile(int index)
    {
        if (index < 0 || index >= profiles.Count) return;
        profiles[index].Actions = points.Select(a => a.Clone()).ToList();
        SaveProfiles();
    }

    private void CmbProfile_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressProfileEvents) return;
        SwitchToProfile(cmbProfile.SelectedIndex);
    }

    // Auto-saves the outgoing profile's current edits before loading the new one, so
    // switching profiles can never silently discard a recorded sequence — the worst
    // outcome this feature could produce. Chosen over a confirm/discard prompt because a
    // profile switch is meant to be a quick, frequent action (that's the point of having
    // several), and a prompt on every switch would defeat that.
    private void SwitchToProfile(int newIndex)
    {
        if (newIndex == activeProfileIndex) return;
        if (activeProfileIndex >= 0 && activeProfileIndex < profiles.Count)
            SaveCurrentPointsToProfile(activeProfileIndex);

        activeProfileIndex = newIndex;
        if (newIndex >= 0 && newIndex < profiles.Count)
        {
            LoadProfileIntoPoints(profiles[newIndex]);
            settings.ActiveProfileName = profiles[newIndex].Name;
            lblStatus.Text = $"Switched to profile \"{profiles[newIndex].Name}\".";
        }

        UpdateProfileHotkeyCombo();
    }

    private void NewProfile()
    {
        string? name = PromptForName("New profile", "Profile name:", "");
        if (name == null) return;

        // The outgoing profile's edits would otherwise vanish the moment the new, empty
        // profile takes over `points` — same reasoning as SwitchToProfile.
        if (activeProfileIndex >= 0) SaveCurrentPointsToProfile(activeProfileIndex);

        string unique = ProfileStore.UniqueName(profiles, string.IsNullOrWhiteSpace(name) ? "Profile" : name.Trim());
        var p = new Profile { Name = unique };
        profiles.Add(p);
        SaveProfiles();

        activeProfileIndex = profiles.Count - 1;
        RefreshProfileCombo(unique);
        LoadProfileIntoPoints(p);
        settings.ActiveProfileName = unique;

        UpdateProfileHotkeyCombo();
        UpdateProfileControlsEnabled();
        RegisterProfileHotkeys();
        lblStatus.Text = $"Created profile \"{unique}\".";
    }

    private void RenameProfile()
    {
        if (activeProfileIndex < 0 || activeProfileIndex >= profiles.Count) return;
        Profile p = profiles[activeProfileIndex];
        string? name = PromptForName("Rename profile", "Profile name:", p.Name);
        if (name == null) return;

        string desired = string.IsNullOrWhiteSpace(name) ? p.Name : name.Trim();
        string unique = ProfileStore.UniqueName(profiles.Where(x => x != p), desired);
        p.Name = unique;
        SaveProfiles();

        settings.ActiveProfileName = unique;
        RefreshProfileCombo(unique);
        lblStatus.Text = $"Renamed to \"{unique}\".";
    }

    private void DuplicateProfile()
    {
        if (activeProfileIndex < 0 || activeProfileIndex >= profiles.Count) return;

        // Persist any in-progress edits into the source profile first, so the duplicate
        // reflects what's on screen rather than a stale on-disk copy.
        SaveCurrentPointsToProfile(activeProfileIndex);

        Profile source = profiles[activeProfileIndex];
        string unique = ProfileStore.UniqueName(profiles, source.Name + " (copy)");
        var copy = new Profile { Name = unique, Actions = source.Actions.Select(a => a.Clone()).ToList() };
        profiles.Add(copy);
        SaveProfiles();

        activeProfileIndex = profiles.Count - 1;
        RefreshProfileCombo(unique);
        LoadProfileIntoPoints(copy);
        settings.ActiveProfileName = unique;

        UpdateProfileHotkeyCombo();
        UpdateProfileControlsEnabled();
        lblStatus.Text = $"Duplicated \"{source.Name}\" as \"{unique}\".";
    }

    private void DeleteProfile()
    {
        if (activeProfileIndex < 0 || activeProfileIndex >= profiles.Count) return;
        Profile p = profiles[activeProfileIndex];

        DialogResult result = MessageBox.Show(this,
            $"Delete profile \"{p.Name}\"? This permanently removes its saved sequence.",
            "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;

        profiles.RemoveAt(activeProfileIndex);
        SaveProfiles();
        activeProfileIndex = -1; // the deleted index no longer refers to anything

        string? nextName = profiles.Count > 0 ? profiles[0].Name : null;
        RefreshProfileCombo(nextName);

        if (nextName != null)
        {
            activeProfileIndex = 0;
            LoadProfileIntoPoints(profiles[0]);
            settings.ActiveProfileName = profiles[0].Name;
        }
        else
        {
            points.Clear();
            RefreshList();
            settings.ActiveProfileName = "";
        }

        UpdateProfileHotkeyCombo();
        UpdateProfileControlsEnabled();
        RegisterProfileHotkeys();
        lblStatus.Text = $"Deleted profile \"{p.Name}\".";
    }

    private void SaveToProfile()
    {
        if (activeProfileIndex < 0 || activeProfileIndex >= profiles.Count)
        {
            lblStatus.Text = "Select a profile first.";
            return;
        }
        SaveCurrentPointsToProfile(activeProfileIndex);
        lblStatus.Text = $"Saved {points.Count} actions to \"{profiles[activeProfileIndex].Name}\".";
    }

    private void UpdateProfileHotkeyCombo()
    {
        suppressProfileEvents = true;
        try
        {
            if (activeProfileIndex < 0 || activeProfileIndex >= profiles.Count)
            {
                cmbProfileHotkey.SelectedIndex = 0; // "None"
                return;
            }
            uint vk = profiles[activeProfileIndex].HotkeyVk;
            cmbProfileHotkey.SelectedIndex = vk == 0 ? 0 : (int)(vk - 0x70 + 1);
        }
        finally { suppressProfileEvents = false; }
    }

    private void CmbProfileHotkey_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressProfileEvents) return;
        if (activeProfileIndex < 0 || activeProfileIndex >= profiles.Count) return;

        int sel = cmbProfileHotkey.SelectedIndex; // 0 = None, 1..12 = F1..F12
        uint vk = sel <= 0 ? 0u : (uint)(0x70 + (sel - 1));

        // Reject a collision with the main start/stop hotkey or F8 (ends recording): either
        // one would otherwise silently never fire once claimed here, or break recording.
        if (vk != 0 && (vk == hotkeyVk || vk == VK_F8))
        {
            lblStatus.Text = vk == hotkeyVk
                ? $"F{sel} is already the start/stop hotkey — pick a different key."
                : $"F{sel} is reserved for ending recording — pick a different key.";
            UpdateProfileHotkeyCombo(); // revert to the profile's actual (unchanged) hotkey
            return;
        }

        // Reject a collision with another profile's hotkey too — first-claimed wins, same
        // rule ProfileStore.Load() already applies to a hand-edited profiles.json.
        if (vk != 0)
        {
            for (int i = 0; i < profiles.Count; i++)
            {
                if (i == activeProfileIndex || profiles[i].HotkeyVk != vk) continue;
                lblStatus.Text = $"F{sel} is already assigned to profile \"{profiles[i].Name}\" — pick a different key.";
                UpdateProfileHotkeyCombo();
                return;
            }
        }

        Profile p = profiles[activeProfileIndex];
        p.HotkeyVk = vk;
        p.HotkeyName = vk == 0 ? "" : "F" + sel;
        SaveProfiles();
        RegisterProfileHotkeys();
        lblStatus.Text = vk == 0 ? $"Removed hotkey from \"{p.Name}\"." : $"\"{p.Name}\" now runs on F{sel}.";
    }

    private void OnUseProfilesChanged()
    {
        settings.UseProfiles = chkUseProfiles.Checked;
        if (chkUseProfiles.Checked)
        {
            if (activeProfileIndex >= 0 && activeProfileIndex < profiles.Count)
            {
                LoadProfileIntoPoints(profiles[activeProfileIndex]);
                lblStatus.Text = $"Using profile \"{profiles[activeProfileIndex].Name}\".";
            }
            else
            {
                lblStatus.Text = "No profiles yet — click New to create one.";
            }
            RegisterProfileHotkeys();
        }
        else
        {
            // Save whatever is on screen back to the active profile before returning to the
            // single ad-hoc sequence, then fall back to exactly the pre-profiles behavior.
            if (activeProfileIndex >= 0 && activeProfileIndex < profiles.Count)
                SaveCurrentPointsToProfile(activeProfileIndex);
            UnregisterProfileHotkeys();
            RestoreLastSequence();
        }

        UpdateProfileControlsEnabled();
    }

    private void UpdateProfileControlsEnabled()
    {
        bool on = chkUseProfiles.Checked;
        bool hasSelection = on && activeProfileIndex >= 0 && activeProfileIndex < profiles.Count;
        cmbProfile.Enabled = on && profiles.Count > 0;
        btnProfileNew.Enabled = on;
        btnProfileRename.Enabled = hasSelection;
        btnProfileDuplicate.Enabled = hasSelection;
        btnProfileDelete.Enabled = hasSelection;
        btnProfileSave.Enabled = hasSelection;
        cmbProfileHotkey.Enabled = hasSelection;
    }

    // Registers one global hotkey per profile that has one assigned, using ids
    // ProfileHotkeyIdBase + index so they can never collide with HOTKEY_TOGGLE/RECEND/PANIC.
    // Always unregisters everything currently held first: the profile count and hotkey
    // assignments can both change between calls (New/Rename/Duplicate/Delete/hotkey edit),
    // and a stale registration would otherwise keep claiming a key globally forever — the
    // same leak RegisterHotKey risks everywhere else it's used in this form.
    private void RegisterProfileHotkeys()
    {
        UnregisterProfileHotkeys();
        if (lblStatus is null) return; // the handle can be created before BuildUi finishes
        if (!chkUseProfiles.Checked) return;

        for (int i = 0; i < profiles.Count; i++)
        {
            uint vk = profiles[i].HotkeyVk;
            if (vk == 0) continue;
            // A collision with the main hotkey or F8 is already rejected at assignment time
            // (CmbProfileHotkey_SelectedIndexChanged), so reaching here with one is only
            // possible via a hand-edited profiles.json — skip it rather than register
            // something that would silently steal F6/F8 from the rest of the app.
            if (vk == hotkeyVk || vk == VK_F8) continue;

            int id = ProfileHotkeyIdBase + i;
            if (RegisterHotKey(Handle, id, 0, vk))
                registeredProfileHotkeyIds.Add(id);
            else
                lblStatus.Text = $"{profiles[i].HotkeyName} is already claimed by another app — \"{profiles[i].Name}\" hotkey OFF.";
        }
    }

    private void UnregisterProfileHotkeys()
    {
        foreach (int id in registeredProfileHotkeyIds) UnregisterHotKey(Handle, id);
        registeredProfileHotkeyIds.Clear();
    }

    // Minimal modal text-entry dialog, styled like the hotkey-setting dialog above: this
    // keeps Profiles from needing a new dialog abstraction for what's otherwise a single
    // TextBox + OK/Cancel.
    private string? PromptForName(string title, string prompt, string initialValue)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 120),
            MaximizeBox = false,
            MinimizeBox = false
        };
        var lbl = new Label { Text = prompt, Location = new Point(16, 20), AutoSize = true };
        var txt = new TextBox { Location = new Point(16, 44), Width = 288, Text = initialValue };
        var ok = new Button { Text = "OK", Location = new Point(140, 76), Size = new Size(75, 32), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(223, 76), Size = new Size(82, 32), DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        txt.SelectAll();

        return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
    }

    // Pressing a profile hotkey both selects that profile and starts it, routed through the
    // same StartClicking() the toolbar Start button and main hotkey use, so the busy claim,
    // start-delay countdown and panic-key registration all still apply — a profile hotkey
    // is not a shortcut around them.
    private void HandleProfileHotkey(int index)
    {
        if (index < 0 || index >= profiles.Count) return;

        // Refuse the whole switch while a run owns the engine. Switching first and letting
        // StartClicking() then early-return on `busy` left the list showing a DIFFERENT
        // profile than the one executing — nothing started, nothing stopped, no explanation.
        if (Volatile.Read(ref busy) != 0 || running)
        {
            lblStatus.Text = $"Stop the current run before switching to \"{profiles[index].Name}\".";
            return;
        }

        SwitchToProfile(index);
        suppressProfileEvents = true;
        try { cmbProfile.SelectedIndex = index; }
        finally { suppressProfileEvents = false; }
        UpdateProfileHotkeyCombo();

        StartClicking();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // A failed save must not block closing, and there's nowhere useful left to report it.
        CaptureSettingsFromControls();
        settings.Save();

        running = false;
        if (recording) StopRecording();

        pickTimer?.Stop();
        pickTimer?.Dispose();
        pickTimer = null;
        pickDone = null;

        // Must run before Join below: if a start-delay countdown is pending, `worker` holds
        // a Thread that was built but never started, and Thread.Join throws on one of those.
        CancelPendingStartDelay();

        // Wait for the worker to unwind. The worker is a background thread, so without this
        // the process can exit mid-"hold" and leave the mouse button physically stuck down.
        // It polls `running` every <= 20 ms, so this returns almost immediately.
        worker?.Join(2000);

        UnregisterHotKey(Handle, HOTKEY_TOGGLE);
        UnregisterHotKey(Handle, HOTKEY_RECEND);
        UnregisterPanicKey();
        UnregisterProfileHotkeys();

        // Visible = false first, then Dispose: without this a ghost icon lingers in the
        // tray until the user happens to hover over its old location.
        trayIcon.Visible = false;
        trayIcon.Dispose();

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
            if (id == HOTKEY_PANIC)
            {
                panicStopped = true;
                CancelPendingStartDelay();
                running = false;
                InputSender.ReleaseAllButtons();
                lblStatus.Text = PanicMessage;
                return;
            }
            if (id >= ProfileHotkeyIdBase && id < ProfileHotkeyIdBase + profiles.Count)
            {
                if (recording) { StopRecording(); return; }
                HandleProfileHotkey(id - ProfileHotkeyIdBase);
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

    private void StartClicking()
    {
        if (recording) return;

        // Claim the engine. `running` alone is not enough: after Stop, the previous worker
        // is still unwinding and its finally-block sets running = false — which would
        // immediately kill a run started in that window. busy is only released by Finish(),
        // once the old worker is genuinely done — or, if the run never leaves its start-delay
        // countdown, by CancelPendingStartDelay instead (Finish() never runs for that thread).
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;

        panicStopped = false;
        bool seq = chkSequence.Checked && points.Count > 0;
        bool limited = rbRepeatN.Checked;
        int limit = (int)numRepeat.Value;
        int jitterPx = (int)numJitterPx.Value;
        int jitterPct = (int)numJitterPct.Value;
        int delaySeconds = (int)numStartDelay.Value;

        running = true;
        SetRunningButtonsState(running: true);

        bool panicArmed = RegisterPanicKey();
        string panicWarn = chkPanic.Checked && !panicArmed
            ? " Esc is already claimed by another app — panic key OFF."
            : "";

        // Build the thread now, capturing every run parameter at the moment Start was
        // pressed — not after a delay elapses — so nothing the user changes mid-countdown
        // can silently alter the run that was actually requested.
        string runDescription;
        if (seq)
        {
            bool bg = chkBackground.Checked;
            var acts = points.Select(p => p.Clone()).ToList();
            runDescription = $"Running {acts.Count} actions{(bg ? " (background)" : "")}... press {hotkeyName} to stop.";
            var options = new RunOptions
            {
                Background = bg,
                Limited = limited,
                Limit = limit,
                JitterPixels = jitterPx,
                JitterPercent = jitterPct
            };
            worker = new Thread(() => RunSequenceWorker(acts, options)) { IsBackground = true };
        }
        else
        {
            bool hold = cmbType.SelectedIndex == 2;
            bool dbl = cmbType.SelectedIndex == 1;
            bool useFixedPos = rbPick.Checked;
            int px = (int)numX.Value, py = (int)numY.Value;
            int interval = IntervalMs();
            int button = cmbButton.SelectedIndex;
            runDescription = hold ? $"Holding {cmbButton.Text} button... press {hotkeyName} to stop."
                                  : $"Clicking... press {hotkeyName} to stop.";
            var options = new SingleRunOptions
            {
                Hold = hold,
                DoubleClick = dbl,
                Button = button,
                UseFixedPosition = useFixedPos,
                X = px,
                Y = py,
                IntervalMs = interval,
                // A repeat count is meaningless for press-and-hold: computed at the call site,
                // same as before the engine was extracted, so RunSingle itself stays dumb about it.
                Limited = limited && !hold,
                Limit = limit,
                JitterPixels = jitterPx,
                JitterPercent = jitterPct
            };
            worker = new Thread(() => RunSingleWorker(options)) { IsBackground = true };
        }

        if (delaySeconds <= 0)
        {
            lblStatus.Text = runDescription + panicWarn;
            StartWorkerThread();
            return;
        }

        startDelayCountdown = delaySeconds;
        startDelayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        lblStatus.Text = $"Starting in {startDelayCountdown} s... press {hotkeyName} or Esc to cancel.{panicWarn}";
        startDelayTimer.Tick += (_, _) =>
        {
            startDelayCountdown--;
            if (startDelayCountdown > 0)
            {
                lblStatus.Text = $"Starting in {startDelayCountdown} s... press {hotkeyName} or Esc to cancel.{panicWarn}";
                return;
            }
            startDelayTimer!.Stop();
            startDelayTimer.Dispose();
            startDelayTimer = null;
            lblStatus.Text = runDescription + panicWarn;
            StartWorkerThread();
        };
        startDelayTimer.Start();
    }

    private void StartWorkerThread()
    {
        try
        {
            worker!.Start();
        }
        catch (Exception ex)
        {
            running = false;
            worker = null;
            UnregisterPanicKey();
            Interlocked.Exchange(ref busy, 0);
            OnStopped();
            lblStatus.Text = "Could not start: " + ex.Message;
        }
    }

    // Cancels a pending start-delay countdown, if any. The thread built in StartClicking
    // was never started in that case, so Finish() will never run for it — this is the only
    // path that releases `busy` and restores the UI for a countdown that never went live.
    private bool CancelPendingStartDelay()
    {
        if (startDelayTimer == null) return false;
        startDelayTimer.Stop();
        startDelayTimer.Dispose();
        startDelayTimer = null;
        worker = null; // discard the never-started thread
        Interlocked.Exchange(ref busy, 0);
        OnStopped();
        return true;
    }

    // SequenceRunner itself never catches exceptions — it propagates them so the caller
    // decides how to report an abort. This is that decision: surface it to the status label
    // and always release the engine, exactly as the pre-extraction code did.
    private void RunSingleWorker(SingleRunOptions options)
    {
        try { runner.RunSingle(options); }
        catch (Exception ex) { ReleaseAfterFailure(ex); }
        finally { Finish(); }
    }

    private void RunSequenceWorker(List<SeqAction> acts, RunOptions options)
    {
        try { runner.RunSequence(acts, options); }
        catch (Exception ex) { ReleaseAfterFailure(ex); }
        finally { Finish(); }
    }

    /// <summary>
    /// A run that ended by throwing may have died between a button-down and its matching
    /// up — an elevated foreground window makes the release itself throw, for instance.
    /// Release everything before reporting, so a failed run can't leave a button held.
    /// The CLI already does this unconditionally; the GUI previously relied on the user
    /// noticing and hitting the panic key.
    /// </summary>
    private void ReleaseAfterFailure(Exception ex)
    {
        InputSender.ReleaseAllButtons();
        ReportError(ex);
    }

    // Fires just before a step is attempted — including one that then blocks for a while (a
    // WaitPixel can sit here for its whole timeout) — so the row is painted amber right away
    // rather than only once it finishes.
    private void OnRunnerStepStarting(int index) => Highlight(index);

    // Fires once a step has been attempted. A false `performed` means a pixel gate suppressed
    // it; recolor that row the skipped grey-blue. index -1 means the run ended: clear everything.
    private void OnRunnerStep(int index, bool performed)
    {
        if (index < 0) { Highlight(-1); return; }
        if (!performed) Highlight(index, skipped: true);
    }

    private bool KeepGoing() => running;

    private static readonly Color RunningHighlight = Color.FromArgb(255, 230, 160); // amber: this row is executing
    private static readonly Color SkippedHighlight = Color.FromArgb(210, 224, 236); // calmer grey-blue: gate suppressed this row

    private void Highlight(int index, bool skipped = false)
    {
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                foreach (ListViewItem it in lvPoints.Items) it.BackColor = lvPoints.BackColor;
                if (index >= 0 && index < lvPoints.Items.Count)
                {
                    lvPoints.Items[index].BackColor = skipped ? SkippedHighlight : RunningHighlight;
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

    private void StopClicking()
    {
        running = false;
        CancelPendingStartDelay();
    }

    private void OnStopped()
    {
        UnregisterPanicKey();
        SetRunningButtonsState(running: false);
        // Confirming the buttons were force-released is the whole point of the panic key,
        // so don't let the worker's own async "Stopped" message land on top of that.
        lblStatus.Text = panicStopped ? PanicMessage : $"Stopped. Press {hotkeyName} to start.";
    }

    // Keeps the tray menu's Start/Stop items in step with the main buttons — this is the only
    // place either pair is ever set, so they can't drift apart.
    private void SetRunningButtonsState(bool running)
    {
        btnStart.Enabled = !running;
        btnStop.Enabled = running;
        miStart.Enabled = !running;
        miStop.Enabled = running;
    }
}
