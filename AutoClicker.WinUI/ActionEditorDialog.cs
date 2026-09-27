using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;
using Bitmap = System.Drawing.Bitmap;
using DrawingRect = System.Drawing.Rectangle;
using static AutoClicker.Native;

namespace AutoClicker.WinUI;

/// <summary>
/// Add/edit dialog for one <see cref="SeqAction"/> — the WinUI twin of WinForms'
/// <c>ActionEditorForm</c>. Shows only the fields the selected action kind actually uses
/// (same visibility predicates, same validation messages, same capture flows).
/// </summary>
/// <remarks>
/// Dialog-result API mirrors the WinForms pattern: the constructor seeds from a
/// <see cref="SeqAction"/> clone plus a mode title ("Add action" / "Edit action #N"),
/// <see cref="Result"/> exposes the clone, and validation runs on the OK (Primary) click —
/// a failure cancels the close and reports inline, because WinUI forbids the nested
/// ContentDialog a MessageBox would need (the M2b1 hard-crash rule).
/// </remarks>
internal sealed class ActionEditorDialog
{
    private readonly SeqAction action;
    private readonly List<SeqAction>? sequence;
    private readonly ContentDialog dialog;
    private readonly DispatcherQueue dispatcherQueue;
    private readonly IntPtr ownerHwnd;

