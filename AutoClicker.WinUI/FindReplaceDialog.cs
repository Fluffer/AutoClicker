using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace AutoClicker.WinUI;

/// <summary>
/// Modeless Find &amp; Replace window (Ctrl+F) — the WinUI twin of WinForms'
/// <c>FindReplaceForm</c>. It holds no sequence state of its own: every search/replace runs
/// through callbacks into the main window, so it always sees the live list and can select
/// rows, open the action editor and refresh the list without owning the data. The main window
/// owns exactly one instance (see <c>MainWindow.ShowFindReplace</c>); closing it just releases
/// the instance.
/// </summary>
/// <remarks>
/// All matching/replacing is delegated to the Core <see cref="FindReplace"/> static helpers —
/// the same ones the WinForms form calls — so the two UIs cannot drift apart on scope rules,
/// wrap-around or which fields are editable. A modeless <see cref="Window"/> (rather than a
/// ContentDialog) is used because the WinForms original is modeless and the main window must
/// stay interactive while it is open.
/// </remarks>
internal sealed class FindReplaceDialog : Window
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
    private readonly CheckBox chkMatchCase = new() { Content = "Match case" };
    private readonly ComboBox cmbScope = new() { Width = 120 };

    public FindReplaceDialog(
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

        Title = "Find and replace";

        cmbScope.Items.Add("All fields");
        cmbScope.Items.Add("Action text");
        cmbScope.Items.Add("Comment");
        cmbScope.SelectedIndex = 0;

        // ---- Layout: label column + field column, mirroring the WinForms coordinates ----
        var grid = new Grid { Padding = new Thickness(12), RowSpacing = 8, ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddRow(grid, 0, "Find what:", txtFind);
        AddRow(grid, 1, "Replace with:", txtReplace);

        // Row 2: Match case + Search in, side by side.
        var scopeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        scopeRow.Children.Add(chkMatchCase);
        scopeRow.Children.Add(new TextBlock { Text = "Search in:", VerticalAlignment = VerticalAlignment.Center });
        scopeRow.Children.Add(cmbScope);
        Grid.SetRow(scopeRow, 2);
        Grid.SetColumn(scopeRow, 0);
        Grid.SetColumnSpan(scopeRow, 2);
        grid.Children.Add(scopeRow);

        // Row 3: the hint, exactly the WinForms wording.
        var hint = new TextBlock
        {
            Text = "Replace edits only the free-text Comment/Text fields; matches in a computed description open the action editor.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 105, 105, 105)), // Color.DimGray
        };
        Grid.SetRow(hint, 3);
        Grid.SetColumn(hint, 0);
        Grid.SetColumnSpan(hint, 2);
        grid.Children.Add(hint);

        // Row 4: the four buttons.
        var btnFindNext = new Button { Content = "Find next", MinWidth = 82 };
        btnFindNext.Click += (_, _) => FindNext();
        var btnReplace = new Button { Content = "Replace", MinWidth = 82 };
        btnReplace.Click += (_, _) => Replace();
        var btnReplaceAll = new Button { Content = "Replace all", MinWidth = 92 };
        btnReplaceAll.Click += (_, _) => ReplaceAll();
        var btnClose = new Button { Content = "Close", MinWidth = 72 };
        btnClose.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(btnFindNext);
        buttons.Children.Add(btnReplace);
        buttons.Children.Add(btnReplaceAll);
        buttons.Children.Add(btnClose);
        Grid.SetRow(buttons, 4);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;

        // Sized to fit the measured content: 420x300 clipped the button row BOTH ways
        // (M3 QA — Close fell off the right edge, all four buttons below the bottom).
        AppWindow.Resize(new SizeInt32(520, 480));
    }

    private static void AddRow(Grid grid, int row, string label, FrameworkElement field)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
    }

    private FindScope Scope => (FindScope)cmbScope.SelectedIndex;

    /// <summary>Refocuses the Find box (used when Ctrl+F reopens an existing dialog).</summary>
    public void FocusFindBox()
    {
        txtFind.Focus(FocusState.Programmatic);
        txtFind.SelectAll();
    }

    private bool MatchesRow(int i)
    {
        IReadOnlyList<SeqAction> list = actions();
        return i >= 0 && i < list.Count
            && FindReplace.Matches(list[i], Scope, txtFind.Text, chkMatchCase.IsChecked == true);
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
        string? field = FindReplace.ReplaceOne(action, Scope, txtFind.Text, txtReplace.Text, chkMatchCase.IsChecked == true);
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

        int n = FindReplace.ReplaceAll(list, Scope, txtFind.Text, txtReplace.Text, chkMatchCase.IsChecked == true);
        if (n == 0) { status($"No replaceable occurrences of \"{txtFind.Text}\" (only Comment/Text are edited)."); return; }

        refresh();
        status($"Replaced {n} occurrence{(n == 1 ? "" : "s")}.");
    }
}
