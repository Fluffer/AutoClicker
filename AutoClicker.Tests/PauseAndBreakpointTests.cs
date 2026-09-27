using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Wave 2: true pause/resume + step debugging (F3) and the breakpoint action kind (F4).
/// The gate's permit math is pure (<see cref="RunController.ConsumePermit"/>) and is tested
/// directly; the runner's between-action blocking is exercised with a Wait-only sequence
/// and a gate delegate, so no real input is ever sent. RunController is driven headlessly
/// (synchronous marshal, no hotkeys) for the pause/step/breakpoint lifecycle.
/// </summary>
public class PauseAndBreakpointTests
{
    // ---- Pure gate math ----

    [Fact]
    public void ConsumePermit_blocks_without_a_permit()
    {
        int permit = 0;
        Assert.False(RunController.ConsumePermit(ref permit));
        Assert.Equal(0, permit);
    }

    [Fact]
    public void ConsumePermit_consumes_a_single_permit_then_blocks_again()
    {
        int permit = 1;
        Assert.True(RunController.ConsumePermit(ref permit)); // permit consumed
        Assert.Equal(0, permit);
        Assert.False(RunController.ConsumePermit(ref permit)); // no permit left
    }

    // ---- RunController: no-op guards ----

    [Fact]
    public void Pause_is_a_noop_when_not_running()
    {
        using var rc = NewController();
        rc.Pause();
        Assert.False(rc.IsPaused);
    }

    [Fact]
    public void StepOnce_is_a_noop_when_not_running()
    {
        using var rc = NewController();
        rc.StepOnce();
        Assert.False(rc.IsPaused);
    }

    // ---- Runner gate: blocking between actions ----

    [Fact]
    public async Task Runner_blocks_between_actions_until_a_permit_is_granted()
    {
        var waits = new List<SeqAction>
        {
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.Wait },
        };
        var executed = new List<int>();
        int executedCount = 0;
        int permit = 0;
        var runner = new SequenceRunner(
            keepGoing: () => true,
            onStep: (idx, done) =>
            {
                if (idx >= 0 && done)
                {
                    lock (executed) executed.Add(idx);
                    Interlocked.Increment(ref executedCount);
                }
            },
            canProceed: () => Volatile.Read(ref executedCount) == 0 || Interlocked.Exchange(ref permit, 0) == 1);

        var task = Task.Run(() => runner.RunSequence(waits, new RunOptions { Limited = true, Limit = 1 }));

        Assert.True(WaitUntil(() => Snapshot(executed).Count == 1), "action 0 did not run");
        Thread.Sleep(120); // window to prove it's blocked
        Assert.Equal(new[] { 0 }, Snapshot(executed));

        Interlocked.Exchange(ref permit, 1); // grant one permit
        Assert.True(WaitUntil(() => Snapshot(executed).Count == 2), "action 1 did not run after the permit");
        Thread.Sleep(120);
        Assert.Equal(new[] { 0, 1 }, Snapshot(executed)); // blocked again before action 2

