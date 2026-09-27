using System.Globalization;

namespace AutoClicker;

/// <summary>
/// Lists, adds, edits and removes scheduled runs. Saves through <see cref="ScheduleStore"/>
/// after every mutation, so the background <see cref="ScheduleWatcher"/> — which reloads the
/// store each tick — picks changes up without any extra wiring.
/// </summary>
internal sealed class ScheduleForm : Form
{
    private readonly IReadOnlyList<Profile> profiles;
    private readonly ListBox lst = new();
    private List<ScheduledRun> schedules = new();

    private static readonly string[] DayLabels =
        { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    public ScheduleForm(IReadOnlyList<Profile> profiles)
    {
        this.profiles = profiles;
        Text = "Schedules";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 340);
        MaximizeBox = false;
        MinimizeBox = false;

        lst.Location = new Point(16, 16);
        lst.Size = new Size(340, 260);
        lst.IntegralHeight = false;
        Controls.Add(lst);

        var add = new Button { Text = "Add…", Location = new Point(372, 16), Size = new Size(92, 32) };
        add.Click += (_, _) => AddSchedule();
        var edit = new Button { Text = "Edit…", Location = new Point(372, 58), Size = new Size(92, 32) };
        edit.Click += (_, _) => EditSchedule();
        var remove = new Button { Text = "Remove", Location = new Point(372, 100), Size = new Size(92, 32) };
        remove.Click += (_, _) => RemoveSchedule();
        var close = new Button { Text = "Close", Location = new Point(372, 244), Size = new Size(92, 32), DialogResult = DialogResult.OK };
        Controls.AddRange(new Control[] { add, edit, remove, close });

        var hint = new Label
        {
            Text = "Schedules fire only while Auto Clicker is open.",
            Location = new Point(16, 286),
            AutoSize = true,
            ForeColor = Color.DimGray
        };
        Controls.Add(hint);

        schedules = ScheduleStore.Load();
        RefreshList();
    }

    private void RefreshList()
    {
        lst.BeginUpdate();
        lst.Items.Clear();
        foreach (ScheduledRun s in schedules) lst.Items.Add(Describe(s));
        lst.EndUpdate();
    }

    private void AddSchedule()
    {
        using var dlg = new ScheduleEditForm(new ScheduledRun(), profiles);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        schedules.Add(dlg.Result);
        SaveAndRefresh();
    }

    private void EditSchedule()
    {
        int i = lst.SelectedIndex;
        if (i < 0 || i >= schedules.Count) return;
        using var dlg = new ScheduleEditForm(schedules[i], profiles);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        schedules[i] = dlg.Result;
        SaveAndRefresh();
    }

    private void RemoveSchedule()
    {
        int i = lst.SelectedIndex;
        if (i < 0 || i >= schedules.Count) return;
        schedules.RemoveAt(i);
        SaveAndRefresh();
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
}
