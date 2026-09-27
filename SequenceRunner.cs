using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>Parameters for <see cref="SequenceRunner.RunSequence"/>.</summary>
internal sealed class RunOptions
{
    public bool Background { get; set; }
    public bool Limited { get; set; }
    public int Limit { get; set; } = 1;
    public int JitterPixels { get; set; }
    public int JitterPercent { get; set; }
}

/// <summary>Parameters for <see cref="SequenceRunner.RunSingle"/>.</summary>
internal sealed class SingleRunOptions
{
    public bool Hold { get; set; }
    public bool DoubleClick { get; set; }
    public int Button { get; set; }
    public bool UseFixedPosition { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int IntervalMs { get; set; }
    public bool Limited { get; set; }
    public int Limit { get; set; } = 1;
    public int JitterPixels { get; set; }
    public int JitterPercent { get; set; }
}

/// <summary>
/// The click/sequence engine — extracted out of the UI so it can be driven headlessly (a CLI
/// mode, or a test) without dragging WinForms along. Progress is reported only through
/// <see cref="onStep"/>; marshalling that callback to a UI thread, if one exists, is the
/// caller's job, not this class's. Exceptions from a run are never caught here — they
/// propagate to the caller, which decides how to report an abort. The class is designed to
/// be driven from one worker thread at a time; there is no internal locking.
/// </summary>
internal sealed class SequenceRunner
{
    private readonly Func<bool> keepGoing;
    private readonly Action<int, bool>? onStep;
    private readonly Action<int>? onStepStarting;

    /// <param name="keepGoing">Polled throughout a run; once it returns false the run unwinds promptly.</param>
    /// <param name="onStep">
    /// Invoked as each action is reached: (index, wasPerformed). index -1 means "clear any
    /// highlight" and fires once the sequence ends. May be null (headless).
    /// </param>
    /// <param name="onStepStarting">
    /// Invoked with an action's index immediately before it is attempted — this is what lets a
    /// caller highlight the step that's about to run, including one that then blocks for a
    /// while (a <see cref="ActionKind.WaitPixel"/> can sit here for its whole timeout). It
    /// fires on the same worker thread as <see cref="onStep"/>, but the two calls are not
    /// necessarily close together in time: a caller marshalling either callback to a UI thread
    /// must not assume they arrive back-to-back. May be null (headless).
    /// </param>
    public SequenceRunner(Func<bool> keepGoing, Action<int, bool>? onStep = null, Action<int>? onStepStarting = null)
    {
        this.keepGoing = keepGoing;
        this.onStep = onStep;
        this.onStepStarting = onStepStarting;
    }

    public void RunSingle(SingleRunOptions options)
    {
        using var timerRes = new TimerResolutionScope();

        if (options.UseFixedPosition) MoveToOrFail(options.X, options.Y);
        if (options.Hold)
        {
            InputSender.HoldUntil(options.Button, keepGoing);
            return;
        }

        int count = 0;
        while (keepGoing())
        {
            if (options.UseFixedPosition)
                MoveToOrFail(options.X + JitterPx(options.JitterPixels), options.Y + JitterPx(options.JitterPixels));
            InputSender.Click(options.Button, options.DoubleClick);
            count++;
            if (options.Limited && count >= options.Limit) break;
            InterruptibleSleep(JitterMs(options.IntervalMs, options.JitterPercent));
        }
    }

    public void RunSequence(IReadOnlyList<SeqAction> actions, RunOptions options)
    {
        using var timerRes = new TimerResolutionScope();
        try
        {
            int pass = 0;
            // Keyboard actions have no coordinates of their own; in background mode they
            // are posted to the window of the most recent positioned action.
            POINT lastTarget = CursorPos();

            while (keepGoing())
            {
                for (int i = 0; i < actions.Count && keepGoing(); i++)
                {
                    var a = actions[i];
                    onStepStarting?.Invoke(i);
                    bool performed = Execute(a, options.Background, options.JitterPixels, ref lastTarget);
                    onStep?.Invoke(i, performed);
                    InterruptibleSleep(JitterMs(a.DelayMs, options.JitterPercent));
                }
                pass++;
                if (options.Limited && pass >= options.Limit) break;
            }
        }
        finally
        {
            onStep?.Invoke(-1, true);
        }
    }

