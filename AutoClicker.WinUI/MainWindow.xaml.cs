using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using static AutoClicker.Native;

namespace AutoClicker.WinUI;

/// <summary>
/// The WinUI 3 twin of Form1: the full main-window layout plus (M2b1) the interactive
/// core path — record, run/stop/pause/step, list mutations, save/load, the pick countdown
/// and global-hotkey delivery. The editor / find &amp; replace / schedule dialogs and the
/// profile CRUD + hotkey rebind dialogs are still tagged <c>M2b2</c> / <c>M3</c> stubs.
/// </summary>
/// <remarks>
/// Row order mirrors <c>Form1.BuildUi</c>: toolbar, then the sections (interval, options +
/// repeat, cursor + humanize, sequence, profiles, run options, Start/Stop, hotkey), then the
/// status strip. Three WinForms mechanisms have WinUI-native replacements:
/// <list type="bullet">
/// <item>the ▼/► collapse glyphs become <see cref="Expander"/>s, reusing the Core
/// <see cref="CollapsedSections"/> keys so settings.json round-trips unchanged;</item>
/// <item>the details <c>ListView</c> becomes a <c>ListView</c> with a Grid <c>ItemTemplate</c>
/// (WinUI has no column headers — a static header Grid shares the exact same widths);</item>
/// <item>the AutoSize form becomes <c>AppWindow.Resize</c> from the measured content, since
/// WinUI windows never size themselves.</item>
/// </list>
/// </remarks>
public sealed partial class MainWindow : Window, IDisposable
{
    /// <summary>
    /// One rendered sequence row. Public (and reflection-bindable) because the list's
    /// <c>ItemTemplate</c> binds to it with classic <c>{Binding}</c>: WinUI's x:Bind would
    /// need an <c>x:DataType</c>, which cannot name a nested type.
    /// </summary>
    /// <param name="Index">1-based row number, as shown in the "#" column.</param>
    /// <param name="Action">Describe() output, prefixed with the ControlFlow depth guide.</param>
    /// <param name="Target">DescribeTarget() output.</param>
    /// <param name="Delay">DelayMs, raw.</param>
    /// <param name="Comment">Free text.</param>
    /// <param name="Tint">Semantic row background (transparent when untinted / dark theme).</param>
    public sealed record SeqRow(int Index, string Action, string Target, string Delay, string Comment, Brush Tint);

    // Physical size the window is given before the measured resize below: it only has to be
    // in the right ballpark, because the content is measured with an unbounded height and
    // rebuilt at the real size in the Loaded pass.
    private const int ProvisionalWidthPhysical = 1200;
    private const int ProvisionalHeightPhysical = 800;

    // Logical (DIP) sizing. The WinForms original was AutoSize=True, so it measured itself
    // and froze that as its minimum; MinWidth 900 mirrors its MinimumSize. The spec's
    // "~1100x1000" and "MinWidth 900" are DIPs — at 150% a 900 DIP floor is 1350 physical
    // pixels, so the "physical 1100-1250" note further up can only have been a slip.
    private const double DefaultWidthLogical = 1100;
    private const double MinWindowWidthLogical = 900;
    private const double MaxWidthLogical = 1400;
    private const double MinHeightLogical = 700;
    private const double MaxHeightLogical = 1000;

    private readonly AppSettings _settings;
    private readonly List<SeqAction> _points = new();
    private List<Profile> _profiles = new();

    // Every Expander that persists a CollapsedSections key, with that key. The two
    // side-by-side pairs share one key, so collapsing either half collapses both — which is
    // exactly what the single WinForms glyph owned by that key used to do.
    private readonly List<(Expander Expander, string Key)> _sectionExpanders = new();

    private readonly Brush _noTint;
    private readonly Brush _flowTint;
    private readonly Brush _visualTint;
    private readonly Brush _waitTint;
    private readonly Brush _breakpointTint;

    private bool _applyingCollapsedState;
    private bool _autoSized;

    // ---- M2b1: collaborators ----
    // Built in the constructor exactly like Form1's: the window owns the controls, the
    // controllers own the engine and report through events.
    private readonly IntPtr _hwnd;
    private readonly HotkeyManager _hotkeyManager;
    private readonly RecordingController _recordingController;
    private readonly RunController _runController;

    // Hotkey / panic-key state, mirrored from settings on load and re-captured on close.
    // Form1 keeps the same three fields per key, because the rebind dialogs (M2b2) mutate
    // them before anything is written back to settings.
    private uint _hotkeyVk;
    private uint _hotkeyModifiers;
    private string _hotkeyName;
    private uint _panicKeyVk;
    private string _panicKeyName;

    // Run-row highlighting (Form1's RunningHighlight / SkippedHighlight), plus the Record
    // button's two caption tints. The idle red comes from the XAML so there is one source
    // of truth for it; the grey is Form1's Color.DarkGray.
    private readonly Brush _runningHighlight = new SolidColorBrush(Color.FromArgb(255, 255, 230, 160));
    private readonly Brush _skippedHighlight = new SolidColorBrush(Color.FromArgb(255, 210, 224, 236));
    private readonly Brush _recordRed;
    private readonly Brush _recordGray = new SolidColorBrush(Color.FromArgb(255, 169, 169, 169));
    private int _highlightIndex = -1;
    private bool _highlightSkipped;

    // Recording bookkeeping: how many actions existed when the recording started, so the
    // live and finished status lines report only what THIS session recorded.
    private int _recordStartCount;

    // Run timer (Form1's runTimer + runTimerStopwatch) and the 0-based original index of the
    // step about to run, for the "Running N/M" state label.
    private readonly Stopwatch _runTimerStopwatch = new();
    private readonly DispatcherQueueTimer _runTimer;
    private int _runStepIndex = -1;

    // Pick-location countdown (Form1's pickTimer / pickCountdown / pickDone).
    private DispatcherQueueTimer? _pickTimer;
    private int _pickCountdown;
    private Action? _pickDone;

    // WinUI allows exactly ONE ContentDialog at a time: a second concurrent ShowAsync does not
    // throw a catchable managed exception, it faults inside Microsoft.UI.Xaml.dll (verified:
    // Application Error/1000, faulting module Microsoft.UI.Xaml.dll). A real user cannot reach
    // that state — the dialog is modal and blocks the rest of the window — but a hotkey fired
    // while a dialog is open can, so every dialog in this window is gated on this flag.
    private bool _dialogOpen;

    // WndProc subclass state: the framework's original proc and the delegate the OS calls.
    // The delegate is a field because a function pointer the OS holds is invisible to the GC.
    private IntPtr _oldWndProc;
    private WndProcDelegate? _wndProc;

