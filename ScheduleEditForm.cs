using System.Globalization;

namespace AutoClicker;

/// <summary>
/// Add/edit one scheduled run: name, time, days of the week, target (sequence file or saved
/// profile) and an enabled flag. Styled like the other hand-built dialogs (absolute layout,
/// OK/Cancel), with no designer surface.
/// </summary>
internal sealed class ScheduleEditForm : Form
{
    private readonly TextBox txtName = new();
    private readonly NumericUpDown numHour = new();
    private readonly NumericUpDown numMinute = new();
    private readonly CheckedListBox lstDays = new();
    private readonly RadioButton rbSequence = new();
    private readonly RadioButton rbProfile = new();
    private readonly TextBox txtPath = new();
    private readonly Button btnBrowse = new();
    private readonly ComboBox cmbProfile = new();
    private readonly CheckBox chkEnabled = new();
    private readonly IReadOnlyList<Profile> profiles;

    public ScheduledRun Result { get; private set; } = new();

    public ScheduleEditForm(ScheduledRun seed, IReadOnlyList<Profile> profiles)
    {
        this.profiles = profiles;
        Text = string.IsNullOrEmpty(seed.Name) ? "Add schedule" : "Edit schedule";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 400);
        MaximizeBox = false;
        MinimizeBox = false;

        Controls.Add(Lbl("Name:", 16, 20));
        txtName.Location = new Point(110, 16);
        txtName.Size = new Size(270, 23);
        txtName.Text = seed.Name;
        Controls.Add(txtName);

        Controls.Add(Lbl("Time:", 16, 52));
        numHour.Location = new Point(110, 48);
        numHour.Size = new Size(60, 23);
        numHour.Minimum = 0;
        numHour.Maximum = 23;
        numMinute.Location = new Point(190, 48);
        numMinute.Size = new Size(60, 23);
        numMinute.Minimum = 0;
        numMinute.Maximum = 59;
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
        Controls.Add(numHour);
        Controls.Add(Lbl(":", 178, 52));
        Controls.Add(numMinute);

        Controls.Add(Lbl("Days:", 16, 84));
        lstDays.Location = new Point(110, 80);
        lstDays.Size = new Size(150, 104);
        lstDays.CheckOnClick = true;
        lstDays.Items.AddRange(new object[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" });
        for (int ui = 0; ui < 7; ui++)
            lstDays.SetItemChecked(ui, seed.DaysOfWeek.Length == 7 && seed.DaysOfWeek[(ui + 1) % 7]);
        Controls.Add(lstDays);

        Controls.Add(Lbl("Run:", 16, 196));
        rbSequence.Text = "Sequence file";
        rbSequence.Location = new Point(110, 192);
        rbSequence.AutoSize = true;
        rbSequence.Checked = string.IsNullOrWhiteSpace(seed.ProfileName);
        rbSequence.CheckedChanged += (_, _) => UpdateTargetEnabled();
        Controls.Add(rbSequence);

        txtPath.Location = new Point(110, 214);
        txtPath.Size = new Size(210, 23);
        txtPath.Text = seed.SequencePath;
        Controls.Add(txtPath);
        btnBrowse.Text = "…";
        btnBrowse.Location = new Point(326, 212);
        btnBrowse.Size = new Size(54, 27);
        btnBrowse.Click += (_, _) => BrowseForSequence();
        Controls.Add(btnBrowse);

        rbProfile.Text = "Profile";
        rbProfile.Location = new Point(110, 246);
        rbProfile.AutoSize = true;
        rbProfile.Checked = !string.IsNullOrWhiteSpace(seed.ProfileName);
        rbProfile.CheckedChanged += (_, _) => UpdateTargetEnabled();
        Controls.Add(rbProfile);

        cmbProfile.Location = new Point(110, 270);
        cmbProfile.Size = new Size(210, 23);
        cmbProfile.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (Profile p in profiles) cmbProfile.Items.Add(p.Name);
        int profileIdx = cmbProfile.Items.IndexOf(seed.ProfileName);
        if (profileIdx >= 0) cmbProfile.SelectedIndex = profileIdx;
        else if (cmbProfile.Items.Count > 0) cmbProfile.SelectedIndex = 0;
        Controls.Add(cmbProfile);

        chkEnabled.Text = "Enabled";
        chkEnabled.Location = new Point(110, 302);
        chkEnabled.AutoSize = true;
        chkEnabled.Checked = seed.Enabled;
        Controls.Add(chkEnabled);

        var ok = new Button { Text = "OK", Location = new Point(150, 350), Size = new Size(75, 32), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(253, 350), Size = new Size(82, 32), DialogResult = DialogResult.Cancel };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) => BuildResult();
        UpdateTargetEnabled();
    }

    private void BrowseForSequence()
    {
        using var ofd = new OpenFileDialog { Filter = "Auto Clicker sequence (*.acseq;*.json)|*.acseq;*.json|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) == DialogResult.OK) txtPath.Text = ofd.FileName;
    }

    private void UpdateTargetEnabled()
    {
        txtPath.Enabled = rbSequence.Checked;
        btnBrowse.Enabled = rbSequence.Checked;
        cmbProfile.Enabled = rbProfile.Checked;
    }

    private void BuildResult()
    {
        var days = new bool[7];
        for (int ui = 0; ui < 7; ui++)
            if (lstDays.GetItemChecked(ui)) days[(ui + 1) % 7] = true;

        Result = new ScheduledRun
        {
            Name = txtName.Text.Trim(),
            Time = string.Create(CultureInfo.InvariantCulture, $"{(int)numHour.Value:00}:{(int)numMinute.Value:00}"),
            DaysOfWeek = days,
            SequencePath = rbSequence.Checked ? txtPath.Text.Trim() : "",
            ProfileName = rbProfile.Checked ? cmbProfile.SelectedItem as string ?? "" : "",
            Enabled = chkEnabled.Checked,
        };
        Result.Normalize();
    }

    private static Label Lbl(string text, int x, int y) =>
        new() { Text = text, Location = new Point(x, y), AutoSize = true };
}
