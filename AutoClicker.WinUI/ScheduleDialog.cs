using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AutoClicker.WinUI;

/// <summary>
/// Lists, adds, edits and removes scheduled runs — the WinUI twin of WinForms'
/// <c>ScheduleForm</c> + <c>ScheduleEditForm</c>. Saves through <see cref="ScheduleStore"/>
/// after every mutation, so the background <see cref="ScheduleWatcher"/> — which reloads the
/// store each tick — picks changes up without any extra wiring.
/// </summary>
/// <remarks>
/// A plain <see cref="Window"/> (like <c>FindReplaceDialog</c>), not a ContentDialog:
/// ContentDialog's default style caps the dialog at 548 logical px and an instance
/// <c>MaxWidth</c> did NOT override it — the two-column content (790px) was clipped
/// (day-grid columns 3-4, the browse button, the minute box; M3 QA). WinUI also
/// forbids nested ContentDialog.ShowAsync, which is why the Add/Edit sub-dialog is an
/// INLINE editor: the left column is the schedule list with Add/Edit/Remove, the right
/// column is the editor the Add/Edit buttons populate — Save/Cancel apply to the
/// in-memory list and refresh it, exactly like <c>ScheduleEditForm.BuildResult</c>.
/// </remarks>
internal sealed class ScheduleDialog : Window
{
    private readonly IReadOnlyList<Profile> profiles;
    private readonly IntPtr ownerHwnd;

    private readonly ListView list = new() { Width = 300, Height = 200 };
    private List<ScheduledRun> schedules = new();

    // ---- Inline editor controls (names follow ScheduleEditForm for easy diffing) ----
    private readonly TextBox txtName = new() { Width = 240 };
    private readonly NumberBox numHour = new() { Minimum = 0, Maximum = 23, Width = 70, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox numMinute = new() { Minimum = 0, Maximum = 59, Width = 70, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly CheckBox[] dayChecks = new CheckBox[7];
    private readonly RadioButton rbSequence = new() { Content = "Sequence file", GroupName = "scheduleTarget" };
    private readonly RadioButton rbProfile = new() { Content = "Profile", GroupName = "scheduleTarget" };
    private readonly TextBox txtPath = new() { Width = 200 };
    private readonly Button btnBrowse = new() { Content = "…", Width = 40 };
    private readonly ComboBox cmbProfile = new() { Width = 200 };
    private readonly CheckBox chkEnabled = new() { Content = "Enabled" };

    // 460 not 300: the 4-column day grid needs ~442px (Mon-Thu), and at 300 the
    // Thursday column was clipped offscreen with no way to scroll to it (M3 QA).
    private readonly StackPanel editorPanel = new() { Spacing = 8, Width = 460 };
    private readonly TextBlock editorTitle = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock editorPlaceholder = new()
    {
        Text = "Select a schedule and click Edit, or click Add to create one.",
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 105, 105, 105)), // Color.DimGray
    };

    // The schedule currently being edited: index -1 means "new" (Add), otherwise the row.
    private int editingIndex = -1;

