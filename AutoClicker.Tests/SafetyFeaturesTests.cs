using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// F2 (Wave 1) safety batch: the max-actions watchdog and the stop-on-mouse-move threshold.
/// The watchdog's pure budget helper and the integration seam (a Wait-only sequence driven
/// through the runner's keepGoing) are tested here; the mouse-move hook itself is
/// best-effort Win32 and only its threshold math is unit-testable.
/// </summary>
public class SafetyFeaturesTests
{
    // ---- MaxActions watchdog: pure budget helper ----

    [Theory]
    [InlineData(0, 0, false)] // no budget
    [InlineData(5, 0, false)] // no budget
    [InlineData(0, 5, false)] // nothing performed
    [InlineData(4, 5, false)] // under budget
    [InlineData(5, 5, true)]  // exactly at budget
    [InlineData(6, 5, true)]  // over budget
    public void MaxActionsReached_only_when_a_positive_budget_is_hit(int performed, int max, bool expected) =>
        Assert.Equal(expected, RunController.MaxActionsReached(performed, max));

    // ---- MaxActions watchdog: integration (Wait-only sequence) ----

    [Fact]
    public void Runner_stops_after_n_performed_actions_with_keepGoing_budget()
    {
        var waits = Enumerable.Range(0, 10)
            .Select(_ => new SeqAction { Kind = ActionKind.Wait })
            .ToList();
        int performed = 0;
        var runner = new SequenceRunner(
            keepGoing: () => !RunController.MaxActionsReached(performed, 3),
            onStep: (idx, done) => { if (idx >= 0 && done) performed++; });
        runner.RunSequence(waits, new RunOptions { Limited = true, Limit = 1 });

        Assert.Equal(3, performed);
    }

    // ---- StopOnUserMouseMove: threshold math ----

    [Theory]
    [InlineData(0, 0, 10, 10, 15, false)]      // ~14 px < 15
    [InlineData(0, 0, 15, 0, 15, false)]       // exactly 15 px does not count
    [InlineData(0, 0, 16, 0, 15, true)]        // 16 px counts
    [InlineData(0, 0, 0, 0, 15, false)]        // no movement
    [InlineData(100, 100, 100, 80, 15, true)]  // 20 px up
    [InlineData(100, 100, 88, 91, 15, false)]  // ~15 px diagonal (squared: 12²+9²=225, not >225)
    public void MovedBeyondThreshold_uses_squared_distance(int x1, int y1, int x2, int y2, int threshold, bool expected) =>
        Assert.Equal(expected, MouseMoveWatcher.MovedBeyondThreshold(x1, y1, x2, y2, threshold));
}
