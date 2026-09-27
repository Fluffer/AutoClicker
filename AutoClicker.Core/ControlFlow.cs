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

    /// <summary>
    /// Maps each <see cref="ActionKind.IfElse"/> opener index to the index of its
    /// <see cref="ActionKind.Else"/> marker, when one exists between the opener and its
    /// matching <see cref="ActionKind.EndBlock"/>. An <c>Else</c> attaches to the innermost
    /// still-open IfElse; an Else with no open IfElse (a stray marker) is absent from the
    /// map and is treated as a no-op by the runner. A Repeat block between an If and its
    /// Else does not break the pairing — Repeats are not If-scopes.
    /// </summary>
    public static Dictionary<int, int> BuildElseMap(IReadOnlyList<SeqAction> actions)
    {
        var elseMap = new Dictionary<int, int>();
        var openers = new Stack<int>(); // indices of open Repeat/IfElse openers
        var openIfs = new Stack<int>(); // indices of open IfElse openers (subset of openers)

        for (int i = 0; i < actions.Count; i++)
        {
            switch (actions[i].Kind)
            {
                case ActionKind.Repeat:
                    openers.Push(i);
                    break;
                case ActionKind.IfElse:
                    openers.Push(i);
                    openIfs.Push(i);
                    break;
                case ActionKind.Else:
                    if (openIfs.Count > 0) elseMap[openIfs.Peek()] = i;
                    break;
                case ActionKind.EndBlock:
                    if (openers.Count > 0 && actions[openers.Pop()].Kind == ActionKind.IfElse)
                        openIfs.Pop();
                    break;
            }
        }

        return elseMap;
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
    /// EndBlock pops. An <see cref="ActionKind.Else"/> sits at the same depth as its
    /// <see cref="ActionKind.IfElse"/> opener (one level shallower than the body it
    /// separates), so <c>else</c> aligns with <c>if</c>. Depth never goes negative: a stray
    /// EndBlock clamps at zero.
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
                case ActionKind.Else:
                    depth[i] = Math.Max(0, d - 1);
                    break;
                default:
                    depth[i] = d;
                    break;
            }
        }
        return depth;
    }
}