    public MainWindow()
    {
        InitializeComponent();

        // Brushes are UI-thread objects, so they are built here rather than in a static
        // initialiser. The pastels are the same ones Form1.RowTint uses, as WinUI colours:
        // LightCoral, LightGoldenrodYellow, Honeydew, Azure.
        _noTint = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        _breakpointTint = new SolidColorBrush(Color.FromArgb(255, 240, 128, 128));
        _flowTint = new SolidColorBrush(Color.FromArgb(255, 250, 250, 210));
        _visualTint = new SolidColorBrush(Color.FromArgb(255, 240, 255, 240));
        _waitTint = new SolidColorBrush(Color.FromArgb(255, 240, 255, 255));
        _recordRed = RecordButton.Foreground; // "#D13438" from the XAML

        _settings = AppSettings.Load();

        // ---- M2b1: the interactive path's collaborators ----
        // The HWND already exists (WinUI creates the window in the base constructor), so the
        // lazy providers the controllers take can resolve it from here on.
        _hwnd = WindowNative.GetWindowHandle(this);

        _hotkeyVk = _settings.HotkeyVk;
        _hotkeyModifiers = _settings.HotkeyModifiers;
        _hotkeyName = _settings.HotkeyName;
        _panicKeyVk = _settings.PanicKeyVk;
        _panicKeyName = _settings.PanicKeyName;

        _hotkeyManager = new HotkeyManager(() => _hwnd, SetStatus);
        _recordingController = new RecordingController();
        // Form1's exact construction, including the two lambdas that arm/disarm the panic
        // key per run against the live checkbox and the current panic VK.
        _runController = new RunController(
            () => _hotkeyManager.RegisterPanic(PanicKeyCheck.IsChecked == true, _panicKeyVk),
            () => _hotkeyManager.UnregisterPanic(),
            () => _hotkeyName,
            () => _panicKeyName,
            MarshalToUi,
            () => _hotkeyManager.RegisterPause(),
            () => _hotkeyManager.UnregisterPause());
        WireControllerEvents();

        _runTimer = DispatcherQueue.CreateTimer();
        _runTimer.Interval = TimeSpan.FromSeconds(1);
        _runTimer.IsRepeating = true;
        _runTimer.Tick += (_, _) => UpdateRunTimerTick();

        // Physical pixels: give the first measure pass a realistic width instead of WinUI's
        // default, then let the Loaded pass below replace it with the content's size.
        AppWindow.Resize(new SizeInt32(ProvisionalWidthPhysical, ProvisionalHeightPhysical));

        WireSectionExpanders();
        WireDependentEnablement();
        ApplyDisplayState();

        // The global hotkeys need a real HWND — Form1 defers the same calls to
        // OnHandleCreated. The WndProc subclass is installed FIRST: without it the WM_HOTKEY
        // the OS posts would go straight to the framework's proc and be lost.
        InstallHotkeyHook();
        _hotkeyManager.RegisterMain(_hotkeyVk, _hotkeyModifiers, _hotkeyName);
        _hotkeyManager.ProfilesEnabled = UseProfilesCheck.IsChecked == true;
        _hotkeyManager.RegisterProfiles(_profiles, _hotkeyVk, _hotkeyModifiers);

        Closed += OnWindowClosed;

        // Window itself has no Loaded event; the root element does.
        Root.Loaded += OnRootLoaded;
    }

    /// <summary>
    /// Form1.WireEvents, one subscriber at a time. Two deliberate WinUI differences: the
    /// run callbacks that arrive on the WORKER thread are marshalled here — WinForms let
    /// Form1 touch controls from that thread only because it disables its cross-thread check
    /// outside the debugger, whereas WinUI throws — and the profile hotkey handler stays an
    /// M2b2 stub because there is no ProfileController yet.
    /// </summary>
    private void WireControllerEvents()
    {
        _hotkeyManager.TogglePressed += () =>
        {
            if (_recordingController.IsRecording) { StopRecording(); return; }
            if (_runController.IsRunning) _runController.Stop(); else StartClicking();
        };
        _hotkeyManager.EndRecordingPressed += () => { if (_recordingController.IsRecording) StopRecording(); };
        _hotkeyManager.PanicPressed += () => _runController.PanicStop();
        _hotkeyManager.PausePressed += TogglePause;
        _hotkeyManager.ProfileHotkeyPressed += _ =>
        {
            // M2b2: switch to the profile and start it (ProfileController.SwitchTo + StartClicking).
        };

        _recordingController.ActionRecorded += AddRecordedAction;
        _recordingController.EndRecordingRequested += StopRecording;

        _runController.StatusChanged += SetStatus;
        _runController.RunningChanged += running => MarshalToUi(() => UpdateRunButtonsState(running));
        _runController.PauseChanged += paused => MarshalToUi(() => UpdatePauseUi(paused));
        _runController.StepStarting += OnRunnerStepStarting;
        _runController.StepCompleted += OnRunnerStep;
    }

    /// <summary>
    /// Form1.MarshalToUi on a DispatcherQueue instead of BeginInvoke: run this on the UI
    /// thread, and swallow the "queue is gone, we are shutting down" case exactly like the
    /// old ObjectDisposedException / InvalidOperationException catch did.
    /// </summary>
    private void MarshalToUi(Action action)
    {
        try { DispatcherQueue.TryEnqueue(() => action()); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { }
    }

    /// <summary>
    /// Re-applies the dependent-control greying on every control change Form1 listened to
    /// (chkSequence, cmbType, rbRepeatN, rbCurrent, rbPick). M2a wired the function but no
    /// triggers, so nothing ever followed a user edit.
    /// </summary>
    private void WireDependentEnablement()
    {
        UseSequenceCheck.Checked += OnDependentToggleChanged;
        UseSequenceCheck.Unchecked += OnDependentToggleChanged;
        ClickTypeCombo.SelectionChanged += OnDependentSelectionChanged;
        RepeatNRadio.Checked += OnDependentToggleChanged;
        CurrentLocationRadio.Checked += OnDependentToggleChanged;
        PickRadio.Checked += OnDependentToggleChanged;
    }

    private void OnDependentToggleChanged(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void OnDependentSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEnabledState();

    /// <summary>
    /// One-shot startup: the WinForms original sized itself to its content (AutoSize +
    /// GrowAndShrink) and only then froze that as the minimum. WinUI never sizes a window,
    /// so it is measured here — after the first layout pass, when <c>DesiredSize</c> is real.
    /// </summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        AutoSizeToContent();
    }

    private void AutoSizeToContent()
    {
        // Only ever runs once, at startup, before the user could have resized anything.
        if (_autoSized) return;
        _autoSized = true;

        try
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;

            // The ScrollViewer measures its child with an infinite height, so the stack's
            // DesiredSize is the content's natural size (with all expanders expanded).
            Root.UpdateLayout();
            double naturalWidth = SectionStack.DesiredSize.Width;
            double naturalHeight = SectionStack.DesiredSize.Height;

            double viewportHeight = WorkAreaHeight() / scale - 60;
            // Never narrower than the measured content, never narrower than the WinForms
            // MinimumSize equivalent, and never wider than a sane ceiling (a pathological
            // row would otherwise produce a window wider than the screen).
            double clientWidthLogical = Math.Clamp(
                Math.Max(naturalWidth, DefaultWidthLogical), MinWindowWidthLogical, MaxWidthLogical);
            double clientHeightLogical = Math.Clamp(
                naturalHeight, MinHeightLogical, Math.Min(MaxHeightLogical, viewportHeight));

            int frameWidth = (int)Math.Round(2 * 8 * scale);       // resize borders
            int frameHeight = (int)Math.Round((32 + 16) * scale);  // title bar + borders
            int targetWidth = (int)Math.Round(clientWidthLogical * scale) + frameWidth;
            int targetHeight = (int)Math.Round(clientHeightLogical * scale) + frameHeight;

            AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // Closest WinUI equivalent of Form1's MinimumSize. If this property turns
                // out to be in DIPs rather than physical pixels, the OS would bounce the
                // window back up over the size just chosen — so re-assert and relax the
                // floor in that case.
                presenter.PreferredMinimumWidth = (int)Math.Round(MinWindowWidthLogical * scale);
                presenter.PreferredMinimumHeight = (int)Math.Round(MinHeightLogical * scale);
                if (AppWindow.Size.Width > targetWidth && presenter.PreferredMinimumWidth is int floorWidth)
                    presenter.PreferredMinimumWidth = Math.Min(floorWidth, targetWidth);
                if (AppWindow.Size.Height > targetHeight && presenter.PreferredMinimumHeight is int floorHeight)
                    presenter.PreferredMinimumHeight = Math.Min(floorHeight, targetHeight);
                if (AppWindow.Size.Width > targetWidth || AppWindow.Size.Height > targetHeight)
                    AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            // Sizing is cosmetic — never take the window down over it.
        }
    }