        Interlocked.Exchange(ref permit, 1);
        await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 0, 1, 2 }, Snapshot(executed));
    }

    [Fact]
    public void Runner_gate_wait_wakes_when_keep_going_goes_false()
    {
        var waits = new List<SeqAction> { new() { Kind = ActionKind.Wait } };
        int executed = 0;
        int polls = 0;
        var runner = new SequenceRunner(
            keepGoing: () => Interlocked.Increment(ref polls) <= 50,
            onStep: (idx, done) => { if (idx >= 0 && done) Interlocked.Increment(ref executed); },
            canProceed: () => false); // gate permanently closed

        var sw = System.Diagnostics.Stopwatch.StartNew();
        runner.RunSequence(waits, new RunOptions { Limited = true, Limit = 1 });
        sw.Stop();

        Assert.Equal(0, Volatile.Read(ref executed)); // the closed gate kept the action from running
        Assert.True(Volatile.Read(ref polls) > 50, "keepGoing was not polled through to false");
        Assert.True(sw.ElapsedMilliseconds < 5000, "the gate wait did not wake on keepGoing=false");
    }

    // ---- RunController: pause / step lifecycle ----

    [Fact]
    public void Breakpoint_pauses_the_run_until_resumed()
    {
        using var rc = NewController();
        using var finished = new ManualResetEventSlim();
        rc.RunFinished += finished.Set;

        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.Breakpoint },
            new() { Kind = ActionKind.Wait },
        };
        Assert.True(rc.TryStart(SequenceSpec(actions)));

        Assert.True(WaitUntil(() => rc.IsPaused), "the run did not pause at the breakpoint");
        Assert.False(finished.Wait(0)); // still paused, not finished

        rc.Resume();
        Assert.True(finished.Wait(3000), "the run did not finish after resume");
        Assert.False(rc.IsPaused);
    }

    [Fact]
    public void StepOnce_runs_one_action_then_returns_to_paused()
    {
        using var rc = NewController();
        using var finished = new ManualResetEventSlim();
        rc.RunFinished += finished.Set;
        int completed = 0;
        rc.StepCompleted += (idx, _) => { if (idx >= 0) Interlocked.Increment(ref completed); };

        // Non-zero delays keep the run alive long enough to pause before it finishes.
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Wait, DelayMs = 30 },
            new() { Kind = ActionKind.Wait, DelayMs = 30 },
            new() { Kind = ActionKind.Wait, DelayMs = 30 },
        };
        Assert.True(rc.TryStart(SequenceSpec(actions)));

        rc.Pause();
        Assert.True(WaitUntil(() => rc.IsPaused), "the run did not pause");

        int before = Volatile.Read(ref completed);
        rc.StepOnce();
        Assert.True(WaitUntil(() => Volatile.Read(ref completed) == before + 1), "exactly one action did not run");
        Assert.True(rc.IsPaused, "the run did not re-pause after stepping");

        rc.Resume();
        Assert.True(finished.Wait(3000), "the run did not finish after resume");
    }

    [Fact]
    public void StepOnce_when_running_but_not_paused_is_a_noop()
    {
        using var rc = NewController();
        using var finished = new ManualResetEventSlim();
        rc.RunFinished += finished.Set;
        int completed = 0;
        rc.StepCompleted += (idx, _) => { if (idx >= 0) Interlocked.Increment(ref completed); };

        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Wait, DelayMs = 20 },
            new() { Kind = ActionKind.Wait, DelayMs = 20 },
            new() { Kind = ActionKind.Wait, DelayMs = 20 },
        };
        Assert.True(rc.TryStart(SequenceSpec(actions)));

        rc.StepOnce(); // running but not paused — must do nothing
        Assert.False(rc.IsPaused);
        Assert.True(finished.Wait(3000), "the run should complete without pausing");
        Assert.Equal(3, Volatile.Read(ref completed));
    }

    // ---- Breakpoint runner semantics + Describe ----

    [Fact]
    public void Breakpoint_is_a_noop_that_advances_the_instruction_pointer()
    {
        var actions = new List<SeqAction>
        {
            new() { Kind = ActionKind.Wait },
            new() { Kind = ActionKind.Breakpoint },
            new() { Kind = ActionKind.Wait },
        };
        var visited = new List<int>();
        var runner = new SequenceRunner(
            keepGoing: () => true,
            onStep: (idx, _) => { if (idx >= 0) visited.Add(idx); });
        runner.RunSequence(actions, new RunOptions { Limited = true, Limit = 1 });

        Assert.Equal(new[] { 0, 1, 2 }, visited);
    }

    [Fact]
    public void Describe_breakpoint_and_else()
    {
        Assert.Equal("Breakpoint", new SeqAction { Kind = ActionKind.Breakpoint }.Describe());
        Assert.Equal("Else", new SeqAction { Kind = ActionKind.Else }.Describe());
    }

    // ---- Helpers ----

    private static RunController NewController() => new(
        armPanic: () => true,
        disarmPanic: () => { },
        hotkeyNameProvider: () => "F6",
        panicKeyNameProvider: () => "Esc",
        uiMarshal: a => a());

    private static RunSpec SequenceSpec(List<SeqAction> actions) => new()
    {
        UseSequence = true,
        Actions = actions,
        Limited = true,
        Limit = 1,
    };

    private static List<int> Snapshot(List<int> executed)
    {
        lock (executed) return new List<int>(executed);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }
}