    /// <summary>
    /// Evaluates the pixel gate on an ordinary (non-<see cref="ActionKind.WaitPixel"/>) action.
    /// A failed sample — the point falls off every display — counts as "not matching", the same
    /// rule <see cref="PixelSampler.WaitUntil"/> applies.
    /// </summary>
    private static bool GateOpen(SeqAction a)
    {
        bool matches = PixelSampler.TrySample(a.CondX, a.CondY, out Color sampled)
                    && PixelSampler.Matches(sampled, a.CondColor, a.CondTolerance);
        return a.Condition == PixelCondition.IfMatch ? matches : !matches;
    }

    /// <summary>Performs one sequence step. Returns false when a pixel gate suppressed it.</summary>
    private bool Execute(SeqAction a, bool background, int jitterPx, ref POINT lastTarget)
    {
        // A gate only applies to ordinary actions — WaitPixel is itself the wait mechanism, so
        // its Condition means "what to wait for" instead (see SeqAction.Condition).
        if (a.Kind != ActionKind.WaitPixel && a.Condition != PixelCondition.None && !GateOpen(a))
        {
            // The caller still applies a.DelayMs after a skip: skipping the delay too would let
            // a run of gated-off actions spin the CPU at full speed waiting for their condition
            // to change, instead of idling at the configured pace like every other action does.
            return false;
        }

        if (a.Kind == ActionKind.WaitPixel)
        {
            if (a.Condition == PixelCondition.None) return true; // nothing to wait for

            bool ok = PixelSampler.WaitUntil(a.CondX, a.CondY, a.CondColor, a.CondTolerance,
                a.Condition == PixelCondition.IfMatch, a.PixelTimeoutMs, a.PollIntervalMs, keepGoing);

            // WaitUntil also returns false when the user stops the run mid-wait; only a real
            // timeout — the run is still going — should be reported as one.
            if (!ok && keepGoing() && a.AbortRunOnTimeout)
                throw new InvalidOperationException($"Timed out waiting for {a.DescribeColor()} at {a.CondX},{a.CondY}.");

            return true;
        }

        bool positioned = a.Kind is ActionKind.Click or ActionKind.Drag or ActionKind.Scroll;
        int x = a.X, y = a.Y, ex = a.EndX, ey = a.EndY;

        if (positioned && a.WindowRelative)
        {
            if (!WindowAnchor.Resolve(a, a.X, a.Y, out POINT sp))
                throw new InvalidOperationException($"Anchor window \"{a.WindowLabel}\" is not open.");
            x = sp.X;
            y = sp.Y;
            if (a.Kind == ActionKind.Drag && WindowAnchor.Resolve(a, a.EndX, a.EndY, out POINT ep))
            {
                ex = ep.X;
                ey = ep.Y;
            }
        }

        if (positioned && jitterPx > 0)
        {
            x += JitterPx(jitterPx);
            y += JitterPx(jitterPx);
            if (a.Kind == ActionKind.Drag) { ex += JitterPx(jitterPx); ey += JitterPx(jitterPx); }
        }

        if (positioned) lastTarget = new POINT { X = x, Y = y };

        int method = ResolveClickMethod(a.ClickMethod, background);

        switch (a.Kind)
        {
            case ActionKind.Click:
                if (method == 2)
                {
                    // Soft failure by design: TryInvokeAt returns false when there's simply no
                    // invokable element under the point (or COM misbehaved), which is not a
                    // reason to abort the run -- the action was still attempted, so it counts
                    // as performed either way. DoubleClick/Button/HoldMs are ignored here: an
                    // invoke activates a control rather than synthesizing a physical click, so a
                    // user who set "right double-click + UIA" would otherwise be quietly
                    // surprised that none of that took effect.
                    UiaInvoker.TryInvokeAt(x, y, out _);
                }
                // keepGoing is threaded through so a long HoldMs doesn't make Stop and the
                // panic key wait out the whole hold before taking effect.
                else if (method == 1) InputSender.BackgroundClick(x, y, a.Button, a.DoubleClick, a.HoldMs, keepGoing);
                else { MoveToOrFail(x, y); InputSender.Click(a.Button, a.DoubleClick, a.HoldMs, keepGoing); }
                break;

            case ActionKind.Drag:
                // UI Automation has no drag primitive -- "invoke a control" doesn't generalize to
                // a gesture -- so method 2 falls back to PostMessage rather than doing nothing.
                if (method != 0) InputSender.BackgroundDrag(x, y, ex, ey, a.Button, a.DragMs, keepGoing);
                else InputSender.Drag(x, y, ex, ey, a.Button, a.DragMs, keepGoing);
                break;

            case ActionKind.Scroll:
                // Same reasoning as Drag: no UIA equivalent for a wheel notch, so method 2 also
                // falls back to PostMessage.
                if (method != 0) InputSender.BackgroundScroll(x, y, a.ScrollNotches, a.Horizontal);
                else { MoveToOrFail(x, y); InputSender.Scroll(a.ScrollNotches, a.Horizontal); }
                break;

            case ActionKind.Key:
                if (background) InputSender.BackgroundCombo(lastTarget.X, lastTarget.Y, a.KeyCombo);
                else InputSender.SendCombo(a.KeyCombo);
                break;

            case ActionKind.Text:
                if (background) InputSender.BackgroundTypeText(lastTarget.X, lastTarget.Y, a.Text, keepGoing);
                else InputSender.TypeText(a.Text, keepGoing);
                break;

            case ActionKind.Wait:
                break; // the DelayMs after the action is the whole point
        }

        return true;
    }

