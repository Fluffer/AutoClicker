namespace AutoClicker;

/// <summary>Which part of an action the Find &amp; Replace dialog searches.</summary>
internal enum FindScope
{
    /// <summary>The full <see cref="SeqAction.SearchableText"/> blob.</summary>
    All = 0,

    /// <summary>Only the computed Action + Target columns.</summary>
    ActionText = 1,

    /// <summary>Only the free-text comment.</summary>
    Comment = 2,
}

/// <summary>
/// Pure Find &amp; Replace logic with no WinForms dependency, so the find-next index math and
/// the replace scoping rules can be tested directly. The dialog only supplies the current
/// selection and calls back into the form to select/edit rows.
/// </summary>
/// <remarks>
/// Replace scope is deliberately honest and narrow: a match in the Action/Target columns is
/// a COMPUTED description of the action (kind, button, coordinates, key combo...) and can't
/// be edited textually, so Replace only rewrites the two free-text fields
/// (<see cref="SeqAction.Text"/> and <see cref="SeqAction.Comment"/>). When a match lands
/// anywhere else the caller opens the action editor instead.
/// </remarks>
internal static class FindReplace
{
    /// <summary>True when <paramref name="find"/> occurs in <paramref name="haystack"/>.</summary>
    public static bool Contains(string haystack, string find, bool matchCase)
    {
        if (string.IsNullOrEmpty(find)) return false;
        return haystack.Contains(find, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The searchable text for one action under the given scope.</summary>
    public static string SearchText(SeqAction a, FindScope scope) => scope switch
    {
        FindScope.ActionText => a.Describe() + "\n" + a.DescribeTarget(),
        FindScope.Comment => a.Comment,
        _ => a.SearchableText(),
    };

    /// <summary>True when action <paramref name="a"/> matches under the given scope.</summary>
    public static bool Matches(SeqAction a, FindScope scope, string find, bool matchCase) =>
        Contains(SearchText(a, scope), find, matchCase);

    /// <summary>
    /// Finds the next matching index after <paramref name="start"/>, wrapping around to the
    /// beginning when <paramref name="wrap"/> is set. Returns -1 when nothing matches.
    /// A <paramref name="start"/> of -1 means "no selection" and searches from 0.
    /// </summary>
    public static int NextMatch(int start, int count, Func<int, bool> matches, bool wrap)
    {
        if (count <= 0) return -1;
        for (int i = start + 1; i < count; i++)
            if (matches(i)) return i;
        if (wrap)
        {
            for (int i = 0; i <= start; i++)
                if (matches(i)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Replaces the first occurrence on <paramref name="action"/> under the given scope.
    /// Returns the field that was edited ("Text" or "Comment"), or null when the match is in
    /// a computed field (caller should open the action editor).
    /// </summary>
    public static string? ReplaceOne(SeqAction action, FindScope scope, string find, string replace, bool matchCase)
    {
        if (string.IsNullOrEmpty(find)) return null;
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (scope == FindScope.Comment)
        {
            if (!Contains(action.Comment, find, matchCase)) return null;
            action.Comment = ReplaceFirst(action.Comment, find, replace, comparison);
            return "Comment";
        }

        // ActionText scope searches only the computed description — nothing to replace.
        if (scope == FindScope.ActionText) return null;

        // All fields: prefer the free-text Text field, then Comment; anything else is computed.
        if (Contains(action.Text, find, matchCase))
        {
            action.Text = ReplaceFirst(action.Text, find, replace, comparison);
            return "Text";
        }
        if (Contains(action.Comment, find, matchCase))
        {
            action.Comment = ReplaceFirst(action.Comment, find, replace, comparison);
            return "Comment";
        }
        return null;
    }

    /// <summary>
    /// Replaces every occurrence in the free-text fields over the whole list. Returns the
    /// total number of replacements made. ActionText scope replaces nothing (computed only).
    /// </summary>
    public static int ReplaceAll(IReadOnlyList<SeqAction> actions, FindScope scope, string find, string replace, bool matchCase)
    {
        if (string.IsNullOrEmpty(find)) return 0;
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int total = 0;
        foreach (SeqAction a in actions)
        {
            if (scope == FindScope.ActionText) continue;

            if (scope is FindScope.Comment or FindScope.All)
            {
                int n = CountOccurrences(a.Comment, find, comparison);
                if (n > 0) { a.Comment = a.Comment.Replace(find, replace, comparison); total += n; }
            }
            if (scope == FindScope.All)
            {
                int n = CountOccurrences(a.Text, find, comparison);
                if (n > 0) { a.Text = a.Text.Replace(find, replace, comparison); total += n; }
            }
        }
        return total;
    }

    private static string ReplaceFirst(string input, string find, string replace, StringComparison comparison)
    {
        int i = input.IndexOf(find, comparison);
        if (i < 0) return input;
        return string.Concat(input.AsSpan(0, i), replace, input.AsSpan(i + find.Length));
    }

    private static int CountOccurrences(string input, string find, StringComparison comparison)
    {
        int count = 0, start = 0;
        while (true)
        {
            int i = input.IndexOf(find, start, comparison);
            if (i < 0) return count;
            count++;
            start = i + find.Length;
        }
    }
}
