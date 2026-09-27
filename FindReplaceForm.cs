namespace AutoClicker;

/// <summary>
/// Modeless Find &amp; Replace dialog (Ctrl+F). It holds no sequence state of its own: every
/// search/replace runs through callbacks into the form, so it always sees the live list and
/// can select rows, open the action editor and refresh the list without owning the data.
/// The form owns exactly one instance (see Form1.ShowFindReplace); closing it just releases
/// the instance.
/// </summary>
internal sealed class FindReplaceForm : Form
{
    private readonly Func<IReadOnlyList<SeqAction>> actions;
    private readonly Func<int> selection;
    private readonly Action<int> select;
    private readonly Action<int> edit;
    private readonly Func<bool> busy;
    private readonly Action refresh;
    private readonly Action<string> status;

    private readonly TextBox txtFind = new() { Width = 252 };
    private readonly TextBox txtReplace = new() { Width = 252 };
    private readonly CheckBox chkMatchCase = new() { Text = "Match case", AutoSize = true };
    private readonly ComboBox cmbScope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };

    public FindReplaceForm(
        Func<IReadOnlyList<SeqAction>> actions,
        Func<int> selection,
        Action<int> select,
        Action<int> edit,
        Func<bool> busy,
        Action refresh,
        Action<string> status)
    {
        this.actions = actions;
        this.selection = selection;
        this.select = select;
        this.edit = edit;
        this.busy = busy;
        this.refresh = refresh;
        this.status = status;

        Text = "Find and replace";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(372, 214);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        cmbScope.Items.AddRange("All fields", "Action text", "Comment");
        cmbScope.SelectedIndex = 0;

        var lblFind = new Label { Text = "Find what:", AutoSize = true, Location = new Point(12, 20) };
        txtFind.Location = new Point(104, 16);

        var lblReplace = new Label { Text = "Replace with:", AutoSize = true, Location = new Point(12, 52) };
        txtReplace.Location = new Point(104, 48);

        chkMatchCase.Location = new Point(12, 82);

        var lblScope = new Label { Text = "Search in:", AutoSize = true, Location = new Point(120, 86) };
        cmbScope.Location = new Point(182, 82);

        var hint = new Label
        {
            Text = "Replace edits only the free-text Comment/Text fields; matches in a computed description open the action editor.",
            AutoSize = false,
            Location = new Point(12, 116),
            Size = new Size(348, 30),
            ForeColor = Color.DimGray,
        };

        var btnFindNext = new Button { Text = "Find next", Location = new Point(12, 158), Size = new Size(82, 30) };
        btnFindNext.Click += (_, _) => FindNext();
        var btnReplace = new Button { Text = "Replace", Location = new Point(100, 158), Size = new Size(82, 30) };
        btnReplace.Click += (_, _) => Replace();
        var btnReplaceAll = new Button { Text = "Replace all", Location = new Point(188, 158), Size = new Size(92, 30) };
        btnReplaceAll.Click += (_, _) => ReplaceAll();
        var btnClose = new Button { Text = "Close", Location = new Point(286, 158), Size = new Size(72, 30) };
        btnClose.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { lblFind, txtFind, lblReplace, txtReplace, chkMatchCase, lblScope, cmbScope, hint, btnFindNext, btnReplace, btnReplaceAll, btnClose });

        AcceptButton = btnFindNext;
        CancelButton = btnClose;
    }

    private FindScope Scope => (FindScope)cmbScope.SelectedIndex;

    /// <summary>Refocuses the Find box (used when Ctrl+F reopens an existing dialog).</summary>
    public void FocusFindBox()
    {
        txtFind.Focus();
        txtFind.SelectAll();
    }

    private bool MatchesRow(int i)
    {
        IReadOnlyList<SeqAction> list = actions();
        return i >= 0 && i < list.Count
            && FindReplace.Matches(list[i], Scope, txtFind.Text, chkMatchCase.Checked);
    }

    private void FindNext()
    {
        IReadOnlyList<SeqAction> list = actions();
        if (list.Count == 0) { status("Nothing to search — the sequence is empty."); return; }
        if (string.IsNullOrEmpty(txtFind.Text)) { status("Type text to find first."); return; }

        int idx = FindReplace.NextMatch(selection(), list.Count, MatchesRow, wrap: true);
        if (idx < 0) { status($"No match for \"{txtFind.Text}\"."); return; }

        select(idx);
        status($"Found \"{txtFind.Text}\" at action #{idx + 1}.");
    }

    private void Replace()
    {
        if (busy()) { status("Stop the run/recording before replacing."); return; }
        IReadOnlyList<SeqAction> list = actions();
        if (list.Count == 0) { status("Nothing to replace — the sequence is empty."); return; }
        if (string.IsNullOrEmpty(txtFind.Text)) { status("Type text to find first."); return; }

        int idx = selection();
        // With no match under the cursor, advance to the next one first (same as Find next).
        if (!MatchesRow(idx)) { FindNext(); return; }

        SeqAction action = list[idx];
        string? field = FindReplace.ReplaceOne(action, Scope, txtFind.Text, txtReplace.Text, chkMatchCase.Checked);
        if (field is null)
        {
            status("Match is in a computed description — edit the action.");
            edit(idx);
            return;
        }

        refresh();
        select(idx);
        status($"Replaced in {field} of action #{idx + 1}.");
    }

    private void ReplaceAll()
    {
        if (busy()) { status("Stop the run/recording before replacing."); return; }
        IReadOnlyList<SeqAction> list = actions();
        if (list.Count == 0) { status("Nothing to replace — the sequence is empty."); return; }
        if (string.IsNullOrEmpty(txtFind.Text)) { status("Type text to find first."); return; }

        int n = FindReplace.ReplaceAll(list, Scope, txtFind.Text, txtReplace.Text, chkMatchCase.Checked);
        if (n == 0) { status($"No replaceable occurrences of \"{txtFind.Text}\" (only Comment/Text are edited)."); return; }

        refresh();
        status($"Replaced {n} occurrence{(n == 1 ? "" : "s")}.");
    }
}