    /// <summary>
    /// Picks the backend for a positioned action. A per-action <see cref="SeqAction.ClickMethod"/>
    /// other than 0 wins outright; left at 0, the run's global <see cref="RunOptions.Background"/>
    /// flag decides between real input and PostMessage, exactly as it always has. That fallback
    /// matters because every sequence saved before per-action methods existed has ClickMethod == 0
    /// on every action and depends on the global flag alone -- collapsing this to "per-action always
    /// wins" would silently strand those sequences on whichever backend 0 happens to mean. If UI
    /// Automation was requested but isn't available on this thread, fall back to real input so the
    /// run still does something useful instead of a run of no-ops.
    /// </summary>
    private static int ResolveClickMethod(int clickMethod, bool background)
    {
        int method = clickMethod != 0 ? clickMethod : (background ? 1 : 0);
        return method == 2 && !UiaInvoker.IsAvailable ? 0 : method;
    }

    private static POINT CursorPos() { GetCursorPos(out POINT p); return p; }

    /// <summary>
    /// Moves the real cursor, aborting the step when the move fails. An unchecked
    /// <see cref="SetCursorPos"/> failure means the click that follows lands wherever the
    /// cursor already was — silently clicking the wrong thing — so this throws the same
    /// way the SendInput path does when it's blocked.
    /// </summary>
    private static void MoveToOrFail(int x, int y)
    {
        if (!SetCursorPos(x, y))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Could not move the cursor to {x},{y} — the click would have landed at the wrong position.");
    }

    // Waits `ms`, but bails out early once `keepGoing` goes false.
    // Measured against a Stopwatch rather than by accumulating fixed steps: the old
    // "sleep 25 ms until you've slept enough" loop overshot every interval that wasn't
    // a multiple of 25 (a 30 ms interval actually waited 50 ms).
    private void InterruptibleSleep(int ms)
    {
        // 0 means "as fast as possible", but returning without ever sleeping pinned a core
        // at 100% for the whole run: floor at 1 ms so the loop still yields the CPU.
        if (ms < 1) ms = 1;
        var sw = Stopwatch.StartNew();
        while (keepGoing())
        {
            long left = ms - sw.ElapsedMilliseconds;
            if (left <= 0) return;
            if (left > 20) Thread.Sleep(10);   // coarse; keeps stop latency <= 10 ms
            else if (left > 2) Thread.Sleep(1);
            else Thread.SpinWait(100);         // final approach, Sleep is too granular here
        }
    }

    // ---- Humanize ----
    private static int JitterMs(int ms, int percent)
    {
        if (ms <= 0 || percent <= 0) return ms;
        double factor = 1 + ((Random.Shared.NextDouble() * 2) - 1) * percent / 100.0;
        return (int)Math.Max(0, Math.Round(ms * factor));
    }

    private static int JitterPx(int maxPx) => maxPx <= 0 ? 0 : Random.Shared.Next(-maxPx, maxPx + 1);
}