    private static readonly string[] DayLabels =
        { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    public ScheduleDialog(IntPtr ownerHwnd, IReadOnlyList<Profile> profiles)
    {
        this.profiles = profiles;
        this.ownerHwnd = ownerHwnd;

        BuildEditor();

        // ---- Left column: the list + its three buttons ----
        var add = new Button { Content = "Add…", Width = 92 };
        add.Click += (_, _) => BeginEdit(new ScheduledRun(), -1);
        var edit = new Button { Content = "Edit…", Width = 92 };
        edit.Click += (_, _) => EditSelected();
        var remove = new Button { Content = "Remove", Width = 92 };
        remove.Click += (_, _) => RemoveSelected();

        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        listButtons.Children.Add(add);
        listButtons.Children.Add(edit);
        listButtons.Children.Add(remove);

        var hint = new TextBlock
        {
            Text = "Schedules fire only while Auto Clicker is open.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 105, 105, 105)),
        };

        var left = new StackPanel { Spacing = 8, Width = 300 };
        left.Children.Add(list);
        left.Children.Add(listButtons);
        left.Children.Add(hint);
        var btnClose = new Button { Content = "Close", Width = 92 };
        btnClose.Click += (_, _) => Close(); // Window has no ContentDialog CloseButtonText
        left.Children.Add(btnClose);

        // ---- Right column: the editor (placeholder until Add/Edit) ----
        var right = new StackPanel { Spacing = 8, Width = 460 };
        right.Children.Add(editorTitle);
        right.Children.Add(editorPlaceholder);
        right.Children.Add(editorPanel);

        var root = new Grid { ColumnSpacing = 16, Width = 790 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        root.Children.Add(left);
        root.Children.Add(right);

        Title = "Schedules";
        // A plain Window sizes freely — ContentDialog's default style caps at 548 logical
        // px and an instance MaxWidth=900 did NOT override it, clipping our 790px
        // two-column content (day-grid columns 3-4, browse, minute box; M3 QA). The
        // ScrollViewer covers vertical overflow instead of the dialog's own cap.
        Content = new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
        };
        // Physical pixels (AppWindow) vs logical XAML units: our root Grid is 790
        // LOGICAL wide (= 1185 physical at 150% DPI), so a 900-physical window still
        // clipped the day grid (Wednesday/Thursday offscreen, M3 QA round 3). Size
        // generously in physical terms; the ScrollViewer absorbs the vertical slack.
        AppWindow.Resize(new SizeInt32(1440, 1120));

        schedules = ScheduleStore.Load();
        RefreshList();
        ShowPlaceholder();
    }

    public void Show() => Activate();

    // =====================================================================
    // List side (ScheduleForm)
    // =====================================================================

    private void RefreshList()
    {
        list.Items.Clear();
        foreach (ScheduledRun s in schedules) list.Items.Add(Describe(s));
    }

    private void EditSelected()
    {
        int i = list.SelectedIndex;
        if (i < 0 || i >= schedules.Count) return;
        BeginEdit(schedules[i], i);
    }

    private void RemoveSelected()
    {
        int i = list.SelectedIndex;
        if (i < 0 || i >= schedules.Count) return;
        schedules.RemoveAt(i);
        SaveAndRefresh();
        ShowPlaceholder();
    }

    private void SaveAndRefresh()
    {
        ScheduleStore.Save(schedules);
        RefreshList();
    }

    private static string Describe(ScheduledRun s)
    {
        string days = string.Join(",", s.DaysOfWeek.Length == 7
            ? DayLabels.Select((label, ui) => s.DaysOfWeek[(ui + 1) % 7] ? label[..2] : null).Where(x => x != null)
            : Array.Empty<string>());
        string target = string.IsNullOrWhiteSpace(s.Target) ? "(no target)" : s.Target;
        string prefix = s.Enabled ? "" : "(off) ";
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{s.Name} — {s.Time} [{days}] → {target}");
    }

    // =====================================================================
    // Editor side (ScheduleEditForm)
    // =====================================================================

    private void BuildEditor()
    {
        // Name
        editorPanel.Children.Add(Labeled("Name:", txtName));

        // Time: hour : minute
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        timeRow.Children.Add(new TextBlock { Text = "Time:", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        timeRow.Children.Add(numHour);
        timeRow.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center });
        timeRow.Children.Add(numMinute);
        editorPanel.Children.Add(timeRow);

        // Days of the week
        editorPanel.Children.Add(new TextBlock { Text = "Days:" });
        // Two rows of four instead of seven stacked checkboxes (WinUI's StackPanel has
        // no wrap mode): made the editor ~200px taller than needed and pushed
        // Save/Cancel past the dialog edge (M3 QA).
        var daysPanel = new Grid { ColumnSpacing = 6, RowSpacing = 4 };
        for (int c = 0; c < 4; c++)
            daysPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        daysPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        daysPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int ui = 0; ui < 7; ui++)
        {
            dayChecks[ui] = new CheckBox { Content = DayLabels[ui] };
            Grid.SetColumn(dayChecks[ui], ui % 4);
            Grid.SetRow(dayChecks[ui], ui / 4);
            daysPanel.Children.Add(dayChecks[ui]);
        }
        editorPanel.Children.Add(daysPanel);

        // Target: sequence file or profile
        editorPanel.Children.Add(new TextBlock { Text = "Run:" });
        rbSequence.Checked += (_, _) => UpdateTargetEnabled();
        rbProfile.Checked += (_, _) => UpdateTargetEnabled();
        editorPanel.Children.Add(rbSequence);

        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pathRow.Children.Add(txtPath);
        btnBrowse.Click += (_, _) => _ = BrowseForSequenceAsync();
        pathRow.Children.Add(btnBrowse);
        editorPanel.Children.Add(pathRow);

        editorPanel.Children.Add(rbProfile);
        foreach (Profile p in profiles) cmbProfile.Items.Add(p.Name);
        editorPanel.Children.Add(cmbProfile);

        editorPanel.Children.Add(chkEnabled);

        // Save / Cancel for the inline editor.
        var save = new Button { Content = "Save", Width = 80 };
        save.Click += (_, _) => CommitEdit();
        var cancel = new Button { Content = "Cancel", Width = 80 };
        cancel.Click += (_, _) => ShowPlaceholder();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        editorPanel.Children.Add(buttons);
    }

