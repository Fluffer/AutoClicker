using System.Globalization;
using System.Text.Json;
using static AutoClicker.Native;

namespace AutoClicker;

public partial class Form1 : Form
{
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
    private List<SeqAction> points = new();
    // How many actions `points` held when the current recording started, so the live and
    // finished status lines report only what THIS session recorded.
    private int recordStartCount;
    private uint hotkeyVk = 0x75; // F6
    private string hotkeyName = "F6";
    private System.Windows.Forms.Timer? pickTimer;
    private int pickCountdown;
    private Action? pickDone;
    private AppSettings settings = null!;
    // Guards programmatic ComboBox/CheckBox updates (restoring settings, refreshing the
    // profile list after New/Rename/Duplicate/Delete) from re-entering the same handlers
    // that respond to the user's own selections.
    private bool suppressProfileEvents;

    // Collaborators: hotkeys, recording, run orchestration and profile state live outside
    // the form — only the form touches controls.
    private readonly HotkeyManager hotkeyManager;
    private readonly RecordingController recordingController;
    private readonly ProfileController profileController;
    private readonly RunController runController;

    public Form1()
    {
        // Load before BuildUi (called from InitializeComponent): BuildUi bakes hotkeyName
        // into the Start/Stop captions and the status label, so it must already reflect
        // the persisted hotkey by the time the controls are built.
        settings = AppSettings.Load();
        hotkeyVk = settings.HotkeyVk;
        hotkeyName = settings.HotkeyName;

        hotkeyManager = new HotkeyManager(() => Handle, ReportStatus);
        recordingController = new RecordingController();
        profileController = new ProfileController();
        runController = new RunController(
            () => hotkeyManager.RegisterPanic(chkPanic.Checked),
            () => hotkeyManager.UnregisterPanic(),
            () => hotkeyName,
            MarshalToUi);

        WireEvents();

        InitializeComponent();

        ApplySettings();
        UpdateEnabled();
    }

    private void WireEvents()
    {
        hotkeyManager.TogglePressed += () =>
        {
            if (recordingController.IsRecording) { StopRecording(); return; }
            if (runController.IsRunning) runController.Stop(); else StartClicking();
        };
        hotkeyManager.EndRecordingPressed += () => { if (recordingController.IsRecording) StopRecording(); };
        hotkeyManager.PanicPressed += () => runController.PanicStop();
        hotkeyManager.ProfileHotkeyPressed += HandleProfileHotkey;
        recordingController.ActionRecorded += AddRecordedAction;
        recordingController.EndRecordingRequested += StopRecording;
        runController.StatusChanged += ReportStatus;
        runController.RunningChanged += SetRunningButtonsState;
        runController.StepStarting += OnRunnerStepStarting;
        runController.StepCompleted += OnRunnerStep;
    }

    // Null-safe status write: hotkey registration can run before BuildUi has finished.
    private void ReportStatus(string message)
    {
        if (lblStatus is not null) lblStatus.Text = message;
    }

    // Guarded BeginInvoke, as the worker used to reach OnStopped / ReportError.
    private void MarshalToUi(Action action)
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
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
        if (chkUseProfiles.Checked && profileController.ActiveProfileIndex >= 0 && profileController.ActiveProfileIndex < profileController.Profiles.Count)
        {
            settings.ActiveProfileName = profileController.Profiles[profileController.ActiveProfileIndex].Name;
            profileController.SaveCurrentPointsToProfile(profileController.ActiveProfileIndex, points);
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

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows };
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
            Dock = DockStyle.Fill, Height = 56, Margin = new Padding(3, 6, 3, 3),
            BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray
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

