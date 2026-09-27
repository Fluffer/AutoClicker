using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Wave 3 (F7): Find &amp; Replace. The find-next index math, searchable-text assembly and
/// the replace-in-comment/text scoping rules are all pure (<see cref="FindReplace"/>) and
/// tested headlessly; the modeless dialog itself only wires those helpers to the ListView.
/// </summary>
public class FindReplaceTests
{
    [Fact]
    public void SearchableText_includes_description_target_combo_text_and_comment()
    {
        var a = new SeqAction
        {
            Kind = ActionKind.Key,
            KeyCombo = "Ctrl+C",
            Text = "hello world",
            Comment = "paste at top",
        };

        string text = a.SearchableText();

        Assert.Contains("Press Ctrl+C", text); // Describe()
        Assert.Contains("Ctrl+C", text);       // KeyCombo
        Assert.Contains("hello world", text);  // Text
        Assert.Contains("paste at top", text); // Comment
    }

    // ---- Find-next index math ----

    [Fact]
    public void NextMatch_finds_after_start_without_wrapping()
    {
        int index = FindReplace.NextMatch(1, 5, i => i == 3, wrap: false);

        Assert.Equal(3, index);
    }

    [Fact]
    public void NextMatch_wraps_to_the_beginning()
    {
        int index = FindReplace.NextMatch(3, 5, i => i == 1, wrap: true);

        Assert.Equal(1, index);
    }

    [Fact]
    public void NextMatch_returns_minus_one_when_nothing_matches()
    {
        int index = FindReplace.NextMatch(0, 5, _ => false, wrap: true);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void NextMatch_handles_no_selection_starting_from_zero()
    {
        int index = FindReplace.NextMatch(-1, 3, i => i == 0, wrap: true);

        Assert.Equal(0, index);
    }

    [Fact]
    public void NextMatch_empty_list_returns_minus_one()
    {
        Assert.Equal(-1, FindReplace.NextMatch(0, 0, _ => true, wrap: true));
    }

    // ---- Replace scoping ----

    [Fact]
    public void ReplaceOne_replaces_comment_when_scope_is_comment()
    {
        var a = new SeqAction { Kind = ActionKind.Wait, Comment = "wait for login" };

        string? field = FindReplace.ReplaceOne(a, FindScope.Comment, "login", "checkout", matchCase: false);

        Assert.Equal("Comment", field);
        Assert.Equal("wait for checkout", a.Comment);
    }

    [Fact]
    public void ReplaceOne_replaces_text_when_scope_is_all_and_match_is_in_text()
    {
        var a = new SeqAction { Kind = ActionKind.Text, Text = "hello hello" };

        string? field = FindReplace.ReplaceOne(a, FindScope.All, "hello", "bye", matchCase: false);

        Assert.Equal("Text", field);
        Assert.Equal("bye hello", a.Text); // first occurrence only
    }

    [Fact]
    public void ReplaceOne_returns_null_when_match_is_computed()
    {
        var a = new SeqAction { Kind = ActionKind.Key, KeyCombo = "Ctrl+Shift+S" };

        string? field = FindReplace.ReplaceOne(a, FindScope.All, "Ctrl+Shift+S", "X", matchCase: false);

        Assert.Null(field);
        Assert.Equal("Ctrl+Shift+S", a.KeyCombo); // untouched — not a free-text field
    }

    [Fact]
    public void ReplaceOne_returns_null_for_action_text_scope()
    {
        var a = new SeqAction { Kind = ActionKind.Click, Comment = "click here" };

        string? field = FindReplace.ReplaceOne(a, FindScope.ActionText, "Click", "Tap", matchCase: false);

        Assert.Null(field);
        Assert.Equal("click here", a.Comment); // comment untouched under ActionText scope
    }

    [Fact]
    public void ReplaceAll_replaces_comment_and_text_and_reports_count()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Text, Text = "foo bar foo", Comment = "a foo here" },
            new() { Kind = ActionKind.Click, Comment = "no match" },
        };

        int n = FindReplace.ReplaceAll(actions, FindScope.All, "foo", "baz", matchCase: false);

        Assert.Equal(3, n);
        Assert.Equal("baz bar baz", actions[0].Text);
        Assert.Equal("a baz here", actions[0].Comment);
        Assert.Equal("no match", actions[1].Comment);
    }

    [Fact]
    public void ReplaceAll_respects_match_case()
    {
        var actions = new List<SeqAction> { new() { Comment = "Foo foo" } };

        int n = FindReplace.ReplaceAll(actions, FindScope.Comment, "foo", "x", matchCase: true);

        Assert.Equal(1, n);
        Assert.Equal("Foo x", actions[0].Comment);
    }

    [Fact]
    public void Matches_is_case_insensitive_by_default()
    {
        var a = new SeqAction { Kind = ActionKind.Text, Text = "HELLO" };

        Assert.True(FindReplace.Matches(a, FindScope.All, "hello", matchCase: false));
        Assert.False(FindReplace.Matches(a, FindScope.All, "hello", matchCase: true));
    }
}