    private static StackPanel Labeled(string label, FrameworkElement field)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(field);
        return row;
    }

    /// <summary>Seeds the inline editor from a schedule (a fresh one for Add, the row for Edit).</summary>
    private void BeginEdit(ScheduledRun seed, int index)
    {
        editingIndex = index;
        editorTitle.Text = string.IsNullOrEmpty(seed.Name) ? "Add schedule" : "Edit schedule";

        txtName.Text = seed.Name;

        if (TimeSpan.TryParse(seed.Time, CultureInfo.InvariantCulture, out TimeSpan t))
        {
            numHour.Value = t.Hours;
            numMinute.Value = t.Minutes;
        }
        else
        {
            numHour.Value = 9;
            numMinute.Value = 0;
        }

        for (int ui = 0; ui < 7; ui++)
            dayChecks[ui].IsChecked = seed.DaysOfWeek.Length == 7 && seed.DaysOfWeek[(ui + 1) % 7];

        rbSequence.IsChecked = string.IsNullOrWhiteSpace(seed.ProfileName);
        rbProfile.IsChecked = !string.IsNullOrWhiteSpace(seed.ProfileName);

        txtPath.Text = seed.SequencePath;

        int profileIdx = cmbProfile.Items.IndexOf(seed.ProfileName);
        if (profileIdx >= 0) cmbProfile.SelectedIndex = profileIdx;
        else if (cmbProfile.Items.Count > 0) cmbProfile.SelectedIndex = 0;

        chkEnabled.IsChecked = seed.Enabled;

        UpdateTargetEnabled();

        editorPlaceholder.Visibility = Visibility.Collapsed;
        editorPanel.Visibility = Visibility.Visible;
    }

    private void ShowPlaceholder()
    {
        editingIndex = -1;
        editorTitle.Text = "";
        editorPanel.Visibility = Visibility.Collapsed;
        editorPlaceholder.Visibility = Visibility.Visible;
    }

    private void UpdateTargetEnabled()
    {
        bool sequence = rbSequence.IsChecked == true;
        txtPath.IsEnabled = sequence;
        btnBrowse.IsEnabled = sequence;
        cmbProfile.IsEnabled = rbProfile.IsChecked == true;
    }

    private async Task BrowseForSequenceAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, ownerHwnd);
            picker.FileTypeFilter.Add(".acseq");
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add("*");

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null) txtPath.Text = file.Path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            // A picker failure is cosmetic here: the path box stays editable by hand.
        }
    }

    /// <summary>ScheduleEditForm.BuildResult + the Add/Edit commit, applied to the in-memory list.</summary>
    private void CommitEdit()
    {
        var days = new bool[7];
        for (int ui = 0; ui < 7; ui++)
            if (dayChecks[ui].IsChecked == true) days[(ui + 1) % 7] = true;

        var result = new ScheduledRun
        {
            Name = txtName.Text.Trim(),
            Time = string.Create(CultureInfo.InvariantCulture, $"{(int)numHour.Value:00}:{(int)numMinute.Value:00}"),
            DaysOfWeek = days,
            SequencePath = rbSequence.IsChecked == true ? txtPath.Text.Trim() : "",
            ProfileName = rbProfile.IsChecked == true ? cmbProfile.SelectedItem as string ?? "" : "",
            Enabled = chkEnabled.IsChecked == true,
        };
        result.Normalize();

        if (editingIndex >= 0 && editingIndex < schedules.Count) schedules[editingIndex] = result;
        else schedules.Add(result);

        SaveAndRefresh();
        ShowPlaceholder();
    }
}
