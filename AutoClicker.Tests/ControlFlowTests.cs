using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Control-flow structure and the runner's instruction-pointer semantics. Block/label maps
/// and nesting depth are pure functions and are tested directly; the runner is exercised
/// only with non-input kinds (Wait/SetVar/IfElse/Repeat/Break/GotoLabel/Label, DelayMs 0),
/// so no real click is ever sent.
/// </summary>
public class ControlFlowTests
{
    // ---- Pure helpers ----

    [Fact]
    public void Block_map_matches_openers_to_closers()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat },
            new() { Kind = ActionKind.Click },
            new() { Kind = ActionKind.EndBlock },
        };
        var map = ControlFlow.BuildBlockMap(actions);
        Assert.Equal(2, map[0]);
    }

    [Fact]
    public void Block_map_handles_nesting()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat },   // 0
            new() { Kind = ActionKind.IfElse },   // 1
            new() { Kind = ActionKind.Wait },     // 2
            new() { Kind = ActionKind.EndBlock }, // 3 closes IfElse
            new() { Kind = ActionKind.EndBlock }, // 4 closes Repeat
        };
        var map = ControlFlow.BuildBlockMap(actions);
        Assert.Equal(4, map[0]);
        Assert.Equal(3, map[1]);
    }

    [Fact]
    public void Unclosed_repeat_maps_to_end_of_sequence()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat },
            new() { Kind = ActionKind.Wait },
        };
        var map = ControlFlow.BuildBlockMap(actions);
        Assert.Equal(2, map[0]);
    }

    [Fact]
    public void Stray_end_block_is_ignored()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.EndBlock },
            new() { Kind = ActionKind.Wait },
        };
        Assert.Empty(ControlFlow.BuildBlockMap(actions));
    }

    [Fact]
    public void Label_map_first_occurrence_wins_and_skips_empties()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Label, Label = "farm" },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.Label, Label = "farm" },
            new() { Kind = ActionKind.Label, Label = "  " },
        };
        var map = ControlFlow.BuildLabelMap(actions);
        Assert.Equal(0, map["farm"]);
        Assert.Single(map);
    }

    [Fact]
    public void Depth_indents_bodies_and_closes_at_the_opener_level()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat },   // 0
            new() { Kind = ActionKind.Click },    // 1
            new() { Kind = ActionKind.EndBlock }, // 2
            new() { Kind = ActionKind.Click },    // 3
        };
        var depth = ControlFlow.BuildDepth(actions);
        Assert.Equal(new[] { 0, 1, 0, 0 }, depth);
    }

    [Fact]
    public void Stray_end_block_does_not_drive_depth_negative()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.EndBlock },
            new() { Kind = ActionKind.Click },
        };
        Assert.Equal(new[] { 0, 0 }, ControlFlow.BuildDepth(actions));
    }

    [Fact]
    public void Depth_aligns_else_with_its_if_opener()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse },   // 0 depth 0
            new() { Kind = ActionKind.Click },    // 1 depth 1
            new() { Kind = ActionKind.Else },     // 2 depth 0 (same level as If)
            new() { Kind = ActionKind.Click },    // 3 depth 1 (else body)
            new() { Kind = ActionKind.EndBlock }, // 4 depth 0
        };
        Assert.Equal(new[] { 0, 1, 0, 1, 0 }, ControlFlow.BuildDepth(actions));
    }

    // ---- Runner semantics (non-input actions only) ----

    private static List<int> Run(List<SeqAction> actions, int maxSteps = 1000)
    {
        var visited = new List<int>();
        int completed = 0;
        var runner = new SequenceRunner(
            keepGoing: () => completed < maxSteps,
            onStep: (idx, _) =>
            {
                if (idx < 0) return;
                visited.Add(idx);
                completed++;
            });
        runner.RunSequence(actions, new RunOptions { Limited = true, Limit = 1 });
        return visited;
    }

    [Fact]
    public void Repeat_runs_its_body_the_requested_number_of_times()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat, RepeatCount = 3 },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.EndBlock },
        };
        Assert.Equal(new[] { 0, 1, 2, 1, 2, 1, 2 }, Run(actions));
    }

    [Fact]
    public void Break_exits_the_innermost_repeat()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat, RepeatCount = 0 },
            new() { Kind = ActionKind.Break },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.EndBlock },
        };
        // The Break fires before the body's Wait ever runs: only header and Break are visited.
        Assert.Equal(new[] { 0, 1 }, Run(actions));
    }

    [Fact]
    public void Break_outside_a_repeat_throws()
    {
        var actions = new List<SeqAction> { new() { Kind = ActionKind.Break } };
        var runner = new SequenceRunner(() => true);
        Assert.Throws<InvalidOperationException>(
            () => runner.RunSequence(actions, new RunOptions { Limited = true, Limit = 1 }));
    }

    [Fact]
    public void Goto_jumps_to_the_label_and_skips_between()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.GotoLabel, Label = "farm" },
            new() { Kind = ActionKind.Wait },              // skipped
            new() { Kind = ActionKind.Label, Label = "farm" },
            new() { Kind = ActionKind.Wait },
        };
        Assert.Equal(new[] { 0, 2, 3 }, Run(actions));
    }

    [Fact]
    public void Goto_to_an_unknown_label_throws()
    {
        var actions = new List<SeqAction> { new() { Kind = ActionKind.GotoLabel, Label = "nowhere" } };
        var runner = new SequenceRunner(() => true);
        var ex = Assert.Throws<InvalidOperationException>(
            () => runner.RunSequence(actions, new RunOptions { Limited = true, Limit = 1 }));
        Assert.Contains("nowhere", ex.Message);
    }

    [Fact]
    public void IfElse_runs_the_block_when_true_and_skips_it_when_false()
    {
        var runTrue = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse, ConditionExpr = "1" },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.EndBlock },
        };
        Assert.Equal(new[] { 0, 1, 2 }, Run(runTrue));

        var runFalse = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse, ConditionExpr = "0" },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.EndBlock },
        };
        // False jumps PAST the matching EndBlock, so only the header is visited.
        Assert.Equal(new[] { 0 }, Run(runFalse));
    }

    [Fact]
    public void SetVar_persists_across_actions_and_drives_a_later_condition()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.SetVar, VarName = "counter", ValueExpr = "0" },
            new() { Kind = ActionKind.Repeat, RepeatCount = 3 },
            new() { Kind = ActionKind.SetVar, VarName = "counter", ValueExpr = "counter + 1" },
            new() { Kind = ActionKind.EndBlock },
            new() { Kind = ActionKind.IfElse, ConditionExpr = "counter == 3" },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.EndBlock },
        };
        // Indices: 0=SetVar, 1=Repeat, 2=SetVar(body), 3=EndBlock, 4=IfElse, 5=Wait, 6=EndBlock.
        // The Wait at 5 is reached only because counter == 3 holds after the loop.
        var visited = Run(actions);
        Assert.Contains(5, visited);
    }

    [Fact]
    public void Builtin_pass_var_is_zero_on_the_first_pass_and_grows()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse, ConditionExpr = "pass < 1" },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.EndBlock },
        };

        int waitHits = 0;
        var runner = new SequenceRunner(() => true, onStep: (idx, _) => { if (idx == 1) waitHits++; });
        runner.RunSequence(actions, new RunOptions { Limited = true, Limit = 2 });
        Assert.Equal(1, waitHits); // pass 0 runs the body, pass 1 skips it
    }

    [Fact]
    public void Infinite_repeat_stops_when_keep_going_goes_false()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat, RepeatCount = 0 },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.EndBlock },
        };
        // keepGoing is flipped off after five executed actions; the loop must not hang.
        var visited = Run(actions, maxSteps: 5);
        Assert.Equal(5, visited.Count);
    }

    // ---- Else pairing (pure) ----

    [Fact]
    public void Else_map_pairs_else_with_the_innermost_open_if()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse },   // 0
            new() { Kind = ActionKind.Wait },     // 1
            new() { Kind = ActionKind.IfElse },   // 2
            new() { Kind = ActionKind.Wait },     // 3
            new() { Kind = ActionKind.EndBlock }, // 4 closes If 2
            new() { Kind = ActionKind.Else },     // 5 attaches to If 0, not the closed If 2
            new() { Kind = ActionKind.Wait },     // 6
            new() { Kind = ActionKind.EndBlock }, // 7 closes If 0
        };
        var map = ControlFlow.BuildElseMap(actions);
        Assert.Single(map);
        Assert.Equal(5, map[0]);
    }

    [Fact]
    public void Else_map_ignores_a_stray_else()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Else },
            new() { Kind = ActionKind.Wait },
        };
        Assert.Empty(ControlFlow.BuildElseMap(actions));
    }

    [Fact]
    public void Else_after_the_if_is_closed_is_stray()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse },   // 0
            new() { Kind = ActionKind.Wait },     // 1
            new() { Kind = ActionKind.EndBlock }, // 2 closes If 0
            new() { Kind = ActionKind.Else },     // 3 stray — nothing open
        };
        Assert.Empty(ControlFlow.BuildElseMap(actions));
    }

    // ---- Else semantics (runner) ----

    [Fact]
    public void IfElse_with_else_runs_the_else_body_when_false()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse, ConditionExpr = "0" }, // 0
            new() { Kind = ActionKind.Wait },                       // 1 (true body)
            new() { Kind = ActionKind.Else },                       // 2
            new() { Kind = ActionKind.Wait },                       // 3 (else body)
            new() { Kind = ActionKind.EndBlock },                   // 4
        };
        // False enters the else body: If, else Wait, EndBlock — the true body is skipped.
        Assert.Equal(new[] { 0, 3, 4 }, Run(actions));
    }

    [Fact]
    public void IfElse_with_else_skips_the_else_body_when_true()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse, ConditionExpr = "1" }, // 0
            new() { Kind = ActionKind.Wait },                       // 1 (true body)
            new() { Kind = ActionKind.Else },                       // 2 — jump-over
            new() { Kind = ActionKind.Wait },                       // 3 (else body, skipped)
            new() { Kind = ActionKind.EndBlock },                   // 4 (skipped)
        };
        // True body completes, Else jumps past EndBlock: else body and EndBlock are skipped.
        Assert.Equal(new[] { 0, 1, 2 }, Run(actions));
    }

    [Fact]
    public void Nested_if_else_attaches_each_else_to_its_own_if()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse, ConditionExpr = "1" }, // 0 outer true
            new() { Kind = ActionKind.IfElse, ConditionExpr = "0" }, // 1 inner false
            new() { Kind = ActionKind.Wait },                       // 2 (inner true body)
            new() { Kind = ActionKind.Else },                       // 3 (inner else)
            new() { Kind = ActionKind.Wait },                       // 4 (inner else body)
            new() { Kind = ActionKind.EndBlock },                   // 5 closes inner
            new() { Kind = ActionKind.EndBlock },                   // 6 closes outer
        };
        // Inner is false: run its else body, close inner, close outer. Outer true body holds.
        Assert.Equal(new[] { 0, 1, 4, 5, 6 }, Run(actions));
    }

    [Fact]
    public void Stray_else_is_a_plain_noop()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Else }, // 0 — no opener, treated as a no-op
            new() { Kind = ActionKind.Wait }, // 1
        };
        Assert.Equal(new[] { 0, 1 }, Run(actions));
    }

    [Fact]
    public void Elseless_if_still_skips_past_end_block_when_false()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.IfElse, ConditionExpr = "0" }, // 0
            new() { Kind = ActionKind.Wait },                       // 1
            new() { Kind = ActionKind.EndBlock },                   // 2
        };
        // No Else marker: false still jumps past EndBlock, only the header is visited.
        Assert.Equal(new[] { 0 }, Run(actions));
    }
}