    private int WorkAreaHeight()
    {
        DisplayArea? area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        return area?.WorkArea.Height ?? (int)MaxHeightLogical;
    }

    // ===== Display wiring (mirrors Form1.ApplySettings) =====

    private void ApplyDisplayState()
    {
        // Form1 gets this from BuildStatusStrip's initial label; set it first so the restore
        // messages below can overwrite it exactly like they do in WinForms.
        SetStatus("Ready. Press " + _settings.HotkeyName + " to start/stop.");

        PopulateSettings();
        RestoreLastSequence();
        RestoreProfiles();
        ApplyCollapsedSections();
        RefreshList();
        UpdateEnabledState();
    }

    private void PopulateSettings()
    {
        HoursBox.Value = _settings.IntervalHours;
        MinutesBox.Value = _settings.IntervalMinutes;
        SecondsBox.Value = _settings.IntervalSeconds;
        MillisecondsBox.Value = _settings.IntervalMilliseconds;
        MouseButtonCombo.SelectedIndex = _settings.MouseButton;
        ClickTypeCombo.SelectedIndex = _settings.ClickType;
        RepeatNRadio.IsChecked = _settings.RepeatLimited;
        RepeatUntilRadio.IsChecked = !_settings.RepeatLimited;
        RepeatCountBox.Value = _settings.RepeatCount;
        PickRadio.IsChecked = _settings.UsePickedPosition;
        CurrentLocationRadio.IsChecked = !_settings.UsePickedPosition;
        XBox.Value = _settings.PickedX;
        YBox.Value = _settings.PickedY;
        UseSequenceCheck.IsChecked = _settings.UseSequence;
        BackgroundModeCheck.IsChecked = _settings.BackgroundMode;
        AnchorPointsCheck.IsChecked = _settings.AnchorNewPoints;
        JitterPixelsBox.Value = _settings.JitterPixels;
        JitterPercentBox.Value = _settings.JitterPercent;
        StartDelayBox.Value = _settings.StartDelaySeconds;
        PanicKeyCheck.IsChecked = _settings.PanicKeyEnabled;
        PanicKeyButton.Content = "Panic key: " + _settings.PanicKeyName;
        MaxRunSecondsBox.Value = Math.Clamp(
            _settings.MaxRunSeconds, (int)MaxRunSecondsBox.Minimum, (int)MaxRunSecondsBox.Maximum);
        MaxActionsBox.Value = Math.Clamp(
            _settings.MaxActions, (int)MaxActionsBox.Minimum, (int)MaxActionsBox.Maximum);
        CornerFailSafeCheck.IsChecked = _settings.CornerFailSafe;
        StopOnMouseMoveCheck.IsChecked = _settings.StopOnUserMouseMove;
        RunLoggingCheck.IsChecked = _settings.RunLoggingEnabled;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        UseProfilesCheck.IsChecked = _settings.UseProfiles;
        AutoSwitchCheck.IsChecked = _settings.ProfileAutoSwitch;
        ColorModeCombo.SelectedIndex = _settings.ColorMode;
        SpeedBox.Value = _settings.SpeedPercent;
        RestoreCursorCheck.IsChecked = _settings.RestoreCursorAfterRun;

        // Captions follow the persisted hotkey, exactly like Form1's Start/Stop labels —
        // a hardcoded "F6" would contradict the status line above.
        StartButton.Content = "Start (" + _settings.HotkeyName + ")";
        StopRunButton.Content = "Stop (" + _settings.HotkeyName + ")";
    }