        lvPoints = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, Width = 500, Height = 200, Margin = new Padding(3) };
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

        using var dlg = new ActionEditorForm(seed, "Add action", points);
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
        // Block depth is recomputed after every mutation (add/edit/remove/move/load), so
        // the indent guides always reflect the current structure.
        int[] depth = ControlFlow.BuildDepth(points);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var item = new ListViewItem((i + 1).ToString(CultureInfo.InvariantCulture));
            item.SubItems.Add(IndentGuide(depth[i]) + p.Describe());
            item.SubItems.Add(p.DescribeTarget());
            item.SubItems.Add(p.DelayMs.ToString(CultureInfo.InvariantCulture));
            lvPoints.Items.Add(item);
        }
        lvPoints.EndUpdate();
    }

    // ListView subitems can't be indented with ListViewItem.Indent (that shifts only the
    // first column), so the Action column gets text indent guides instead.
    private static string IndentGuide(int depth) => depth <= 0 ? "" : new string(' ', depth * 2) + "↳ ";

    // ---- Edit an action ----
    private void EditSelectedPoint()
    {
        int i = SelectedIndex();
        if (i < 0) { lblStatus.Text = "Select an action first, then Edit."; return; }

        using var dlg = new ActionEditorForm(points[i], $"Edit action #{i + 1}", points);
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

    // ---- Recording via low-level mouse + keyboard hooks ----
    private void ToggleRecording()
    {
        if (recordingController.IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (runController.IsRunning) { lblStatus.Text = "Stop clicking before recording."; return; }
        // Profile hotkeys stop a recording when pressed (WndProc), so their keydowns must
        // not survive into the recorded sequence as stray Key actions.
        recordingController.AdditionalFilteredVks.Clear();
        foreach (Profile p in profileController.Profiles)
            if (p.HotkeyVk != 0) recordingController.AdditionalFilteredVks.Add(p.HotkeyVk);

        if (recordingController.Start(Handle, hotkeyVk, ReportStatus, MarshalToUi))
        {
            recordStartCount = points.Count;
            chkSequence.Checked = true;
            btnRecord.Text = "■ Stop recording";
            lblStatus.Text = "RECORDING: 0 actions — clicks and keys still reach their apps. Right-click or F8 to finish.";
        }
    }

    private void StopRecording()
    {
        long elapsed = recordingController.ElapsedMs;
        recordingController.Stop();
        btnRecord.Text = "● Record clicks";
        int recorded = points.Count - recordStartCount;
        lblStatus.Text = $"Recording finished. {recorded} action{(recorded == 1 ? "" : "s")} in {FormatDuration(elapsed)}.";
    }

    /// <summary>Appends (or, on a double-click chain, replaces) a freshly recorded action.</summary>
    private void AddRecordedAction(SeqAction a, bool replacesLast)
    {
        if (replacesLast && points.Count > 0) points[^1] = a;
        else points.Add(a);
        RefreshList();
        if (!chkSequence.Checked) chkSequence.Checked = true;
        lblStatus.Text = $"RECORDING: {points.Count - recordStartCount} actions — clicks and keys still reach their apps. Right-click or F8 to finish.";
    }

    /// <summary>0..999 ms → "N ms", < 60 s → "N.N s", otherwise "N m N s".</summary>
    private static string FormatDuration(long ms)
    {
        if (ms < 1000) return string.Create(CultureInfo.InvariantCulture, $"{ms} ms");
        if (ms < 60000) return string.Create(CultureInfo.InvariantCulture, $"{ms / 1000.0:0.#} s");
        long totalSeconds = ms / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 60} m {totalSeconds % 60} s");
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
            hotkeyManager.RegisterMain(hotkeyVk, hotkeyName);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hotkeyManager.RegisterMain(hotkeyVk, hotkeyName);
        // Registers any profile hotkeys restored from settings. Deferred to here (rather
        // than called directly from RestoreProfiles during the constructor) for the same
        // reason RegisterMain is: RegisterHotKey needs a real HWND, and this is the
        // first point one is guaranteed to exist.
        hotkeyManager.ProfilesEnabled = chkUseProfiles is { Checked: true };
        hotkeyManager.RegisterProfiles(profileController.Profiles, hotkeyVk);
    }

    // ---- Profiles ----

    // Restores profiles.json and whichever profile (if any) was active last session,
    // without letting the combo box / checkbox events fire mid-restore against state
    // that isn't fully loaded yet. Hotkey registration is deliberately left to
    // OnHandleCreated — see the comment there.
    private void RestoreProfiles()
    {
        profileController.Load(settings);
        if (profileController.LoadFailed)
        {
            lblStatus.Text = "Could not read profiles.json — it has been kept as profiles.json.corrupt\r\n"
                           + "and profile saving is disabled this session so it can't be overwritten.";
        }

        suppressProfileEvents = true;
        try
        {
            cmbProfile.Items.Clear();
            foreach (Profile p in profileController.Profiles) cmbProfile.Items.Add(p.Name);

            chkUseProfiles.Checked = settings.UseProfiles;

            if (profileController.Profiles.Count > 0 && profileController.ActiveProfileIndex >= 0)
                cmbProfile.SelectedIndex = profileController.ActiveProfileIndex;
        }
        finally { suppressProfileEvents = false; }

        // Only load the profile's actions over `points` when profiles are actually in use —
        // otherwise RestoreLastSequence's ad-hoc sequence (already loaded above) must stand.
        if (settings.UseProfiles && profileController.ActiveProfileIndex >= 0)
        {
            Profile p = profileController.Profiles[profileController.ActiveProfileIndex];
            points = p.Actions.Select(a => a.Clone()).ToList();
            RefreshList();
            chkSequence.Checked = points.Count > 0;
        }

        UpdateProfileHotkeyCombo();
        UpdateProfileControlsEnabled();
    }

    // Repopulates the combo box from the profile list, optionally re-selecting a profile by
    // name (its index may have shifted). Suppressed so this never re-triggers the switch logic.
    private void RefreshProfileCombo(string? selectName)
    {
        suppressProfileEvents = true;
        try
        {
            cmbProfile.Items.Clear();
            foreach (Profile p in profileController.Profiles) cmbProfile.Items.Add(p.Name);
            if (selectName != null)
            {
                int idx = profileController.IndexByName(selectName);
                if (idx >= 0) cmbProfile.SelectedIndex = idx;
            }
        }
        finally { suppressProfileEvents = false; }
    }

    // Applies a profile operation's outcome to the controls. `NewPoints` replaces the
    // on-screen sequence, `SelectProfileName` re-selects after a combo repopulation, and
    // the flags drive hotkey re-registration / ad-hoc-sequence reloads.
    private void ApplyProfileResult(ProfileOpResult result)
    {
        if (result.NewPoints is not null)
        {
            points = result.NewPoints;
            RefreshList();
            chkSequence.Checked = points.Count > 0;
        }
        if (result.SelectProfileName is not null)
            RefreshProfileCombo(result.SelectProfileName);
        settings.ActiveProfileName = profileController.ActiveProfileName ?? "";
        if (result.Status is not null) lblStatus.Text = result.Status;
        if (result.RegisterHotkeys)
        {
            hotkeyManager.ProfilesEnabled = true;
            hotkeyManager.RegisterProfiles(profileController.Profiles, hotkeyVk);
        }
        if (result.UnregisterHotkeys)
        {
            hotkeyManager.ProfilesEnabled = false;
            hotkeyManager.UnregisterProfiles();
        }
        if (result.ReloadAdHocSequence) RestoreLastSequence();
        UpdateProfileHotkeyCombo();
        UpdateProfileControlsEnabled();
    }

    private void CmbProfile_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressProfileEvents) return;
        SwitchToProfile(cmbProfile.SelectedIndex);
    }

    private void SwitchToProfile(int newIndex)
    {
        ApplyProfileResult(profileController.SwitchTo(newIndex, points));
    }

    private void NewProfile()
    {
        string? name = PromptForName("New profile", "Profile name:", "");
        if (name == null) return;
        ApplyProfileResult(profileController.NewProfile(name, points));
    }

    private void RenameProfile()
    {
        if (profileController.ActiveProfileIndex < 0 || profileController.ActiveProfileIndex >= profileController.Profiles.Count) return;
        string? name = PromptForName("Rename profile", "Profile name:", profileController.Profiles[profileController.ActiveProfileIndex].Name);
        if (name == null) return;
        ApplyProfileResult(profileController.RenameProfile(name));
    }

    private void DuplicateProfile()
    {
        if (profileController.ActiveProfileIndex < 0 || profileController.ActiveProfileIndex >= profileController.Profiles.Count) return;
        ApplyProfileResult(profileController.DuplicateProfile(points));
    }

    private void DeleteProfile()
    {
        int index = profileController.ActiveProfileIndex;
        if (index < 0 || index >= profileController.Profiles.Count) return;
        Profile p = profileController.Profiles[index];

        DialogResult result = MessageBox.Show(this,
            $"Delete profile \"{p.Name}\"? This permanently removes its saved sequence.",
            "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;

        ApplyProfileResult(profileController.DeleteProfile(index, points));
        if (profileController.Profiles.Count == 0)
        {
            points.Clear();
            RefreshList();
        }
    }

    private void SaveToProfile()
    {
        int index = profileController.ActiveProfileIndex;
        if (index < 0 || index >= profileController.Profiles.Count)
        {
            lblStatus.Text = "Select a profile first.";
            return;
        }
        profileController.SaveCurrentPointsToProfile(index, points);
        lblStatus.Text = $"Saved {points.Count} actions to \"{profileController.Profiles[index].Name}\".";
    }

    private void UpdateProfileHotkeyCombo()
    {
        suppressProfileEvents = true;
        try
        {
            if (profileController.ActiveProfileIndex < 0 || profileController.ActiveProfileIndex >= profileController.Profiles.Count)
            {
                cmbProfileHotkey.SelectedIndex = 0; // "None"
                return;
            }
            uint vk = profileController.Profiles[profileController.ActiveProfileIndex].HotkeyVk;
            cmbProfileHotkey.SelectedIndex = vk == 0 ? 0 : (int)(vk - 0x70 + 1);
        }
        finally { suppressProfileEvents = false; }
    }

    private void CmbProfileHotkey_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (suppressProfileEvents) return;
        int index = profileController.ActiveProfileIndex;
        if (index < 0 || index >= profileController.Profiles.Count) return;

        int sel = cmbProfileHotkey.SelectedIndex; // 0 = None, 1..12 = F1..F12
        uint vk = sel <= 0 ? 0u : (uint)(0x70 + (sel - 1));

        var result = profileController.SetHotkey(index, vk, hotkeyVk);
        if (result.RegisterHotkeys)
        {
            hotkeyManager.RegisterProfiles(profileController.Profiles, hotkeyVk);
        }
        else
        {
            UpdateProfileHotkeyCombo(); // revert to the profile's actual (unchanged) hotkey
        }
        if (result.Status is not null) lblStatus.Text = result.Status;
    }

    private void OnUseProfilesChanged()
    {
        settings.UseProfiles = chkUseProfiles.Checked;
        ApplyProfileResult(profileController.SetEnabled(chkUseProfiles.Checked, points));
    }

    private void UpdateProfileControlsEnabled()
    {
        bool on = chkUseProfiles.Checked;
        bool hasSelection = on && profileController.ActiveProfileIndex >= 0 && profileController.ActiveProfileIndex < profileController.Profiles.Count;
        cmbProfile.Enabled = on && profileController.Profiles.Count > 0;
        btnProfileNew.Enabled = on;
        btnProfileRename.Enabled = hasSelection;
        btnProfileDuplicate.Enabled = hasSelection;
        btnProfileDelete.Enabled = hasSelection;
        btnProfileSave.Enabled = hasSelection;
        cmbProfileHotkey.Enabled = hasSelection;
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
        if (index < 0 || index >= profileController.Profiles.Count) return;

        // Refuse the whole switch while a run owns the engine. Switching first and letting
        // StartClicking() then early-return on `busy` left the list showing a DIFFERENT
        // profile than the one executing — nothing started, nothing stopped, no explanation.
        if (runController.IsBusy || runController.IsRunning)
        {
            lblStatus.Text = $"Stop the current run before switching to \"{profileController.Profiles[index].Name}\".";
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

        runController.Stop();
        if (recordingController.IsRecording) StopRecording();

        pickTimer?.Stop();
        pickTimer?.Dispose();
        pickTimer = null;
        pickDone = null;

        // Must run before Join below: if a start-delay countdown is pending, `worker` holds
        // a Thread that was built but never started, and Thread.Join throws on one of those.
        runController.CancelPendingStartDelay();

        // Wait for the worker to unwind. The worker is a background thread, so without this
        // the process can exit mid-"hold" and leave the mouse button physically stuck down.
        // It polls `running` every <= 20 ms, so this returns almost immediately.
        runController.JoinWorker(2000);

        hotkeyManager.UnregisterAll();

        // Visible = false first, then Dispose: without this a ghost icon lingers in the
        // tray until the user happens to hover over its old location.
        trayIcon.Visible = false;
        trayIcon.Dispose();

        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && hotkeyManager.TryHandle(m.WParam.ToInt32()))
            return;
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
        if (recordingController.IsRecording) return;

        bool seq = chkSequence.Checked && points.Count > 0;
        bool limited = rbRepeatN.Checked;
        int limit = (int)numRepeat.Value;
        int jitterPx = (int)numJitterPx.Value;
        int jitterPct = (int)numJitterPct.Value;
        int delaySeconds = (int)numStartDelay.Value;

        List<SeqAction> acts = new();
        bool bg = false;
        bool hold = false, dbl = false, useFixedPos = false;
        int px = 0, py = 0, interval = 0, button = 0;
        string runDescription;

        if (seq)
        {
            bg = chkBackground.Checked;
            acts = points.Select(p => p.Clone()).ToList();
            runDescription = $"Running {acts.Count} actions{(bg ? " (background)" : "")}... press {hotkeyName} to stop.";
        }
        else
        {
            hold = cmbType.SelectedIndex == 2;
            dbl = cmbType.SelectedIndex == 1;
            useFixedPos = rbPick.Checked;
            px = (int)numX.Value;
            py = (int)numY.Value;
            interval = IntervalMs();
            button = cmbButton.SelectedIndex;
            runDescription = hold ? $"Holding {cmbButton.Text} button... press {hotkeyName} to stop."
                                  : $"Clicking... press {hotkeyName} to stop.";
        }

        var spec = new RunSpec
        {
            UseSequence = seq,
            Actions = acts,
            Background = bg,
            Limited = limited,
            Limit = limit,
            JitterPx = jitterPx,
            JitterPct = jitterPct,
            StartDelaySeconds = delaySeconds,
            Hold = hold,
            DoubleClick = dbl,
            UseFixedPos = useFixedPos,
            X = px,
            Y = py,
            IntervalMs = interval,
            Button = button,
            HotkeyName = hotkeyName,
            PanicEnabled = chkPanic.Checked,
            RunDescription = runDescription,
        };

        runController.TryStart(spec);
    }

    private void StopClicking()
    {
        runController.Stop();
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