    // ---- Controls (names follow the WinForms original for easy diffing) ----
    private readonly ComboBox cmbKind = new() { Width = 170 };
    private readonly NumberBox numX = NewNum(-100000, 100000);
    private readonly NumberBox numY = NewNum(-100000, 100000);
    private readonly Button btnPick = new() { Content = "Pick (3s)…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button btnProbe = new() { Content = "What's here?", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0) };
    private readonly CheckBox chkAnchor = new() { Content = "Anchor to the window it's in", Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock lblAnchor = Hint("");
    private readonly ComboBox cmbClickMethod = new() { Width = 320 };
    private readonly TextBlock lblClickMethodHint = Hint("");
    private readonly ComboBox cmbButton = new() { Width = 110 };
    private readonly ComboBox cmbClickType = new() { Width = 110 };
    private readonly NumberBox numHold = NewNum(0, 600000);
    private readonly TextBox txtSelector = new() { Width = 320, IsReadOnly = true };
    private readonly CheckBox chkPreferSelector = new() { Content = "Self-heal: find this control by its selector if the point drifts", Margin = new Thickness(0, 4, 0, 0) };
    private readonly NumberBox numEndX = NewNum(-100000, 100000);
    private readonly NumberBox numEndY = NewNum(-100000, 100000);
    private readonly NumberBox numDragMs = NewNum(0, 600000);
    private readonly NumberBox numNotches = NewNum(-1000, 1000);
    private readonly CheckBox chkHorizontal = new() { Content = "Horizontal (shift-wheel)", Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox txtCombo = new() { Width = 220 };
    private readonly TextBlock lblComboHint = Hint("");
    private readonly TextBox txtText = new() { Width = 260, Height = 80, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly NumberBox numDelay = NewNum(0, int.MaxValue);
    private readonly NumberBox numDelayRand = NewNum(0, 100);
    private readonly TextBox txtComment = new() { Width = 260, Height = 44, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };

    // ---- Pixel condition / WaitPixel ----
    private readonly ComboBox cmbCondition = new() { Width = 300 };
    private readonly NumberBox numCondX = NewNum(-100000, 100000);
    private readonly NumberBox numCondY = NewNum(-100000, 100000);
    private readonly Button btnPickPixel = new() { Content = "Pick pixel (3s)…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Border pnlSwatch = new() { Width = 24, Height = 24, BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock lblSwatchText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly NumberBox numTolerance = NewNum(0, 255);
    private readonly TextBlock lblToleranceHint = Hint("");
    private readonly NumberBox numPixelTimeout = NewNum(0, 3600000);
    private readonly CheckBox chkAbortOnTimeout = new() { Content = "Stop the whole run if it times out", Margin = new Thickness(0, 4, 0, 0) };
    private readonly NumberBox numPollInterval = NewNum(10, 60000);

    // ---- Control flow ----
    private readonly NumberBox numRepeatCount = NewNum(0, 1_000_000);
    private readonly TextBlock lblRepeatHint = Hint("0 = loop until Break.");
    private readonly TextBox txtCondition = new() { Width = 260 };
    private readonly TextBlock lblConditionHint = Hint("e.g. counter < 5");
    private readonly TextBox txtVarName = new() { Width = 220 };
    private readonly TextBox txtValueExpr = new() { Width = 260 };
    private readonly TextBlock lblVarHint = Hint("e.g. counter = counter + 1");
    private readonly AutoSuggestBox cmbLabel = new() { Width = 240, PlaceholderText = "label name" };

    // ---- Visual targeting (FindImage / FindText) ----
    private readonly Button btnCaptureTemplate = new() { Content = "Capture template (3s)…", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button btnBrowseTemplate = new() { Content = "Browse image…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0) };
    private readonly Border picTemplateBorder = new()
    {
        Width = 96,
        Height = 96,
        BorderThickness = new Thickness(1),
        BorderBrush = ThemeStroke(),
        Background = new SolidColorBrush(Color.FromArgb(255, 240, 240, 240)),
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 4, 0, 0),
    };
    private readonly Image picTemplate = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxWidth = 94, MaxHeight = 94 };
    private readonly NumberBox numTemplateSize = NewNum(8, 256);
    private readonly NumberBox numThreshold = new() { Minimum = 0.5, Maximum = 1.0, SmallChange = 0.05, Width = 110, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox numSearchX = NewNum(-100000, 100000);
    private readonly NumberBox numSearchY = NewNum(-100000, 100000);
    private readonly NumberBox numSearchW = NewNum(0, 100000);
    private readonly NumberBox numSearchH = NewNum(0, 100000);
    private readonly TextBlock lblSearchHint = Hint("");
    private readonly CheckBox chkClickOnFound = new() { Content = "Click the found target", Margin = new Thickness(0, 4, 0, 0) };
    private readonly NumberBox numClickOffsetX = NewNum(-100000, 100000);
    private readonly NumberBox numClickOffsetY = NewNum(-100000, 100000);
    private readonly TextBox txtTextQuery = new() { Width = 260 };
    private readonly CheckBox chkRegexQuery = new() { Content = "Treat as regular expression", Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock lblVisualHint = Hint("");

    private readonly TextBlock errorText = new()
    {
        Visibility = Visibility.Collapsed,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
        Foreground = new SolidColorBrush(Color.FromArgb(255, 178, 34, 34)), // Color.Firebrick
    };

    // Rows follow the WinForms TableLayoutPanel: label column + field column, each row
    // visible only for the kinds its predicate accepts. An Auto row with every child
    // collapsed collapses to nothing — that is how the dialog shrinks to the kind.
    private readonly List<(TextBlock Label, FrameworkElement Field, Func<ActionKind, bool> VisibleFor)> rows = new();
    private readonly Grid rowsGrid = new() { ColumnSpacing = 0 };

    // Capture flows: the same 3-second countdown idiom as the WinForms timers, driven by
    // the shared PickCountdown (M2b1's status-line pick uses the same class).
    private PickCountdown? pickPosition;
    private PickCountdown? pickPixel;
    private PickCountdown? captureTemplate;
    private DispatcherQueueTimer? settleTimer;

    // The FindImage template held by the editor. Kept as PNG bytes (not a Bitmap) so it
    // round-trips through the .acseq format exactly; the preview shows a decoded copy.
    private byte[]? currentTemplate;

    // Window anchoring keeps both representations so toggling the checkbox converts
    // between them instead of throwing the coordinates away.
    private string anchorClass;
    private string anchorTitle;

    /// <summary>The edited action (a clone of the constructor's source). Valid after OK.</summary>
    public SeqAction Result => action;

    public ActionEditorDialog(
        XamlRoot xamlRoot,
        DispatcherQueue dispatcherQueue,
        IntPtr ownerHwnd,
        SeqAction source,
        string title,
        List<SeqAction>? sequence = null)
    {
        action = source.Clone();
        this.sequence = sequence;
        this.dispatcherQueue = dispatcherQueue;
        this.ownerHwnd = ownerHwnd;
        anchorClass = action.WindowClass;
        anchorTitle = action.WindowTitle;

        // ---- Kind selector ----
        string[] kinds =
        {
            "Click", "Drag", "Scroll", "Key press", "Type text", "Wait only", "Wait for pixel",
            "Repeat (loop)", "End block", "If (conditional)", "Set variable", "Break loop",
            "Goto label", "Label", "Find image", "Find text", "Breakpoint", "Else",
        };
        foreach (string k in kinds) cmbKind.Items.Add(k);
        cmbKind.SelectedIndex = (int)action.Kind;

        BuildRows();

        // ---- Seed from the clone (WinForms ctor assignments, one for one) ----
        SetNum(numX, action.X);
        SetNum(numY, action.Y);
        chkAnchor.IsChecked = action.WindowRelative;
        // Items must be added BEFORE SelectedIndex: an empty ComboBox silently ignores
        // the selection and renders as a blank dropdown (M3 QA regression).
        if (cmbClickMethod.Items.Count == 0)
        {
            cmbClickMethod.Items.Add("Real input (moves the cursor)");
            cmbClickMethod.Items.Add("Background messages (no cursor movement)");
            cmbClickMethod.Items.Add("UI Automation invoke (no cursor movement)");
        }
        if (cmbButton.Items.Count == 0)
        {
            cmbButton.Items.Add("Left");
            cmbButton.Items.Add("Right");
            cmbButton.Items.Add("Middle");
        }
        if (cmbClickType.Items.Count == 0)
        {
            cmbClickType.Items.Add("Single");
            cmbClickType.Items.Add("Double");
        }
        cmbClickMethod.SelectedIndex = Math.Clamp(action.ClickMethod, 0, 2);
        cmbButton.SelectedIndex = Math.Clamp(action.Button, 0, 2);
        cmbClickType.SelectedIndex = action.DoubleClick ? 1 : 0;
        SetNum(numHold, action.HoldMs);
        txtSelector.Text = SelectorSummary(action);
        chkPreferSelector.IsChecked = action.PreferSelector;
        SetNum(numEndX, action.EndX);
        SetNum(numEndY, action.EndY);
        SetNum(numDragMs, action.DragMs);
        SetNum(numNotches, Math.Clamp(action.ScrollNotches, -1000, 1000));
        chkHorizontal.IsChecked = action.Horizontal;
        txtCombo.Text = action.KeyCombo;
        txtText.Text = action.Text;
        SetNum(numDelay, action.DelayMs);
        SetNum(numDelayRand, action.DelayRandomPercent);
        txtComment.Text = action.Comment;

        foreach (string label in ConditionLabels(SelectedKind)) cmbCondition.Items.Add(label);
        cmbCondition.SelectedIndex = (int)action.Condition;
        SetNum(numCondX, action.CondX);
        SetNum(numCondY, action.CondY);
        pnlSwatch.Background = new SolidColorBrush(FromDrawing(PixelSampler.FromRgb(action.CondColor)));
        lblSwatchText.Text = action.DescribeColor();
        SetNum(numTolerance, action.CondTolerance);
        lblToleranceHint.Text = "0 = exact match, higher allows more drift.\n"
                              + "Picking moves the cursor aside before sampling, so the colour is the\n"
                              + "target's resting state rather than its hover highlight.";
        SetNum(numPixelTimeout, Math.Clamp(action.PixelTimeoutMs, 0, 3600000));
        chkAbortOnTimeout.IsChecked = action.AbortRunOnTimeout;
        SetNum(numPollInterval, action.PollIntervalMs);

        SetNum(numRepeatCount, Math.Clamp(action.RepeatCount, 0, 1_000_000));
        txtCondition.Text = action.ConditionExpr;
        txtVarName.Text = action.VarName;
        txtValueExpr.Text = action.ValueExpr;
        PopulateLabels();
        cmbLabel.Text = action.Label;

        // Visual targeting.
        currentTemplate = action.TemplatePng;
        SetNum(numTemplateSize, 60);
        SetNum(numThreshold, Math.Clamp(action.MatchThreshold, 0.5, 1.0));
        SetNum(numSearchX, action.SearchX);
        SetNum(numSearchY, action.SearchY);
        SetNum(numSearchW, action.SearchW);
        SetNum(numSearchH, action.SearchH);
        chkClickOnFound.IsChecked = action.ClickOnFound;
        SetNum(numClickOffsetX, action.ClickOffsetX);
        SetNum(numClickOffsetY, action.ClickOffsetY);
        txtTextQuery.Text = action.TextQuery;
        chkRegexQuery.IsChecked = action.RegexQuery;
        lblSearchHint.Text = "0×0 = search the full screen. Screen coordinates; a monitor left of the primary is negative.";
        lblVisualHint.Text = "Captures a fixed-size square centred on the cursor when the countdown ends.\n"
                           + "The template must be captured at the same display scale it is replayed at.";
        _ = UpdateTemplatePreviewAsync();

        // ---- Wire events AFTER seeding (as in the WinForms ctor) ----
        cmbKind.SelectionChanged += (_, _) =>
        {
            // The condition wording means something different for WaitPixel (it's the wait
            // itself) versus every other kind (it's a gate), so the combo text is rebuilt
            // whenever the kind crosses that line.
            RebuildConditionLabels();
            ApplyKindVisibility();
            UpdateClickMethodHint();
            ValidateCombo();
        };
        chkAnchor.Checked += (_, _) => AnchorToggled();
        chkAnchor.Unchecked += (_, _) => AnchorToggled();
        cmbClickMethod.SelectionChanged += (_, _) => UpdateClickMethodHint();
        txtCombo.TextChanged += (_, _) => ValidateCombo();
        // Hand-editing the coordinates must re-sample, or the swatch would silently
        // disagree with what CondX/CondY now point at.
        numCondX.ValueChanged += (_, _) => UpdateColorSwatch();
        numCondY.ValueChanged += (_, _) => UpdateColorSwatch();
        btnPick.Click += (_, _) => StartPick();
        btnProbe.Click += (_, _) => ProbeAt();
        btnPickPixel.Click += (_, _) => StartPickPixel();
        btnCaptureTemplate.Click += (_, _) => StartCaptureTemplate();
        btnBrowseTemplate.Click += (_, _) => BrowseTemplate();

        // ---- Dialog shell ----
        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(new ScrollViewer
        {
            Content = rowsGrid,
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        content.Children.Add(errorText);

        dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        dialog.PrimaryButtonClick += OnPrimaryClick;

        UpdateAnchorLabel();
        ValidateCombo();
        ApplyKindVisibility();
        UpdateClickMethodHint();
    }

    /// <summary>
    /// Shows the dialog once. Any capture countdown still running when the dialog closes is
    /// disposed here — the WinForms equivalent was the control's Dispose stopping its timers.
    /// </summary>
    public async Task<ContentDialogResult> ShowAsync()
    {
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            pickPosition?.Dispose();
            pickPixel?.Dispose();
            captureTemplate?.Dispose();
            settleTimer?.Stop();
        }
    }

    // ===== Row layout =====

    private static NumberBox NewNum(double min, double max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 110,
        SmallChange = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 340,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 4, 0, 4),
    };

    private static Brush ThemeStroke()
    {
        if (Application.Current is not null
            && Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out object? value)
            && value is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>NumberBox.Value as an int, with the NaN-on-clear behaviour MainWindow uses.</summary>
    private static int ValueOf(NumberBox box)
    {
        double value = box.Value;
        if (double.IsNaN(value)) value = box.Minimum;
        return (int)Math.Clamp(value, box.Minimum, box.Maximum);
    }

    private static void SetNum(NumberBox box, double value) =>
        box.Value = Math.Clamp(value, box.Minimum, box.Maximum);

    private static bool UsesPosition(ActionKind k) => k is ActionKind.Click or ActionKind.Drag or ActionKind.Scroll;

    /// <summary>
    /// Whether a kind shows the pixel-condition rows. WaitPixel uses them (it IS the wait),
    /// but FindImage/FindText do not — their own success criterion replaces the gate.
    /// </summary>
    private static bool UsesPixelCondition(ActionKind k) =>
        !SeqAction.IsControlKind(k) && k is not ActionKind.FindImage and not ActionKind.FindText;

    private ActionKind SelectedKind => (ActionKind)Math.Max(0, cmbKind.SelectedIndex);

    private void BuildRows()
    {
        rowsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var posFlow = Flow(numX, Label("Y"), numY, btnPick, btnProbe);
        var endFlow = Flow(numEndX, Label("Y"), numEndY);
        var condPosFlow = Flow(numCondX, Label("Y"), numCondY, btnPickPixel);
        var swatchFlow = Flow(pnlSwatch, lblSwatchText);
        var templateFlow = Flow(btnCaptureTemplate, btnBrowseTemplate);
        var searchPosFlow = Flow(numSearchX, Label("Y"), numSearchY);
        var searchSizeFlow = Flow(numSearchW, Label("H"), numSearchH);
        var clickOffsetFlow = Flow(numClickOffsetX, Label("Y"), numClickOffsetY);

        AddRow("Action:", cmbKind, _ => true);
        AddRow("Position  X", posFlow, UsesPosition);
        AddRow("", chkAnchor, UsesPosition);
        AddRow("", lblAnchor, k => UsesPosition(k) && chkAnchor.IsChecked == true);
        AddRow("Click method:", cmbClickMethod, UsesPosition);
        AddRow("", lblClickMethodHint, UsesPosition);
        AddRow("Mouse button:", cmbButton, k => k is ActionKind.Click or ActionKind.Drag);
        AddRow("Click type:", cmbClickType, k => k == ActionKind.Click);
        AddRow("Hold button (ms):", numHold, k => k == ActionKind.Click);
        AddRow("Recorded selector:", txtSelector, k => k == ActionKind.Click);
        AddRow("", chkPreferSelector, k => k == ActionKind.Click);
        AddRow("Drag to  X", endFlow, k => k == ActionKind.Drag);
        AddRow("Drag duration (ms):", numDragMs, k => k == ActionKind.Drag);
        AddRow("Wheel notches:", numNotches, k => k == ActionKind.Scroll);
        AddRow("", chkHorizontal, k => k == ActionKind.Scroll);
        AddRow("Key combo:", txtCombo, k => k == ActionKind.Key);
        AddRow("", lblComboHint, k => k == ActionKind.Key);
        AddRow("Text:", txtText, k => k == ActionKind.Text);

        // Pixel condition rows apply to every input kind: any action can be gated on a pixel
        // colour, and a WaitPixel action IS the wait built from these same fields. Control-flow
        // kinds ignore gates entirely, so the rows hide for them (a gate there would do nothing);
        // FindImage/FindText are their own wait and hide them too.
        AddRow("Pixel condition:", cmbCondition, UsesPixelCondition);
        AddRow("Pixel point  X", condPosFlow, UsesPixelCondition);
        AddRow("Pixel color:", swatchFlow, UsesPixelCondition);
        AddRow("Tolerance:", numTolerance, UsesPixelCondition);
        AddRow("", lblToleranceHint, UsesPixelCondition);
        AddRow("Wait timeout (ms, 0 = forever):", numPixelTimeout, SeqAction.IsWaitKind);
        AddRow("", chkAbortOnTimeout, SeqAction.IsWaitKind);
        AddRow("Check every (ms):", numPollInterval, SeqAction.IsWaitKind);

        // Control-flow rows: each new kind shows only the fields it uses.
        AddRow("Repeat count:", numRepeatCount, k => k == ActionKind.Repeat);
        AddRow("", lblRepeatHint, k => k == ActionKind.Repeat);
        AddRow("Condition:", txtCondition, k => k == ActionKind.IfElse);
        AddRow("", lblConditionHint, k => k == ActionKind.IfElse);
        AddRow("Variable name:", txtVarName, k => k == ActionKind.SetVar);
        AddRow("Value:", txtValueExpr, k => k == ActionKind.SetVar);
        AddRow("", lblVarHint, k => k == ActionKind.SetVar);
        AddRow("Label:", cmbLabel, k => k is ActionKind.GotoLabel or ActionKind.Label);

        // Visual-targeting rows.
        AddRow("Template:", templateFlow, k => k == ActionKind.FindImage);
        AddRow("Template size (px):", numTemplateSize, k => k == ActionKind.FindImage);
        AddRow("", picTemplateBorder, k => k == ActionKind.FindImage);
        AddRow("", lblVisualHint, k => k == ActionKind.FindImage);
        AddRow("Match threshold:", numThreshold, k => k == ActionKind.FindImage);
        AddRow("Find text:", txtTextQuery, k => k == ActionKind.FindText);
        AddRow("", chkRegexQuery, k => k == ActionKind.FindText);
        AddRow("Search area  X", searchPosFlow, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow("Search size  W", searchSizeFlow, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow("", lblSearchHint, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow("", chkClickOnFound, k => k is ActionKind.FindImage or ActionKind.FindText);
        AddRow("Click offset  X", clickOffsetFlow, k => k is ActionKind.FindImage or ActionKind.FindText);

        AddRow("Wait after (ms):", numDelay, _ => true);
        // Not in the WinForms editor (v2.1 added the field with no UI): the task spec asks
        // for DelayRandomPercent alongside the delay, so the ±% override is editable here.
        AddRow("Delay jitter ±% (0 = global):", numDelayRand, _ => true);
        AddRow("Comment (optional):", txtComment, _ => true);

        picTemplateBorder.Child = picTemplate;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
    };

    private static StackPanel Flow(params FrameworkElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (UIElement child in children) panel.Children.Add(child);
        return panel;
    }

    private void AddRow(string labelText, FrameworkElement field, Func<ActionKind, bool> visibleFor)
    {
        int row = rowsGrid.RowDefinitions.Count;
        rowsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 8, 5),
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        if (field is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 3, 0, 3);
        rowsGrid.Children.Add(label);
        rowsGrid.Children.Add(field);
        rows.Add((label, field, visibleFor));
    }

    private void ApplyKindVisibility()
    {
        ActionKind kind = SelectedKind;
        foreach ((TextBlock label, FrameworkElement field, Func<ActionKind, bool> visibleFor) in rows)
        {
            Visibility show = visibleFor(kind) ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = show;
            field.Visibility = show;
        }
    }

    // ===== Click method wording =====

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

        return "No cursor movement; works on WPF, UWP/WinUI and Chrome. A UIA invoke "
             + "activates the control directly, so button choice, double-click and hold time are ignored.";
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
        int x = ValueOf(numX);
        int y = ValueOf(numY);

        if (chkAnchor.IsChecked == true)
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

    // ===== Pixel condition wording =====

    /// <summary>Rebuilds the condition combo's wording for the current kind, keeping the selection.</summary>
    private void RebuildConditionLabels()
    {
        int keep = Math.Max(cmbCondition.SelectedIndex, 0);
        cmbCondition.Items.Clear();
        foreach (string label in ConditionLabels(SelectedKind)) cmbCondition.Items.Add(label);
        cmbCondition.SelectedIndex = keep;
    }

    private static string[] ConditionLabels(ActionKind kind) => kind == ActionKind.WaitPixel
        ? new[] { "(nothing — no-op)", "Wait until the pixel matches", "Wait until the pixel stops matching" }
        : new[] { "Always run this action", "Only if the pixel matches", "Only if the pixel does NOT match" };

    /// <summary>Keeps the swatch honest with CondX/CondY: called after either changes.</summary>
    private void UpdateColorSwatch()
    {
        if (PixelSampler.TrySample(ValueOf(numCondX), ValueOf(numCondY), out var sampled))
        {
            pnlSwatch.Background = new SolidColorBrush(FromDrawing(sampled));
            lblSwatchText.Text = PixelSampler.DescribeRgb(PixelSampler.ToRgb(sampled));
        }
        else
        {
            // Point is off every display: keep showing the last known colour rather than
            // guessing, and say so instead of crashing on the failed sample.
            lblSwatchText.Text = "#—— (off-screen)";
        }
    }

    private static Color FromDrawing(System.Drawing.Color c) => Color.FromArgb(255, c.R, c.G, c.B);

    // ===== Position picking (shared PickCountdown, button-caption ticks) =====

    private void StartPick()
    {
        pickPosition?.Dispose();
        btnPick.IsEnabled = false;
        pickPosition = PickCountdown.Start(
            dispatcherQueue,
            3,
            n => btnPick.Content = $"{n}…",
            () =>
            {
                pickPosition = null;
                btnPick.Content = "Pick (3s)…";
                btnPick.IsEnabled = true;
                GetCursorPos(out POINT p);
                CaptureAt(p);
            });
    }

    /// <summary>
    /// Same countdown idiom as <see cref="StartPick"/>, but captures a point AND its colour
    /// in one grab: the user hovers the thing they care about and gets both at once.
    /// </summary>
    private void StartPickPixel()
    {
        pickPixel?.Dispose();
        settleTimer?.Stop();
        btnPickPixel.IsEnabled = false;
        pickPixel = PickCountdown.Start(
            dispatcherQueue,
            3,
            n => btnPickPixel.Content = $"{n}…",
            () =>
            {
                // The coordinates come from where the user is pointing...
                GetCursorPos(out POINT p);
                SetNum(numCondX, p.X);
                SetNum(numCondY, p.Y);

                // ...but the COLOUR must not be sampled while the cursor is still sitting on
                // it. Anything that highlights on hover — buttons, links, menu items, list
                // rows — would be captured in its hover colour, which never recurs at run
                // time when the cursor is elsewhere, so the condition could never match. A
                // themed button measures #E0EEF9 hovered against #FDFDFD at rest: far
                // outside any sane tolerance. Park the cursor on our own window and let the
                // target repaint its resting state before sampling.
                ParkCursorOnOwner();
                btnPickPixel.Content = "sampling…";
                pickPixel = null;
                SettleThenSample();
            });
    }

    /// <summary>
    /// How long to wait after moving the cursor away before sampling. The target repaints on
    /// its own message loop, in another process, so this only has to outlast a redraw.
    /// </summary>
    private const int SettleMs = 250;

    private void SettleThenSample()
    {
        settleTimer = dispatcherQueue.CreateTimer();
        settleTimer.Interval = TimeSpan.FromMilliseconds(SettleMs);
        settleTimer.IsRepeating = false;
        settleTimer.Tick += (_, _) =>
        {
            settleTimer = null;
            btnPickPixel.Content = "Pick pixel (3s)…";
            btnPickPixel.IsEnabled = true;
            UpdateColorSwatch();
        };
        settleTimer.Start();
    }

    /// <summary>
    /// Parks the cursor on the owner window (the editor dialog is a modal ContentDialog over
    /// it, so its centre ≈ the WinForms editor's own centre) — off the capture target, which
    /// is the whole point of the settle phase.
    /// </summary>
    private void ParkCursorOnOwner()
    {
        if (GetWindowRect(ownerHwnd, out RECT r))
            SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    private void CaptureAt(POINT screenPt)
    {
        if (chkAnchor.IsChecked == true && WindowAnchor.Capture(screenPt, out string cls, out string title, out POINT clientPt))
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
        SetNum(numX, p.X);
        SetNum(numY, p.Y);
    }

    // ===== Template capture / load =====

    /// <summary>
    /// Same countdown idiom as <see cref="StartPick"/>. When it ends, a fixed-size square
    /// centred on the cursor is captured as the template — the pragmatic alternative to a
    /// drag-select overlay (a full selection rectangle needs a borderless overlay form to be
    /// usable, which is out of scope for this phase). The size is adjustable, and a PNG can
    /// also be loaded instead.
    /// </summary>
    private void StartCaptureTemplate()
    {
        captureTemplate?.Dispose();
        btnCaptureTemplate.IsEnabled = false;
        captureTemplate = PickCountdown.Start(
            dispatcherQueue,
            3,
            n => btnCaptureTemplate.Content = $"{n}…",
            () =>
            {
                captureTemplate = null;
                btnCaptureTemplate.Content = "Capture template (3s)…";
                btnCaptureTemplate.IsEnabled = true;
                GetCursorPos(out POINT p);
                CaptureTemplateAt(p);
            });
    }

    private void CaptureTemplateAt(POINT cursor)
    {
        int size = ValueOf(numTemplateSize);
        int half = size / 2;
        var region = new DrawingRect(cursor.X - half, cursor.Y - half, size, size);
        try
        {
            using Bitmap bmp = ImageMatcher.CaptureRegion(region);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            currentTemplate = ms.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                   or System.ComponentModel.Win32Exception or ExternalException)
        {
            ShowError("Template capture failed", ex.Message);
            return;
        }
        _ = UpdateTemplatePreviewAsync();
    }

    /// <summary>Loads a PNG from disk instead of capturing, for templates the user already has.</summary>
    private async void BrowseTemplate()
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, ownerHwnd);
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add("*");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            byte[] bytes = File.ReadAllBytes(file.Path);
            using (var probe = new Bitmap(new MemoryStream(bytes))) { } // validate it decodes
            currentTemplate = bytes;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowError("Template load failed", ex.Message);
            return;
        }
        await UpdateTemplatePreviewAsync();
    }

    private async Task UpdateTemplatePreviewAsync()
    {
        if (currentTemplate is null || currentTemplate.Length == 0)
        {
            picTemplate.Source = null;
            return;
        }
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(currentTemplate);
                await writer.StoreAsync();
                writer.DetachStream(); // don't let the writer's Dispose close the stream
            }
            stream.Seek(0);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await image.SetSourceAsync(stream);
            picTemplate.Source = image;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            picTemplate.Source = null;
        }
    }

    /// <summary>Converts the coordinates already on screen rather than discarding them.</summary>
    private void AnchorToggled()
    {
        var current = new POINT { X = ValueOf(numX), Y = ValueOf(numY) };

        if (chkAnchor.IsChecked == true)
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
        if (chkAnchor.IsChecked != true) { lblAnchor.Text = ""; return; }
        string label = anchorTitle.Length > 0 ? anchorTitle : anchorClass;
        lblAnchor.Text = label.Length > 0
            ? $"X/Y are relative to: {label}"
            : "No window captured yet — use Pick.";
    }

    // ===== Validation =====

    /// <summary>
    /// Fills the label picker with the labels already present in the sequence, so a Goto can
    /// pick its target from the list instead of retyping it. The field stays free text, so a
    /// brand-new label (or a sequence-less caller) is still editable.
    /// </summary>
    private void PopulateLabels()
    {
        var labels = new List<string>();
        if (sequence is not null)
        {
            foreach (SeqAction a in sequence)
            {
                if (a.Kind != ActionKind.Label) continue;
                string label = a.Label?.Trim() ?? "";
                if (label.Length > 0 && !labels.Contains(label)) labels.Add(label);
            }
        }
        cmbLabel.ItemsSource = labels;
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
        if (problem is null)
        {
            lblComboHint.Foreground = null; // back to the theme's text brush
            lblComboHint.Opacity = 0.6;
            lblComboHint.Text = "e.g. Ctrl+C · Alt+Tab · Shift+F5 · Enter · Esc";
        }
        else
        {
            lblComboHint.Foreground = new SolidColorBrush(Color.FromArgb(255, 178, 34, 34)); // Firebrick
            lblComboHint.Opacity = 1;
            lblComboHint.Text = problem;
        }
    }

    /// <summary>
    /// Dry-parses an expression through the Core evaluator so a syntax slip is caught at
    /// edit time instead of at run time. Only syntax-class messages are reported: unknown
    /// identifiers resolve at run time (variables come from earlier SetVar steps) and
    /// type/division errors depend on runtime values, so flagging those here would reject
    /// expressions the WinForms editor happily accepts.
    /// </summary>
    private static bool IsSyntaxProblem(string expr, out string? message)
    {
        message = null;
        if (expr.Trim().Length == 0) return false;
        try
        {
            ExpressionEvaluator.Evaluate(expr);
        }
        catch (ExpressionException ex)
        {
            string m = ex.Message;
            if (m.StartsWith("Unexpected token", StringComparison.Ordinal)
                || m.StartsWith("Unexpected end of expression", StringComparison.Ordinal)
                || m.Contains("unclosed parenthesis", StringComparison.Ordinal)
                || m.Contains("is not a valid number", StringComparison.Ordinal))
            {
                message = m;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The WinForms OnClosingValidate checks, message for message. On failure the close is
    /// cancelled and the message shown inline (no nested ContentDialog).
    /// </summary>
    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ActionKind kind = SelectedKind;

        if (kind == ActionKind.Key && InputSender.DescribeComboProblem(txtCombo.Text) is string problem)
        {
            args.Cancel = true;
            ShowError("Invalid key combo", problem);
            return;
        }
        if (kind == ActionKind.Text && txtText.Text.Length == 0)
        {
            args.Cancel = true;
            ShowError("Empty text", "Nothing to type.");
            return;
        }
        if (kind == ActionKind.WaitPixel && (PixelCondition)Math.Max(0, cmbCondition.SelectedIndex) == PixelCondition.None)
        {
            args.Cancel = true;
            ShowError("Nothing to wait for", "Set what to wait for.");
            return;
        }
        if (kind == ActionKind.IfElse)
        {
            if (txtCondition.Text.Trim().Length == 0)
            {
                args.Cancel = true;
                ShowError("Empty condition", "Type a condition, e.g. counter < 5.");
                return;
            }
            if (IsSyntaxProblem(txtCondition.Text, out string? condProblem))
            {
                args.Cancel = true;
                ShowError("Invalid expression", condProblem!);
                return;
            }
        }
        if (kind == ActionKind.SetVar)
        {
            string name = txtVarName.Text.Trim();
            if (name.Length == 0)
            {
                args.Cancel = true;
                ShowError("Empty variable name", "Name the variable to set.");
                return;
            }
            if (!IsValidIdentifier(name))
            {
                args.Cancel = true;
                ShowError("Invalid variable name",
                    "Variable names can only use letters, digits and underscores, and can't start with a digit.");
                return;
            }
            if (txtValueExpr.Text.Trim().Length == 0)
            {
                args.Cancel = true;
                ShowError("Empty value", "Type a value, e.g. counter + 1.");
                return;
            }
            if (IsSyntaxProblem(txtValueExpr.Text, out string? valueProblem))
            {
                args.Cancel = true;
                ShowError("Invalid expression", valueProblem!);
                return;
            }
        }
        if (kind is ActionKind.GotoLabel or ActionKind.Label && cmbLabel.Text.Trim().Length == 0)
        {
            args.Cancel = true;
            ShowError("Empty label", "Name the label.");
            return;
        }
        if (kind == ActionKind.FindImage && (currentTemplate is null || currentTemplate.Length == 0))
        {
            args.Cancel = true;
            ShowError("No template", "Capture or load a template image first.");
            return;
        }
        if (kind == ActionKind.FindText && txtTextQuery.Text.Trim().Length == 0)
        {
            args.Cancel = true;
            ShowError("Empty text", "Type the text to find.");
            return;
        }

        errorText.Visibility = Visibility.Collapsed;
        ApplyToAction();
    }

    private void ShowError(string title, string message)
    {
        errorText.Text = $"{title}: {message}";
        errorText.Visibility = Visibility.Visible;
    }

    /// <summary>WinForms OnClosingValidate's field copy, one assignment for one.</summary>
    private void ApplyToAction()
    {
        action.Kind = SelectedKind;
        action.X = ValueOf(numX);
        action.Y = ValueOf(numY);
        action.WindowRelative = chkAnchor.IsChecked == true;
        action.WindowClass = anchorClass;
        action.WindowTitle = anchorTitle;
        action.ClickMethod = cmbClickMethod.SelectedIndex;
        action.Button = cmbButton.SelectedIndex;
        action.DoubleClick = cmbClickType.SelectedIndex == 1;
        action.HoldMs = ValueOf(numHold);
        action.PreferSelector = chkPreferSelector.IsChecked == true;
        action.EndX = ValueOf(numEndX);
        action.EndY = ValueOf(numEndY);
        action.DragMs = ValueOf(numDragMs);
        action.ScrollNotches = ValueOf(numNotches);
        action.Horizontal = chkHorizontal.IsChecked == true;
        action.KeyCombo = txtCombo.Text.Trim();
        action.Text = txtText.Text;
        action.DelayMs = ValueOf(numDelay);
        action.DelayRandomPercent = ValueOf(numDelayRand);
        action.Comment = txtComment.Text;
        action.Condition = (PixelCondition)Math.Max(0, cmbCondition.SelectedIndex);
        action.CondX = ValueOf(numCondX);
        action.CondY = ValueOf(numCondY);
        action.CondColor = PixelSampler.ToRgb(ToDrawing(pnlSwatch.Background));
        action.CondTolerance = ValueOf(numTolerance);
        action.PixelTimeoutMs = ValueOf(numPixelTimeout);
        action.AbortRunOnTimeout = chkAbortOnTimeout.IsChecked == true;
        action.PollIntervalMs = ValueOf(numPollInterval);
        action.RepeatCount = ValueOf(numRepeatCount);
        action.ConditionExpr = txtCondition.Text.Trim();
        action.VarName = txtVarName.Text.Trim();
        action.ValueExpr = txtValueExpr.Text.Trim();
        action.Label = cmbLabel.Text.Trim();
        action.TemplatePng = currentTemplate;
        action.MatchThreshold = numThreshold.Value;
        action.SearchX = ValueOf(numSearchX);
        action.SearchY = ValueOf(numSearchY);
        action.SearchW = ValueOf(numSearchW);
        action.SearchH = ValueOf(numSearchH);
        action.ClickOnFound = chkClickOnFound.IsChecked == true;
        action.ClickOffsetX = ValueOf(numClickOffsetX);
        action.ClickOffsetY = ValueOf(numClickOffsetY);
        action.TextQuery = txtTextQuery.Text;
        action.RegexQuery = chkRegexQuery.IsChecked == true;
        action.Normalize();
    }

    /// <summary>The swatch brush packed back to the on-disk 0xRRGGBB (WinForms used ToRgb(BackColor)).</summary>
    private static System.Drawing.Color ToDrawing(Brush brush)
    {
        if (brush is SolidColorBrush solid)
        {
            Color c = solid.Color;
            return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        return System.Drawing.Color.Black;
    }
}