    /// <summary>
    /// Restores the sequence that was open last time. No dialog and no user-visible failure
    /// beyond the status line: a missing or corrupt file just means "start empty".
    /// </summary>
    private void RestoreLastSequence()
    {
        string path = _settings.LastSequencePath;
        if (string.IsNullOrEmpty(path)) return;

        if (!File.Exists(path))
        {
            _settings.LastSequencePath = "";
            return;
        }

        try
        {
            var loaded = SequenceFile.Deserialize(File.ReadAllText(path));
            _points.Clear();
            _points.AddRange(loaded);
            UseSequenceCheck.IsChecked = _points.Count > 0;
            SetStatus($"Restored {_points.Count} actions from {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or SequenceFile.UnsupportedVersionException)
        {
            _points.Clear();
            _settings.LastSequencePath = "";
            SetStatus("Could not restore the last sequence \u2014 starting empty.");
        }
    }

    /// <summary>
    /// Display-only profile restore: populates the combo and the active profile's own fields.
    /// No profile controller, no hotkey registration, no auto-switch timer.
    /// </summary>
    private void RestoreProfiles()
    {
        _profiles = ProfileStore.Load(out bool loadFailed);
        if (loadFailed)
        {
            SetStatus("Could not read profiles.json \u2014 it has been kept as profiles.json.corrupt "
                    + "and profile saving is disabled this session so it can't be overwritten.");
        }

        foreach (Profile profile in _profiles) ProfileCombo.Items.Add(profile.Name);

        ProfileHotkeyCombo.Items.Add("None");
        for (int i = 1; i <= 12; i++) ProfileHotkeyCombo.Items.Add("F" + i.ToString(CultureInfo.InvariantCulture));
        ProfileHotkeyCombo.SelectedIndex = 0;

        if (_profiles.Count == 0) return;

        ProfileCombo.SelectedIndex = 0;
        int active = IndexOfProfile(_settings.ActiveProfileName);
        if (active >= 0) ProfileCombo.SelectedIndex = active;

        Profile selected = _profiles[ProfileCombo.SelectedIndex];
        TargetProcessBox.Text = selected.TargetProcess;
        // Item 0 is "None"; item N is "FN". HotkeyVk 0 means no hotkey at all.
        int hotkeyIndex = selected.HotkeyVk == 0 ? 0 : (int)(selected.HotkeyVk - 0x70) + 1;
        ProfileHotkeyCombo.SelectedIndex = hotkeyIndex is >= 0 and <= 12 ? hotkeyIndex : 0;

        // Same rule as Form1.RestoreProfiles: only take over the list when profiles are
        // actually in use, otherwise the ad-hoc sequence restored above must stand.
        if (_settings.UseProfiles)
        {
            _points.Clear();
            foreach (SeqAction action in selected.Actions) _points.Add(action.Clone());
            UseSequenceCheck.IsChecked = _points.Count > 0;
        }
    }

    private int IndexOfProfile(string name)
    {
        for (int i = 0; i < _profiles.Count; i++)
            if (string.Equals(_profiles[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Renders the sequence list, the summary counts and the empty state.</summary>
    private void RefreshList()
    {
        // Depth is recomputed on every refresh, so the indent guides always reflect the
        // current block structure (same as Form1.RefreshList).
        int[] depth = ControlFlow.BuildDepth(_points);
        long waitTotalMs = 0;
        var rows = new List<SeqRow>(_points.Count);
        for (int i = 0; i < _points.Count; i++)
        {
            SeqAction action = _points[i];
            waitTotalMs += action.DelayMs;
            // The active/skipped run row is painted over its semantic tint, so the tinting
            // survives run highlighting (Form1.Highlight repainted every row for the same
            // reason). WinUI has no per-item BackColor, so it is baked into the row model.
            Brush tint = i == _highlightIndex
                ? (_highlightSkipped ? _skippedHighlight : _runningHighlight)
                : RowTint(action.Kind);
            rows.Add(new SeqRow(
                i + 1,
                IndentGuide(depth[i]) + action.Describe(),
                action.DescribeTarget(),
                action.DelayMs.ToString(CultureInfo.InvariantCulture),
                action.Comment,
                tint));
        }

        SequenceList.ItemsSource = rows;
        ActionCountText.Text = $"{_points.Count} action{(_points.Count == 1 ? "" : "s")}";
        WaitTotalText.Text = $"waits \u03A3 {FormatWaitTotal(waitTotalMs)}";
        // Form1 also hides the empty state while recording starts: the first recorded action
        // is what makes the list non-empty, and until then the overlay would cover it.
        EmptyOverlay.Visibility = _points.Count == 0 && !_recordingController.IsRecording
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ListView sub-items cannot be indented, so the Action column gets text guides instead
    // (identical to Form1.IndentGuide, including the ↳ U+21B3 marker).
    private static string IndentGuide(int depth) => depth <= 0 ? "" : new string(' ', depth * 2) + "\u21B3 ";

    /// <summary>Compact wait-total formatting: 1200 ms → "1.2s" (same as Form1).</summary>
    private static string FormatWaitTotal(long ms) =>
        ms < 1000 ? $"{ms} ms"
        : ms < 60000 ? string.Create(CultureInfo.InvariantCulture, $"{ms / 1000.0:0.#}s")
        : string.Create(CultureInfo.InvariantCulture, $"{ms / 60000}m {ms % 60000 / 1000}s");

    /// <summary>
    /// The sequence list's semantic row tints: flow kinds (loops/conditionals/jumps) yellow,
    /// visual kinds green, waits blue, breakpoints coral (checked first so a breakpoint
    /// stands out from the flow tint). Dark theme skips tinting entirely, for the same reason
    /// Form1.RowBackColor does — light pastels under light text are unreadable.
    /// </summary>
    private Brush RowTint(ActionKind kind)
    {
        if (Root.ActualTheme == ElementTheme.Dark) return _noTint;

        if (kind == ActionKind.Breakpoint) return _breakpointTint;
        if (SeqAction.IsControlKind(kind)) return _flowTint;
        if (kind is ActionKind.FindImage or ActionKind.FindText) return _visualTint;
        if (kind is ActionKind.Wait or ActionKind.WaitPixel) return _waitTint;
        return _noTint; // "no tint": pixel-gated and everything else stay list-coloured
    }

    /// <summary>
    /// Mirrors Form1.UpdateEnabled: the dependent-control greying that settings drive, plus
    /// the run-state buttons, which now follow the RunController instead of a hardcoded idle
    /// state.
    /// </summary>
    private void UpdateEnabledState()
    {
        bool sequence = UseSequenceCheck.IsChecked == true;
        bool hold = ClickTypeCombo.SelectedIndex == 2;

        RepeatNRadio.IsEnabled = !hold;
        RepeatUntilRadio.IsEnabled = !hold;
        RepeatCountBox.IsEnabled = (!hold || sequence) && RepeatNRadio.IsChecked == true;

        CurrentLocationRadio.IsEnabled = !sequence;
        PickRadio.IsEnabled = !sequence;
        PickLocationButton.IsEnabled = !sequence;
        XBox.IsEnabled = !sequence && PickRadio.IsChecked == true;
        YBox.IsEnabled = !sequence && PickRadio.IsChecked == true;

        ApplyRunControlEnablement();
    }

    /// <summary>
    /// The running / paused / step enablement, shared by UpdateRunButtonsState (a run
    /// transition) and UpdateEnabledState (a checkbox change) so the two can never disagree.
    /// </summary>
    private void ApplyRunControlEnablement()
    {
        bool running = _runController.IsRunning;
        bool paused = running && _runController.IsPaused;

        PlayButton.IsEnabled = !running;
        StartButton.IsEnabled = !running;
        StopToolbarButton.IsEnabled = running;
        StopRunButton.IsEnabled = running;
        PauseButton.IsEnabled = running;
        PauseButton.Label = paused ? "Resume" : "Pause";
        if (PauseButton.Icon is FontIcon glyph) glyph.Glyph = paused ? "\uE768" : "\uE769";
        StepButton.IsEnabled = paused;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    // ===== Collapse state (the one part of the port that is fully wired) =====

    private void WireSectionExpanders()
    {
        _sectionExpanders.Add((IntervalExpander, "interval"));
        // One key per ROW in the WinForms original: its single glyph collapsed both halves.
        _sectionExpanders.Add((OptionsExpander, "repeat"));
        _sectionExpanders.Add((RepeatExpander, "repeat"));
        _sectionExpanders.Add((CursorExpander, "cursor"));
        _sectionExpanders.Add((HumanizeExpander, "cursor"));
        _sectionExpanders.Add((ProfilesExpander, "profiles"));
        _sectionExpanders.Add((RunOptionsExpander, "runoptions"));

        foreach ((Expander expander, _) in _sectionExpanders)
            expander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, OnSectionExpandedChanged);
    }

    /// <summary>Applies settings.CollapsedSections to the expanders, after the XAML has built them.</summary>
    private void ApplyCollapsedSections()
    {
        HashSet<string> collapsed = CollapsedSections.Parse(_settings.CollapsedSections);
        _applyingCollapsedState = true;
        try
        {
            foreach ((Expander expander, string key) in _sectionExpanders)
                expander.IsExpanded = !collapsed.Contains(key);
        }
        finally
        {
            _applyingCollapsedState = false;
        }
    }

    /// <summary>
    /// Persists a user's collapse/expand back to <c>settings.json</c>. Expanders that share a
    /// key are kept in lockstep, so the two halves of a row can never disagree.
    /// </summary>
    private void OnSectionExpandedChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_applyingCollapsedState) return;

        var source = (Expander)sender;
        string key = KeyFor(source);

        _applyingCollapsedState = true;
        try
        {
            foreach ((Expander expander, string candidate) in _sectionExpanders)
                if (candidate == key && !ReferenceEquals(expander, source))
                    expander.IsExpanded = source.IsExpanded;
        }
        finally
        {
            _applyingCollapsedState = false;
        }

        _settings.CollapsedSections = CollapsedSections.Serialize(
            _sectionExpanders.Where(pair => !pair.Expander.IsExpanded).Select(pair => pair.Key));
        _settings.Save();
    }

    private string KeyFor(Expander expander)
    {
        foreach ((Expander candidate, string key) in _sectionExpanders)
            if (ReferenceEquals(candidate, expander)) return key;
        return "";
    }

    /// <summary>
    /// Sequence is the core of the app and stays expanded: the WinForms original never
    /// registered a collapse glyph for it and never persisted a key. Re-expanding here is the
    /// simplest way to keep that guarantee with a native Expander.
    /// </summary>
    private void OnSequenceExpanderCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (ReferenceEquals(sender, SequenceExpander)) sender.IsExpanded = true;
    }

    // =====================================================================
    // M2b1: the interactive core path. Semantics are ported from Form1
    // method-for-method; the deviations (ContentDialog vs MessageBox,
    // DispatcherQueue vs BeginInvoke, DispatcherQueueTimer vs WinForms timer,
    // pickers vs the Win32 common dialogs) are called out inline.
    // =====================================================================

    // ---- Click engine (Form1's "Click engine" region) ----

    /// <summary>
    /// A NumberBox's value as an int. Form1's NumericUpDown could never be empty; a WinUI
    /// NumberBox reports NaN when the user clears it, so that reads as its minimum. Same
    /// clamp the settings restore uses.
    /// </summary>
    private static int ValueOf(NumberBox box)
    {
        double value = box.Value;
        if (double.IsNaN(value)) value = box.Minimum;
        return (int)Math.Clamp(value, box.Minimum, box.Maximum);
    }

    /// <summary>The visible mouse-button name ("Left"/"Right"/"Middle"), for the run description.</summary>
    private string MouseButtonName() =>
        MouseButtonCombo.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? "" : "";

    /// <summary>Form1.IntervalMs: hours/minutes/seconds/milliseconds summed, capped at int.MaxValue.</summary>
    private int IntervalMs()
    {
        long ms = (long)ValueOf(HoursBox) * 3600000
                + (long)ValueOf(MinutesBox) * 60000
                + (long)ValueOf(SecondsBox) * 1000
                + ValueOf(MillisecondsBox);
        return (int)Math.Min(ms, int.MaxValue);
    }

    private void StartClicking()
    {
        if (_recordingController.IsRecording) return;
        _runController.TryStart(BuildRunSpec(0, -1));
    }

    // Right-click "Run from here": the selected action through the end of the list.
    private void RunFromHere()
    {
        if (_recordingController.IsRecording) return;
        int i = SelectedIndex();
        if (i < 0) { SetStatus("Select an action to run from."); return; }
        _runController.TryStart(BuildRunSpec(i, -1));
    }

    // Right-click "Run selection": the first through last of the selected rows, inclusive.
    private void RunSelection()
    {
        if (_recordingController.IsRecording) return;
        var selected = SelectedIndices();
        if (selected.Count == 0) { SetStatus("Select the actions to run."); return; }
        _runController.TryStart(BuildRunSpec(selected[0], selected[^1]));
    }

    /// <summary>
    /// Builds the <see cref="RunSpec"/> for a start — Form1.BuildRunSpec field for field. The
    /// normal Start button uses the full range (0, -1); the list context menu passes a bounded
    /// range, which forces sequence mode regardless of the "Use sequence" checkbox.
    /// </summary>
    private RunSpec BuildRunSpec(int startIndex, int endIndex)
    {
        bool forceSeq = startIndex > 0 || endIndex >= 0;
        bool seq = forceSeq || (UseSequenceCheck.IsChecked == true && _points.Count > 0);
        bool limited = RepeatNRadio.IsChecked == true;
        int limit = ValueOf(RepeatCountBox);
        int jitterPx = ValueOf(JitterPixelsBox);
        int jitterPct = ValueOf(JitterPercentBox);
        int delaySeconds = ValueOf(StartDelayBox);

        List<SeqAction> acts = new();
        bool bg = false;
        bool hold = false, dbl = false, useFixedPos = false;
        int px = 0, py = 0, interval = 0, button = 0;
        string runDescription;

        if (seq)
        {
            bg = BackgroundModeCheck.IsChecked == true;
            acts = _points.Select(p => p.Clone()).ToList();
            if (forceSeq)
            {
                // Bounds are 1-based for the status line, matching the list's "#" column.
                int first = Math.Max(0, startIndex);
                int last = endIndex < 0 ? acts.Count - 1 : Math.Min(endIndex, acts.Count - 1);
                runDescription = $"Running actions {first + 1}-{last + 1}... press {_hotkeyName} to stop.";
            }
            else
            {
                runDescription = $"Running {acts.Count} actions{(bg ? " (background)" : "")}... press {_hotkeyName} to stop.";
            }
        }
        else
        {
            hold = ClickTypeCombo.SelectedIndex == 2;
            dbl = ClickTypeCombo.SelectedIndex == 1;
            useFixedPos = PickRadio.IsChecked == true;
            px = ValueOf(XBox);
            py = ValueOf(YBox);
            interval = IntervalMs();
            button = MouseButtonCombo.SelectedIndex;
            runDescription = hold ? $"Holding {MouseButtonName()} button... press {_hotkeyName} to stop."
                                  : $"Clicking... press {_hotkeyName} to stop.";
        }

        return new RunSpec
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
            HotkeyName = _hotkeyName,
            PanicEnabled = PanicKeyCheck.IsChecked == true,
            RunDescription = runDescription,
            CornerFailSafe = CornerFailSafeCheck.IsChecked == true,
            MaxRunSeconds = ValueOf(MaxRunSecondsBox),
            MaxActions = ValueOf(MaxActionsBox),
            StopOnUserMouseMove = StopOnMouseMoveCheck.IsChecked == true,
            RunLoggingEnabled = RunLoggingCheck.IsChecked == true,
            SpeedPercent = ValueOf(SpeedBox),
            StartIndex = startIndex,
            EndIndex = endIndex,
            RestoreCursorAfterRun = RestoreCursorCheck.IsChecked == true,
        };
    }

    private void StopClicking() => _runController.Stop();

    private void TogglePause()
    {
        if (!_runController.IsRunning) return;
        if (_runController.IsPaused) _runController.Resume();
        else _runController.Pause();
    }

    /// <summary>
    /// Form1.SetRunningButtonsState: toolbar + Start/Stop enablement, the run timer and the
    /// state label, all in one place so the pairs cannot drift apart. The tray menu items the
    /// WinForms original also updated do not exist in the WinUI shell.
    /// </summary>
    private void UpdateRunButtonsState(bool running)
    {
        ApplyRunControlEnablement();
        _runStepIndex = -1;
        UpdateStateLabel();

        if (running)
        {
            _runTimerStopwatch.Restart();
            RunTimerText.Text = "00:00";
            _runTimer.Start();
        }
        else
        {
            _runTimer.Stop();
            RunTimerText.Text = "";
        }
    }

    /// <summary>
    /// Form1.UpdatePauseUi: the caption/enablement swap on a pause transition. Also arrives on
    /// a breakpoint, which the RunController raises from the worker thread — hence the marshal
    /// in <see cref="WireControllerEvents"/>.
    /// </summary>
    private void UpdatePauseUi(bool paused)
    {
        ApplyRunControlEnablement();
        UpdateStateLabel();

        if (paused)
        {
            // Freeze the displayed run timer: the tick already checks IsPaused, and stopping
            // the stopwatch here means the elapsed time excludes the paused stretch.
            _runTimer.Stop();
            _runTimerStopwatch.Stop();
            SetStatus(_runStepIndex >= 0
                ? $"Paused at step {_runStepIndex + 1}/{_points.Count} — press Step to advance"
                : "Paused — press Step to advance");
        }
        else if (_runController.IsRunning)
        {
            _runTimerStopwatch.Start();
            _runTimer.Start();
        }
    }

    private void UpdateRunTimerTick()
    {
        // While paused the displayed timer is frozen (and the stopwatch is stopped), so the
        // elapsed time excludes paused stretches.
        if (_runController.IsPaused) return;
        TimeSpan elapsed = _runTimerStopwatch.Elapsed;
        RunTimerText.Text = string.Create(CultureInfo.InvariantCulture, $"{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    /// <summary>Form1's compact "Ready / Recording / Running N/M" state label.</summary>
    private void UpdateStateLabel()
    {
        if (_recordingController.IsRecording) StateText.Text = "Recording";
        else if (_runController.IsRunning)
        {
            string running = _runController.IsPaused ? "Paused" : "Running";
            StateText.Text = _runStepIndex >= 0 ? $"{running} {_runStepIndex + 1}/{_points.Count}" : running;
        }
        else StateText.Text = "Ready";
    }

    /// <summary>
    /// Form1.OnRunnerStepStarting: fires just BEFORE the step — including one that then blocks
    /// for a while (a WaitPixel can sit here for its whole timeout) — and before the pause
    /// gate, so a paused run re-reports the row it is about to take. Index is the ORIGINAL
    /// list position, so "Running N/M" matches the list.
    /// </summary>
    private void OnRunnerStepStarting(int index)
    {
        _runStepIndex = index;
        MarshalToUi(() =>
        {
            UpdateStateLabel();
            if (_runController.IsPaused)
                SetStatus($"Paused at step {index + 1}/{_points.Count} — press Step to advance");
        });
        Highlight(index);
    }

    /// <summary>
    /// Form1.OnRunnerStep: index -1 means the run ended (clear everything); a false
    /// <c>performed</c> means a gate suppressed the step, so that row goes grey-blue.
    /// </summary>
    private void OnRunnerStep(int index, bool performed)
    {
        if (index < 0) { Highlight(-1); return; }
        if (!performed) Highlight(index, skipped: true);
    }

    /// <summary>
    /// Form1.Highlight, which reset every row to its semantic tint and then repainted the
    /// active one. WinUI has no per-item BackColor, so the index is remembered and RefreshList
    /// bakes the highlight into the rebuilt rows — one code path owns every tint.
    /// </summary>
    private void Highlight(int index, bool skipped = false)
    {
        MarshalToUi(() =>
        {
            _highlightIndex = index;
            _highlightSkipped = skipped;
            RefreshList();
            if (index >= 0 && index < SequenceList.Items.Count)
                SequenceList.ScrollIntoView(SequenceList.Items[index]);
        });
    }

    // ---- Sequence list selection helpers (Form1 read lvPoints.SelectedIndices) ----

    /// <summary>0-based index of the first selected row, or -1 when nothing is selected.</summary>
    private int SelectedIndex() =>
        SequenceList.SelectedItems.Count > 0 ? ((SeqRow)SequenceList.SelectedItems[0]).Index - 1 : -1;

    /// <summary>0-based indices of every selected row, ascending.</summary>
    private List<int> SelectedIndices() =>
        SequenceList.SelectedItems.Cast<SeqRow>().Select(row => row.Index - 1).OrderBy(i => i).ToList();

    // ---- Sequence editing (Form1's list mutations) ----

    private static POINT CursorPos() { GetCursorPos(out POINT p); return p; }

    private void AddPoint(POINT p)
    {
        var a = new SeqAction
        {
            Kind = ActionKind.Click,
            X = p.X,
            Y = p.Y,
            Button = MouseButtonCombo.SelectedIndex,
            DoubleClick = ClickTypeCombo.SelectedIndex == 1,
            DelayMs = IntervalMs()
        };

        if (AnchorPointsCheck.IsChecked == true &&
            WindowAnchor.Capture(p, out string cls, out string title, out POINT clientPt))
        {
            a.WindowRelative = true;
            a.WindowClass = cls;
            a.WindowTitle = title;
            a.X = clientPt.X;
            a.Y = clientPt.Y;
        }

        _points.Add(a);
        RefreshList();
        if (UseSequenceCheck.IsChecked != true) UseSequenceCheck.IsChecked = true;
        SetStatus($"Added action #{_points.Count}: {a.Describe()} at {a.DescribeTarget()}");
    }

    /// <summary>Appends (or, on a double-click chain, replaces) a freshly recorded action.</summary>
    private void AddRecordedAction(SeqAction a, bool replacesLast)
    {
        // Record-time selector enrichment: a Click also snapshots what UI Automation sees at
        // its point, so later (opt-in) playback can self-heal if the point drifts. Cheap and
        // best-effort — a miss just leaves the selector fields empty.
        if (a.Kind == ActionKind.Click)
        {
            UiaInvoker.TryDescribeAt(a.X, a.Y, out string? automationId, out string? name, out string? className);
            a.SelAutomationId = automationId ?? "";
            a.SelName = name ?? "";
            a.SelClass = className ?? "";
        }

        if (replacesLast && _points.Count > 0) _points[^1] = a;
        else _points.Add(a);
        RefreshList();
        if (UseSequenceCheck.IsChecked != true) UseSequenceCheck.IsChecked = true;
        SetStatus($"RECORDING: {_points.Count - _recordStartCount} actions — clicks and keys still reach their apps. Right-click or F8 to finish.");
    }

    private void RemoveSelectedPoint()
    {
        var indices = SelectedIndices();
        if (indices.Count == 0) return;
        // Remove every selected row, highest index first so each RemoveAt stays valid.
        foreach (int i in indices.OrderByDescending(i => i)) _points.RemoveAt(i);
        RefreshList();
    }

    /// <summary>
    /// Form1.ClearAllPoints. The confirmation is a ContentDialog instead of a
    /// Yes/No MessageBox with "No" as the default, so DefaultButton is the close button.
    /// </summary>
    private async Task ClearAllPointsAsync()
    {
        if (_points.Count == 0) return;
        if (_dialogOpen) return;
        var dialog = new ContentDialog
        {
            Title = "Clear sequence",
            Content = "Clear the whole sequence? This removes every action from the list.",
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        _dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (result != ContentDialogResult.Primary) return;
        _points.Clear();
        RefreshList();
        SetStatus("Sequence cleared.");
    }

    private void MovePoint(int dir)
    {
        // Move acts on a single selection: with several rows selected the direction would be
        // ambiguous, so require exactly one and ignore multi-select.
        if (SequenceList.SelectedItems.Count != 1) return;
        int i = SelectedIndex();
        int j = i + dir;
        if (i < 0 || j < 0 || j >= _points.Count) return;
        (_points[i], _points[j]) = (_points[j], _points[i]);
        RefreshList();
        SequenceList.SelectedIndex = j;
    }

    /// <summary>
    /// Inserts a breakpoint action at the selected row (or the end when nothing is selected).
    /// Form1 gates this from the context menu while a run owns the engine; the WinUI
    /// equivalent is <see cref="OnRowMenuOpening"/>.
    /// </summary>
    private void InsertBreakpoint()
    {
        int i = SelectedIndex();
        int insertAt = i >= 0 ? i : _points.Count;
        _points.Insert(insertAt, new SeqAction { Kind = ActionKind.Breakpoint });
        RefreshList();
        SequenceList.SelectedIndex = insertAt;
        SetStatus($"Inserted breakpoint at #{insertAt + 1}. It pauses the run when reached.");
    }

    // ---- Save / load sequence (JSON) ----

    /// <summary>
    /// Form1.SaveSequence, through the WinRT pickers: an unpackaged WinUI app must hand the
    /// picker its owner HWND or the call fails with a class-not-registered COM error. As in
    /// WinForms, LastSequencePath is remembered in memory and written by the close-time save.
    /// </summary>
    private async Task SaveSequenceAsync()
    {
        if (_points.Count == 0) { SetStatus("Nothing to save — sequence is empty."); return; }
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "sequence" };
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeChoices.Add("Auto Clicker sequence (*.acseq)", new List<string> { ".acseq" });
            picker.FileTypeChoices.Add("JSON (*.json)", new List<string> { ".json" });

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await FileIO.WriteTextAsync(file, SequenceFile.Serialize(_points));
            _settings.LastSequencePath = file.Path;
            SetStatus($"Saved {_points.Count} actions to {Path.GetFileName(file.Path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException or COMException)
        {
            // A picker failure is reportable, not fatal: the sequence is still in the list.
            SetStatus("Save failed: " + ex.Message);
        }
    }

    private async Task LoadSequenceAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".acseq");
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add("*");

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var loaded = SequenceFile.Deserialize(await FileIO.ReadTextAsync(file));
            _points.Clear();
            _points.AddRange(loaded);
            RefreshList();
            UseSequenceCheck.IsChecked = _points.Count > 0;
            _settings.LastSequencePath = file.Path;
            SetStatus($"Loaded {_points.Count} actions from {Path.GetFileName(file.Path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or SequenceFile.UnsupportedVersionException
                                      or ArgumentException or COMException)
        {
            SetStatus("Load failed: " + ex.Message);
        }
    }

    // ---- Pick location (single-point): countdown then capture cursor ----

    private void PickLocation()
    {
        PickRadio.IsChecked = true;
        StartPick(() =>
        {
            GetCursorPos(out POINT p);
            XBox.Value = Math.Clamp(p.X, XBox.Minimum, XBox.Maximum);
            YBox.Value = Math.Clamp(p.Y, YBox.Minimum, YBox.Maximum);
            SetStatus($"Captured X={p.X} Y={p.Y}.");
            UpdateEnabledState();
        });
    }

    /// <summary>
    /// Form1.StartPick: a 3-second countdown in the status line, then the callback. The
    /// WinForms timer becomes a DispatcherQueueTimer; the repeat semantics are identical.
    /// </summary>
    private void StartPick(Action done)
    {
        _pickDone = done;
        _pickCountdown = 3;
        _pickTimer?.Stop();
        _pickTimer = DispatcherQueue.CreateTimer();
        _pickTimer.Interval = TimeSpan.FromSeconds(1);
        _pickTimer.IsRepeating = true;
        SetStatus($"Move mouse to target... capturing in {_pickCountdown}s");
        _pickTimer.Tick += (sender, _) =>
        {
            _pickCountdown--;
            if (_pickCountdown > 0) { SetStatus($"Move mouse to target... capturing in {_pickCountdown}s"); return; }
            sender.Stop();
            _pickTimer = null;
            Action? callback = _pickDone;
            _pickDone = null;
            callback?.Invoke();
        };
        _pickTimer.Start();
    }

    /// <summary>0..999 ms → "N ms", &lt; 60 s → "N.N s", otherwise "N m N s" (Form1.FormatDuration).</summary>
    private static string FormatDuration(long ms)
    {
        if (ms < 1000) return string.Create(CultureInfo.InvariantCulture, $"{ms} ms");
        if (ms < 60000) return string.Create(CultureInfo.InvariantCulture, $"{ms / 1000.0:0.#} s");
        long totalSeconds = ms / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 60} m {totalSeconds % 60} s");
    }

    // ---- Recording via the low-level mouse + keyboard hooks ----

    private void ToggleRecording()
    {
        if (_recordingController.IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (_runController.IsRunning) { SetStatus("Stop clicking before recording."); return; }
        // Profile hotkeys stop a recording when pressed, so their keydowns must not survive
        // into the recorded sequence as stray Key actions. M2b1 has no ProfileController yet,
        // so the list comes from the profiles.json snapshot this window already loaded.
        _recordingController.AdditionalFilteredVks.Clear();
        foreach (Profile p in _profiles)
            if (p.HotkeyVk != 0) _recordingController.AdditionalFilteredVks.Add(p.HotkeyVk);

        if (_recordingController.Start(_hwnd, _hotkeyVk, SetStatus, MarshalToUi))
        {
            _recordStartCount = _points.Count;
            UseSequenceCheck.IsChecked = true;
            RecordClicksButton.Content = "■ Stop recording";
            RecordButton.Label = "Stop rec";
            RecordButton.Foreground = _recordGray;
            UpdateStateLabel();
            SetStatus("RECORDING: 0 actions — clicks and keys still reach their apps. Right-click or F8 to finish.");
        }
    }

    private void StopRecording()
    {
        long elapsed = _recordingController.ElapsedMs;
        // Stop() flushes any coalesced typed text still pending, so a word typed right before
        // F8 / right-click becomes its Text action instead of being lost (it lands through
        // ActionRecorded → AddRecordedAction, which is why the caption swap follows it).
        _recordingController.Stop();
        RecordClicksButton.Content = "● Record clicks";
        RecordButton.Label = "Record";
        RecordButton.Foreground = _recordRed;
        UpdateStateLabel();
        int recorded = _points.Count - _recordStartCount;
        SetStatus($"Recording finished. {recorded} action{(recorded == 1 ? "" : "s")} in {FormatDuration(elapsed)}.");
    }

    // ---- Hotkey delivery: the WM_HOTKEY hook + rebind/pick close-out ----

    // WinUI 3 has no overridable WndProc and no message-only window of our own, so
    // RegisterHotKey's WM_HOTKEY is captured the raw way: swap the top-level HWND's proc for
    // ours and chain to the framework's original for every other message. The delegate must
    // stay rooted (the OS holds a raw function pointer, not a reference).
    private const int GWLP_WNDPROC = -4;

    /// <summary>The window-procedure signature the subclass swaps in.</summary>
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Named x64 entry points: this app ships win-x64 only (Platforms=x64 +
    // RuntimeIdentifier=win-x64), so the Ptr variants are always the correct ones.
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void InstallHotkeyHook()
    {
        _wndProc = HotkeyWndProc;
        IntPtr previous = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
        if (previous == IntPtr.Zero)
        {
            // Without the hook a pressed hotkey reaches the framework's proc and vanishes, so
            // say so rather than leaving a silently dead Start/Stop key.
            _wndProc = null;
            SetStatus("Could not subclass the window procedure — global hotkeys are unavailable.");
            return;
        }
        _oldWndProc = previous;
    }

    private IntPtr HotkeyWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // WM_HOTKEY (0x0312). The HotkeyManager owns every id, so an id it does not recognise
        // falls through to the framework untouched.
        if (msg == WM_HOTKEY && _hotkeyManager.TryHandle(wParam.ToInt32()))
            return IntPtr.Zero;
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void UninstallHotkeyHook()
    {
        if (_oldWndProc == IntPtr.Zero) return;
        _ = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
        _oldWndProc = IntPtr.Zero;
        _wndProc = null;
    }

    /// <summary>
    /// Form1's lvMenu.Opening: "Run from here"/"Run selection" need a selection, and inserting
    /// a breakpoint is refused while a run owns the engine.
    /// </summary>
    private void OnRowMenuOpening(object? sender, object e)
    {
        bool any = SequenceList.SelectedItems.Count > 0;
        RunFromHereItem.IsEnabled = any;
        RunSelectionItem.IsEnabled = any;
        InsertBreakpointItem.IsEnabled = !_runController.IsRunning;
    }

    // ---- Settings capture + shutdown (Form1.OnFormClosing) ----

    /// <summary>
    /// Form1.CaptureSettingsFromControls. The profile block is M2b2 (there is no
    /// ProfileController yet), so UseProfiles is captured but no profile is written back.
    /// </summary>
    private void CaptureSettingsFromControls()
    {
        _settings.IntervalHours = ValueOf(HoursBox);
        _settings.IntervalMinutes = ValueOf(MinutesBox);
        _settings.IntervalSeconds = ValueOf(SecondsBox);
        _settings.IntervalMilliseconds = ValueOf(MillisecondsBox);
        _settings.MouseButton = MouseButtonCombo.SelectedIndex;
        _settings.ClickType = ClickTypeCombo.SelectedIndex;
        _settings.RepeatLimited = RepeatNRadio.IsChecked == true;
        _settings.RepeatCount = ValueOf(RepeatCountBox);
        _settings.UsePickedPosition = PickRadio.IsChecked == true;
        _settings.PickedX = ValueOf(XBox);
        _settings.PickedY = ValueOf(YBox);
        _settings.UseSequence = UseSequenceCheck.IsChecked == true;
        _settings.BackgroundMode = BackgroundModeCheck.IsChecked == true;
        _settings.AnchorNewPoints = AnchorPointsCheck.IsChecked == true;
        _settings.JitterPixels = ValueOf(JitterPixelsBox);
        _settings.JitterPercent = ValueOf(JitterPercentBox);
        _settings.HotkeyVk = _hotkeyVk;
        _settings.HotkeyModifiers = _hotkeyModifiers;
        _settings.HotkeyName = _hotkeyName;
        _settings.StartDelaySeconds = ValueOf(StartDelayBox);
        _settings.PanicKeyEnabled = PanicKeyCheck.IsChecked == true;
        _settings.PanicKeyVk = _panicKeyVk;
        _settings.PanicKeyName = _panicKeyName;
        _settings.CornerFailSafe = CornerFailSafeCheck.IsChecked == true;
        _settings.MaxRunSeconds = ValueOf(MaxRunSecondsBox);
        _settings.MaxActions = ValueOf(MaxActionsBox);
        _settings.StopOnUserMouseMove = StopOnMouseMoveCheck.IsChecked == true;
        _settings.RunLoggingEnabled = RunLoggingCheck.IsChecked == true;
        _settings.SpeedPercent = ValueOf(SpeedBox);
        _settings.RestoreCursorAfterRun = RestoreCursorCheck.IsChecked == true;
        _settings.ColorMode = ColorModeCombo.SelectedIndex;
        _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        _settings.UseProfiles = UseProfilesCheck.IsChecked == true;
        _settings.ProfileAutoSwitch = AutoSwitchCheck.IsChecked == true;
        _settings.CollapsedSections = CollapsedSections.Serialize(
            _sectionExpanders.Where(pair => !pair.Expander.IsExpanded).Select(pair => pair.Key));
    }

    /// <summary>
    /// Form1.OnFormClosing, in order. A window with no tray icon and no owned modeless dialogs
    /// has less to tear down than the WinForms original, but the ordering that matters is
    /// identical: CancelPendingStartDelay BEFORE JoinWorker (a thread that was built but never
    /// started throws on Join), and UnregisterAll after the engine has stopped.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // A failed save must not block closing, and there is nowhere useful left to report it.
        CaptureSettingsFromControls();
        _settings.Save();

        _runController.Stop();
        if (_recordingController.IsRecording) StopRecording();

        _pickTimer?.Stop();
        _pickTimer = null;
        _pickDone = null;

        _runTimer.Stop();

        // Must run before Join below: if a start-delay countdown is pending, `worker` holds a
        // Thread that was built but never started, and Thread.Join throws on one of those.
        _runController.CancelPendingStartDelay();

        // Wait for the worker to unwind. The worker is a background thread, so without this the
        // process can exit mid-"hold" and leave the mouse button physically stuck down.
        _runController.JoinWorker(2000);

        _hotkeyManager.UnregisterAll();
        UninstallHotkeyHook();
        Dispose();
    }

    /// <summary>
    /// Releases the owned controllers. <see cref="RunController.Dispose"/> is idempotent, so
    /// calling this from <see cref="OnWindowClosed"/> and again from an explicit disposal is
    /// safe. (WinUI's Window is not disposable, which is why this is here rather than inherited
    /// from a Component base the way Form1's is.)
    /// </summary>
    public void Dispose()
    {
        _runController.Dispose();
        GC.SuppressFinalize(this);
    }

    // ===== Handlers: the M2b1 surface =====

    private void OnRecordClick(object sender, RoutedEventArgs e) => ToggleRecording();

    private void OnPlayClick(object sender, RoutedEventArgs e) => StartClicking();

    private void OnStopClick(object sender, RoutedEventArgs e) => StopClicking();

    private void OnPauseClick(object sender, RoutedEventArgs e) => TogglePause();

    private void OnStepClick(object sender, RoutedEventArgs e) => _runController.StepOnce();

    private void OnStartClick(object sender, RoutedEventArgs e) => StartClicking();

    private void OnAddActionClick(object sender, RoutedEventArgs e)
    {
        // M3: open the action editor seeded with the kind in ((MenuFlyoutItem)sender).Tag
        // (Click/Drag/Scroll/Key/Text/Wait/WaitPixel/FindImage/FindText/Repeat/IfElse/Else/
        // SetVar/Label/GotoLabel/Break/Breakpoint).
    }

    private void OnAddActionMenuClick(object sender, RoutedEventArgs e)
    {
        // M3: same editor, but asking for the kind first.
    }

    private void OnAddCursorPosClick(object sender, RoutedEventArgs e) => AddPoint(CursorPos());

    private void OnEditRowClick(object sender, RoutedEventArgs e)
    {
        // M3: edit the selected row (ActionEditorForm → ContentDialog).
    }

    private void OnRemoveRowClick(object sender, RoutedEventArgs e) => RemoveSelectedPoint();

    private async void OnClearRowsClick(object sender, RoutedEventArgs e) => await ClearAllPointsAsync();

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => MovePoint(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => MovePoint(1);

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveSequenceAsync();

    private async void OnLoadClick(object sender, RoutedEventArgs e) => await LoadSequenceAsync();

    private void OnPickLocationClick(object sender, RoutedEventArgs e) => PickLocation();

    private void OnRunFromHereClick(object sender, RoutedEventArgs e) => RunFromHere();

    private void OnRunSelectionClick(object sender, RoutedEventArgs e) => RunSelection();

    private void OnInsertBreakpointClick(object sender, RoutedEventArgs e) => InsertBreakpoint();

    private void OnFindClick(object sender, RoutedEventArgs e)
    {
        // M3: the Find & Replace dialog.
    }

    private void OnRebindPanicKeyClick(object sender, RoutedEventArgs e)
    {
        // M2b2: capture Esc / F1-F12 and re-register the panic hotkey.
    }

    private void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
        // M2b2: capture the start/stop hotkey and re-register it.
    }

    private void OnScheduleClick(object sender, RoutedEventArgs e)
    {
        // M3: the schedule editor.
    }

    private void OnProfileNewClick(object sender, RoutedEventArgs e)
    {
        // M2b2: ProfileController.New().
    }

    private void OnProfileRenameClick(object sender, RoutedEventArgs e)
    {
        // M2b2: ProfileController.Rename().
    }

    private void OnProfileDuplicateClick(object sender, RoutedEventArgs e)
    {
        // M2b2: ProfileController.Duplicate().
    }

    private void OnProfileDeleteClick(object sender, RoutedEventArgs e)
    {
        // M2b2: ProfileController.Delete().
    }

    private void OnProfileSaveClick(object sender, RoutedEventArgs e)
    {
        // M2b2: ProfileController.SaveCurrentPointsToProfile().
    }
}
