namespace AutoClicker;

/// <summary>
/// Pure, side-effect-free helpers for the block/label structure of a sequence, shared by
/// the runner (jump targets, loop bounds) and the UI (indenting the list). Everything here
/// is deliberately static and input-only so the runner's jump logic can be unit-tested
/// without ever sending input.
/// </summary>
internal static class ControlFlow
{
    /// <summary>
    /// Maps each <see cref="ActionKind.Repeat"/>/<see cref="ActionKind.IfElse"/> opener
    /// index to the index of its matching <see cref="ActionKind.EndBlock"/>. An opener with
    /// no closer maps to <c>actions.Count</c> (its block runs to the end of the sequence);
    /// an EndBlock with no opener is ignored. Mismatched blocks therefore cannot loop
    /// forever at map-build time.
    /// </summary>
    public static Dictionary<int, int> BuildBlockMap(IReadOnlyList<SeqAction> actions)
    {
        var map = new Dictionary<int, int>();
        var stack = new Stack<int>();

        for (int i = 0; i < actions.Count; i++)
        {
            switch (actions[i].Kind)
            {
                case ActionKind.Repeat:
                case ActionKind.IfElse:
                    stack.Push(i);
                    break;
                case ActionKind.EndBlock:
                    if (stack.Count > 0) map[stack.Pop()] = i;
                    break;
            }
        }

        // Openers left on the stack have no closer: their block extends to the end.
        foreach (int open in stack) map[open] = actions.Count;
        return map;
    }

    /// <summary>Maps a label name to the index of its <see cref="ActionKind.Label"/>. First wins.</summary>
    public static Dictionary<string, int> BuildLabelMap(IReadOnlyList<SeqAction> actions)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i].Kind != ActionKind.Label) continue;
            string label = actions[i].Label?.Trim() ?? "";
            if (label.Length > 0 && !map.ContainsKey(label)) map[label] = i;
        }
        return map;
    }

    /// <summary>
    /// Nesting depth of each action, for indenting the sequence list. Repeat/IfElse push,
    /// EndBlock pops. Depth never goes negative: a stray EndBlock clamps at zero.
    /// </summary>
    public static int[] BuildDepth(IReadOnlyList<SeqAction> actions)
    {
        int[] depth = new int[actions.Count];
        int d = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            switch (actions[i].Kind)
            {
                case ActionKind.EndBlock:
                    d = Math.Max(0, d - 1);
                    depth[i] = d;
                    break;
                case ActionKind.Repeat:
                case ActionKind.IfElse:
                    depth[i] = d;
                    d++;
                    break;
                default:
                    depth[i] = d;
                    break;
            }
        }
        return depth;
    }
}
