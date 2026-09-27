using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// v2.1 engine features: playback speed scaling, per-action delay randomization, run
/// selection slicing, and the new window/process expression built-ins.
/// </summary>
public class PlaybackSpeedAndSelectionTests
{
    // ---- Speed scaling (SequenceRunner.Spd) ----

    [Theory]
    [InlineData(1000, 200, 500)] // 2×: half the wait
    [InlineData(1000, 50, 2000)] // ½×: double the wait
    [InlineData(1000, 100, 1000)] // 1×: unchanged
    [InlineData(0, 200, 0)]       // indefinite stays indefinite
    [InlineData(0, 100, 0)]
    public void Spd_scales_durations_by_percentage(int ms, int pct, int expected) =>
        Assert.Equal(expected, SequenceRunner.Spd(ms, pct));

    [Fact]
    public void Spd_leaves_non_positive_durations_untouched() =>
        Assert.Equal(-5, SequenceRunner.Spd(-5, 200));

    // ---- Per-action delay randomization ----

    [Theory]
    [InlineData(50, 10, 50)] // the action's own percent wins
    [InlineData(0, 10, 10)]  // no override: the global percent applies
    [InlineData(100, 0, 100)] // a set override beats a zero global
    [InlineData(0, 0, 0)]
    public void DelayJitterPercent_action_override_beats_global(int actionPct, int globalPct, int expected) =>
        Assert.Equal(expected, SequenceRunner.DelayJitterPercent(new SeqAction { DelayRandomPercent = actionPct }, globalPct));

    [Fact]
    public void EffectiveDelayMs_with_no_jitter_anywhere_is_exact()
    {
        var a = new SeqAction { DelayMs = 1234, DelayRandomPercent = 0 };
        Assert.Equal(1234, SequenceRunner.EffectiveDelayMs(a, 0));
    }

    [Fact]
    public void EffectiveDelayMs_with_full_percent_stays_within_double_bounds()
    {
        var a = new SeqAction { DelayMs = 1000, DelayRandomPercent = 100 };
        int result = SequenceRunner.EffectiveDelayMs(a, 0);
        Assert.InRange(result, 0, 2000);
    }

    [Fact]
    public void EffectiveDelayMs_zero_delay_never_jitters() =>
        Assert.Equal(0, SequenceRunner.EffectiveDelayMs(new SeqAction { DelayMs = 0, DelayRandomPercent = 100 }, 0));

    // ---- Run selection slicing (SequenceRunner.Slice) ----

    private static List<SeqAction> MakeActions(int n)
    {
        var list = new List<SeqAction>(n);
        for (int i = 0; i < n; i++) list.Add(new SeqAction { X = i });
        return list;
    }

    [Fact]
    public void Slice_mid_range_returns_inclusive_bounds()
    {
        var actions = MakeActions(10);
        var slice = SequenceRunner.Slice(actions, 2, 5);
        Assert.Equal(new[] { 2, 3, 4, 5 }, slice.Select(a => a.X));
    }

    [Fact]
    public void Slice_negative_end_means_to_the_end()
    {
        var actions = MakeActions(10);
        var slice = SequenceRunner.Slice(actions, 7, -1);
        Assert.Equal(new[] { 7, 8, 9 }, slice.Select(a => a.X));
    }

    [Fact]
    public void Slice_full_range_is_everything()
    {
        var actions = MakeActions(4);
        var slice = SequenceRunner.Slice(actions, 0, -1);
        Assert.Equal(4, slice.Count);
    }

    [Fact]
    public void Slice_clamps_out_of_range_and_inverted_bounds()
    {
        var actions = MakeActions(5);
        Assert.Equal(5, SequenceRunner.Slice(actions, -3, 99).Count);
        Assert.Empty(SequenceRunner.Slice(actions, 3, 1));      // inverted
        Assert.Empty(SequenceRunner.Slice(new List<SeqAction>(), 0, -1)); // empty source
    }

    // ---- Expression built-ins ----

    private static double Num(string expr) => ExpressionEvaluator.Evaluate(expr).Number;

    [Fact]
    public void Process_running_accepts_name_with_or_without_exe()
    {
        // Explorer is the shell; it is always running on an interactive Windows session.
        Assert.Equal(1, Num("process_running(\"explorer\")"));
        Assert.Equal(1, Num("process_running(\"explorer.exe\")"));
    }

    [Fact]
    public void Window_exists_finds_no_such_window()
    {
        Assert.Equal(0, Num("window_exists(\"definitely-no-such-window-xyz\")"));
    }

    [Fact]
    public void Builtins_compose_with_connectives_and_comparisons()
    {
        // process_running is true, so a negation of it is false.
        Assert.Equal(0, Num("process_running(\"explorer\") && 0"));
        Assert.Equal(1, Num("process_running(\"explorer\") == 1"));
        Assert.Equal(1, Num("window_exists(\"definitely-no-such-window-xyz\") == 0"));
        Assert.Equal(0, Num("window_exists(\"definitely-no-such-window-xyz\") && process_running(\"explorer\")"));
        // A falsy left operand must not short-circuit the parse of the right one.
        Assert.Equal(0, Num("0 && process_running(\"explorer\")"));
        Assert.Equal(1, Num("0 || process_running(\"explorer\")"));
    }

    [Fact]
    public void Window_exists_with_a_number_argument_throws_a_clear_error()
    {
        var ex = Assert.Throws<ExpressionException>(() => Num("window_exists(5)"));
        Assert.Contains("string", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Process_running_empty_string_is_not_running() =>
        Assert.Equal(0, Num("process_running(\"\")"));
}
