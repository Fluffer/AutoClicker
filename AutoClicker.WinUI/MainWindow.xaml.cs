using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace AutoClicker.WinUI;

/// <summary>
/// M2a: the full main-window layout, rendered from the real persisted settings and the
/// last-opened sequence. Everything here is DISPLAY-ONLY — no controller is started, no
/// input is sent, and every button handler is an empty M2b stub.
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
public sealed partial class MainWindow : Window
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

        _settings = AppSettings.Load();

        // Physical pixels: give the first measure pass a realistic width instead of WinUI's
        // default, then let the Loaded pass below replace it with the content's size.
        AppWindow.Resize(new SizeInt32(ProvisionalWidthPhysical, ProvisionalHeightPhysical));

        WireSectionExpanders();
        ApplyDisplayState();

        // Window itself has no Loaded event; the root element does.
        Root.Loaded += OnRootLoaded;
    }

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
            rows.Add(new SeqRow(
                i + 1,
                IndentGuide(depth[i]) + action.Describe(),
                action.DescribeTarget(),
                action.DelayMs.ToString(CultureInfo.InvariantCulture),
                action.Comment,
                RowTint(action.Kind)));
        }

        SequenceList.ItemsSource = rows;
        ActionCountText.Text = $"{_points.Count} action{(_points.Count == 1 ? "" : "s")}";
        WaitTotalText.Text = $"waits \u03A3 {FormatWaitTotal(waitTotalMs)}";
        EmptyOverlay.Visibility = _points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
    /// Mirrors Form1.UpdateEnabled (the dependent-control greying that settings drive) plus
    /// the idle toolbar state. M2a only ever renders the idle state: the run / record
    /// controllers, and therefore every running-state transition, arrive in M2b.
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

        StopToolbarButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StepButton.IsEnabled = false;
        StopRunButton.IsEnabled = false;
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

    // ===== M2b stubs: layout only, no behaviour yet =====

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        // M2b: RecordingController toggle + the "Record"/"Stop recording" caption swap.
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        // M2b: RunController.Start() with the current RunSpec.
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        // M2b: RunController.Stop().
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        // M2b: RunController.TogglePause().
    }

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        // M2b: RunController.StepOnce() (only meaningful while paused).
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        // M2b: same as Play, through StartClicking().
    }

    private void OnAddActionClick(object sender, RoutedEventArgs e)
    {
        // M2b: open the action editor seeded with the kind in ((MenuFlyoutItem)sender).Tag
        // (Click/Drag/Scroll/Key/Text/Wait/WaitPixel/FindImage/FindText/Repeat/IfElse/Else/
        // SetVar/Label/GotoLabel/Break/Breakpoint).
    }

    private void OnAddActionMenuClick(object sender, RoutedEventArgs e)
    {
        // M2b: same editor, but asking for the kind first.
    }

    private void OnAddCursorPosClick(object sender, RoutedEventArgs e)
    {
        // M2b: append a Click at the live cursor position.
    }

    private void OnEditRowClick(object sender, RoutedEventArgs e)
    {
        // M2b: edit the selected row.
    }

    private void OnRemoveRowClick(object sender, RoutedEventArgs e)
    {
        // M2b: remove the selected rows.
    }

    private void OnClearRowsClick(object sender, RoutedEventArgs e)
    {
        // M2b: clear every row (with confirmation).
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        // M2b: move the selection up one row.
    }

    private void OnMoveDownClick(object sender, RoutedEventArgs e)
    {
        // M2b: move the selection down one row.
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // M2b: FileSavePicker + SequenceFile.Serialize, then settings.LastSequencePath.
    }

    private void OnLoadClick(object sender, RoutedEventArgs e)
    {
        // M2b: FileOpenPicker + SequenceFile.Deserialize into the list.
    }

    private void OnPickLocationClick(object sender, RoutedEventArgs e)
    {
        // M2b: the 3-second pick countdown.
    }

    private void OnRunFromHereClick(object sender, RoutedEventArgs e)
    {
        // M2b: run a bounded slice starting at the selected row.
    }

    private void OnRunSelectionClick(object sender, RoutedEventArgs e)
    {
        // M2b: run exactly the selected rows.
    }

    private void OnInsertBreakpointClick(object sender, RoutedEventArgs e)
    {
        // M2b: insert a Breakpoint row (at the selection, or at the end).
    }

    private void OnFindClick(object sender, RoutedEventArgs e)
    {
        // M2b: the Find & Replace dialog.
    }

    private void OnRebindPanicKeyClick(object sender, RoutedEventArgs e)
    {
        // M2b: capture Esc / F1-F12 and re-register the panic hotkey.
    }

    private void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
        // M2b: capture the start/stop hotkey and re-register it.
    }

    private void OnScheduleClick(object sender, RoutedEventArgs e)
    {
        // M2b: the schedule editor.
    }

    private void OnProfileNewClick(object sender, RoutedEventArgs e)
    {
        // M2b: ProfileController.New().
    }

    private void OnProfileRenameClick(object sender, RoutedEventArgs e)
    {
        // M2b: ProfileController.Rename().
    }

    private void OnProfileDuplicateClick(object sender, RoutedEventArgs e)
    {
        // M2b: ProfileController.Duplicate().
    }

    private void OnProfileDeleteClick(object sender, RoutedEventArgs e)
    {
        // M2b: ProfileController.Delete().
    }

    private void OnProfileSaveClick(object sender, RoutedEventArgs e)
    {
        // M2b: ProfileController.SaveCurrentPointsToProfile().
    }
}
