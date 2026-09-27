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

    /// <summary>Stop the run when the cursor is parked in a screen corner (opt-in fail-safe).</summary>
    public bool CornerFailSafe { get; set; }
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
    private readonly Action? onPassComplete;

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
    /// <param name="onPassComplete">
    /// Invoked after each full sequence pass finishes (the instruction pointer reached the
    /// end of the list on its own). Not invoked when a pass is cut short by
    /// <paramref name="keepGoing"/> turning false. May be null (headless).
    /// </param>
    public SequenceRunner(Func<bool> keepGoing, Action<int, bool>? onStep = null,
        Action<int>? onStepStarting = null, Action? onPassComplete = null)
    {
        this.keepGoing = keepGoing;
        this.onStep = onStep;
        this.onStepStarting = onStepStarting;
        this.onPassComplete = onPassComplete;
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

            // Block/label structure is built once per run: it depends only on the action
            // list, which the caller hands us as a snapshot. Variables are run-scoped too,
            // so a SetVar persists across passes (while built-ins like `pass` are rewritten
            // at the top of every pass).
            var ctx = new RunContext(actions);

            while (keepGoing())
            {
                ctx.Variables["pass"] = VarValue.FromNumber(pass);
                var cursor = CursorPos();
                ctx.Variables["x"] = VarValue.FromNumber(cursor.X);
                ctx.Variables["y"] = VarValue.FromNumber(cursor.Y);

                int ip = 0;
                while (ip < actions.Count && keepGoing())
                {
                    int stepIp = ip;
                    var a = actions[stepIp];
                    // Corner fail-safe: checked before each foreground mouse action. The
                    // cursor only moves under our control during foreground clicks/drags/
                    // scrolls, so those are the only kinds where a user parking it in a
                    // corner can mean "stop" — and a background run never moves the cursor.
                    if (options.CornerFailSafe && !options.Background
                        && a.Kind is ActionKind.Click or ActionKind.Drag or ActionKind.Scroll)
                        CheckCornerFailSafe();
                    onStepStarting?.Invoke(stepIp);
                    bool performed = ExecuteStep(a, options, ref lastTarget, ctx, ref ip);
                    onStep?.Invoke(stepIp, performed);
                    // DelayMs applies after every action, control-flow included, so a tight
                    // loop still idles at the configured pace instead of spinning the CPU.
                    InterruptibleSleep(JitterMs(a.DelayMs, options.JitterPercent));
                }

                if (ip >= actions.Count) onPassComplete?.Invoke();
                pass++;
                if (options.Limited && pass >= options.Limit) break;
            }
        }
        finally
        {
            onStep?.Invoke(-1, true);
        }
    }

    /// <summary>Run-scoped control-flow state: block/label maps, variables, open repeat frames.</summary>
    private sealed class RunContext
    {
        public IReadOnlyList<SeqAction> Actions { get; }
        public Dictionary<int, int> BlockMap { get; }
        public Dictionary<string, int> LabelMap { get; }
        public Dictionary<string, VarValue> Variables { get; }
        public Stack<RepeatFrame> Frames { get; } = new();

        /// <summary>
        /// Set once a foreground click's SendInput has been refused (elevated target window).
        /// Sticky for the rest of the run: every later foreground click then goes straight to
        /// PostMessage instead of re-throwing the same way per action.
        /// </summary>
        public bool SendInputBlocked { get; set; }

        public RunContext(IReadOnlyList<SeqAction> actions)
        {
            Actions = actions;
            BlockMap = ControlFlow.BuildBlockMap(actions);
            LabelMap = ControlFlow.BuildLabelMap(actions);
            Variables = new Dictionary<string, VarValue>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>One open <see cref="ActionKind.Repeat"/> block: where it starts, where it ends, and its progress.</summary>
    private sealed class RepeatFrame
    {
        public int RepeatIp { get; }
        public int CloseIp { get; }
        /// <summary>0 = until Break/stop.</summary>
        public int Limit { get; }
        public int Count { get; set; }

        public RepeatFrame(int repeatIp, int closeIp, int limit)
        {
            RepeatIp = repeatIp;
            CloseIp = closeIp;
            Limit = limit;
            Count = 0;
        }
    }

    /// <summary>
    /// Executes one action and advances <paramref name="ip"/> past it. Control-flow kinds
    /// (Repeat/EndBlock/IfElse/SetVar/Break/GotoLabel/Label) are handled here — they move
    /// the instruction pointer instead of sending input, and they ignore pixel gates by
    /// never reaching <see cref="Execute"/>. Everything else delegates to
    /// <see cref="Execute"/>, which returns false when a pixel gate suppressed the action.
    /// </summary>
    private bool ExecuteStep(SeqAction a, RunOptions options, ref POINT lastTarget, RunContext ctx, ref int ip)
    {
        switch (a.Kind)
        {
            case ActionKind.Repeat:
            {
                int close = ctx.BlockMap.TryGetValue(ip, out int c) ? c : ctx.Actions.Count;
                ctx.Frames.Push(new RepeatFrame(ip, close, a.RepeatCount));
                ip++;
                return true;
            }

            case ActionKind.EndBlock:
            {
                if (ctx.Frames.Count > 0 && ctx.Frames.Peek().CloseIp == ip)
                {
                    // This EndBlock closes the innermost Repeat: count the iteration.
                    var frame = ctx.Frames.Pop();
                    frame.Count++;
                    if (frame.Limit > 0 && frame.Count >= frame.Limit)
                        ip = frame.CloseIp + 1;
                    else
                    {
                        ctx.Frames.Push(frame);
                        ip = frame.RepeatIp + 1;
                    }
                }
                else
                {
                    // Closes an IfElse (nothing to restore), or is a stray EndBlock: fall through.
                    ip++;
                }
                return true;
            }

            case ActionKind.IfElse:
            {
                bool condition = EvaluateCondition(a, ctx);
                if (condition) ip++;
                else ip = (ctx.BlockMap.TryGetValue(ip, out int close) ? close : ctx.Actions.Count) + 1;
                return true;
            }

            case ActionKind.SetVar:
            {
                ctx.Variables[a.VarName.Trim()] = ExpressionEvaluator.Evaluate(a.ValueExpr, ctx.Variables);
                ip++;
                return true;
            }

            case ActionKind.Break:
            {
                if (ctx.Frames.Count == 0)
                    throw new InvalidOperationException("Break is not inside a Repeat block.");
                var frame = ctx.Frames.Pop();
                ip = frame.CloseIp + 1;
                return true;
            }

            case ActionKind.GotoLabel:
            {
                string label = a.Label?.Trim() ?? "";
                if (!ctx.LabelMap.TryGetValue(label, out int target))
                    throw new InvalidOperationException($"Goto target '{label}' was not found in the sequence.");
                ip = target;
                return true;
            }

            case ActionKind.Label:
                ip++;
                return true;

            default:
                ip++;
                // Humanization is on when the user asked for either kind of jitter; the
                // glide path itself is randomized independently of the jitter magnitude.
                bool humanize = options.JitterPixels > 0 || options.JitterPercent > 0;
                return Execute(a, options.Background, options.JitterPixels, humanize, ref lastTarget, ctx);
        }
    }

    private static bool EvaluateCondition(SeqAction a, RunContext ctx)
    {
        if (string.IsNullOrWhiteSpace(a.ConditionExpr))
            throw new InvalidOperationException("IfElse has no condition expression.");
        return ExpressionEvaluator.IsTruthy(ExpressionEvaluator.Evaluate(a.ConditionExpr, ctx.Variables));
    }

    /// <summary>
    /// Evaluates the pixel gate on an ordinary (non-wait-kind) action. WaitPixel/FindImage/
    /// FindText never reach this: their own success criterion replaces the gate.
    /// A failed sample — the point falls off every display — counts as "not matching", the same
    /// rule <see cref="PixelSampler.WaitUntil"/> applies.
    /// </summary>
    private static bool GateOpen(SeqAction a)
    {
        bool matches = PixelSampler.TrySample(a.CondX, a.CondY, out Color sampled)
                    && PixelSampler.Matches(sampled, a.CondColor, a.CondTolerance);
        return a.Condition == PixelCondition.IfMatch ? matches : !matches;
    }

    /// <summary>
    /// Performs one ordinary sequence step. Returns false when a pixel gate suppressed it.
    /// Control-flow kinds never reach here — <see cref="ExecuteStep"/> handles them before
    /// delegating — so this method only ever sees input kinds and the wait kinds (WaitPixel,
    /// FindImage, FindText).
    /// </summary>
    private bool Execute(SeqAction a, bool background, int jitterPx, bool humanize, ref POINT lastTarget, RunContext ctx)
    {
        // A gate only applies to ordinary actions — the wait kinds are themselves the wait
        // mechanism, so their Condition means "what to wait for" instead (see SeqAction.Condition).
        if (!SeqAction.IsWaitKind(a.Kind) && a.Condition != PixelCondition.None && !GateOpen(a))
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

        if (a.Kind is ActionKind.FindImage or ActionKind.FindText)
            return ExecuteVisualFind(a, background, jitterPx, humanize, ref lastTarget, ctx);

        // Self-healing playback (opt-in via PreferSelector): a Click that recorded a UIA
        // selector resolves through it first — the most semantic target available — so a
        // matched selector still wins when the window moved or the recorded point drifted.
        // This mirrors SelectorResolver.ChooseResolution's SelectorInvoke outcome; a miss
        // falls through to the coordinate path below, exactly as it behaved before.
        if (a.Kind == ActionKind.Click && SelectorResolver.HasSelector(a)
            && UiaInvoker.TryInvokeSelector(a.SelAutomationId, a.SelName, a.SelClass, out _))
        {
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
                DispatchClick(a, x, y, method, humanize, ctx);
                break;

            case ActionKind.Drag:
                // UI Automation has no drag primitive -- "invoke a control" doesn't generalize to
                // a gesture -- so method 2 falls back to PostMessage rather than doing nothing.
                if (method != 0) InputSender.BackgroundDrag(x, y, ex, ey, a.Button, a.DragMs, keepGoing);
                else InputSender.Drag(x, y, ex, ey, a.Button, a.DragMs, keepGoing, jitterPx);
                break;

            case ActionKind.Scroll:
                // Same reasoning as Drag: no UIA equivalent for a wheel notch, so method 2 also
                // falls back to PostMessage.
                if (method != 0) InputSender.BackgroundScroll(x, y, a.ScrollNotches, a.Horizontal);
                else { MoveCursorTo(x, y, humanize); InputSender.Scroll(a.ScrollNotches, a.Horizontal); }
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
    /// Performs the click for a <see cref="ActionKind.Click"/> action at resolved coordinates.
    /// Shared by <see cref="Execute"/> and <see cref="ExecuteVisualFind"/> so a FindImage/
    /// FindText click-on-found goes through the identical backend resolution (real input,
    /// background messages, or UI Automation) as an ordinary click.
    /// </summary>
    private void DispatchClick(SeqAction a, int x, int y, int method, bool humanize, RunContext ctx)
    {
        // Sticky SendInput-blocked memory (see RunContext.SendInputBlocked): once a
        // SendInput click has been refused by an elevated foreground window, every later
        // foreground click goes straight to PostMessage. Retrying SendInput per action
        // would just re-block and throw on every single step of the run.
        if (ctx.SendInputBlocked && method == 0) method = 1;

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
        else
        {
            MoveCursorTo(x, y, humanize);
            try
            {
                InputSender.Click(a.Button, a.DoubleClick, a.HoldMs, keepGoing);
            }
            catch (Win32Exception)
            {
                // SendInput was blocked (the target window is probably elevated). Fall
                // back one rung down the ladder to PostMessage for this click and
                // remember it for the rest of the run — the fallback doesn't throw.
                ctx.SendInputBlocked = true;
                if (NextBackendOnFailure(0) == 1)
                    InputSender.BackgroundClick(x, y, a.Button, a.DoubleClick, a.HoldMs, keepGoing);
            }
        }
    }

    /// <summary>
    /// The wait-and-resolve loop behind <see cref="ActionKind.FindImage"/> and
    /// <see cref="ActionKind.FindText"/>. Captures the search region, runs the lookup, and on
    /// success stores the found center into the run-scoped variables <c>found.x</c>/
    /// <c>found.y</c>/<c>found.score</c> (readable by later SetVar/IfElse steps) and optionally
    /// clicks it. On timeout it mirrors <see cref="ActionKind.WaitPixel"/>: abort the run when
    /// <see cref="SeqAction.AbortRunOnTimeout"/> is set, otherwise report not-performed like a
    /// failed gate.
    /// </summary>
    private bool ExecuteVisualFind(SeqAction a, bool background, int jitterPx, bool humanize, ref POINT lastTarget, RunContext ctx)
    {
        // Decode the template once, before the wait, so a corrupt PNG fails immediately
        // instead of after the whole timeout has elapsed.
        using Bitmap? template = a.Kind == ActionKind.FindImage ? DecodeTemplate(a.TemplatePng) : null;

        Rectangle region = ResolveSearchRegion(a);
        var sw = Stopwatch.StartNew();

        while (keepGoing())
        {
            Point? found = null;
            double score = 0;
            using (Bitmap screen = ImageMatcher.CaptureRegion(region))
            {
                if (a.Kind == ActionKind.FindImage)
                {
                    if (ImageMatcher.TryFind(screen, template!, a.MatchThreshold, out Point topLeft, out score))
                        found = topLeft;
                }
                else if (OcrTextReader.TryFind(screen, a.TextQuery, a.RegexQuery, out Rectangle box, out score))
                {
                    // OCR returns the matched word/line box; the runner works in centers.
                    found = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
                }
            }

            if (found is Point p)
            {
                // found.x/y are the raw found center (no offset or jitter), so a later
                // SetVar/IfElse can reason about where the target actually was.
                ctx.Variables["found.x"] = VarValue.FromNumber(p.X);
                ctx.Variables["found.y"] = VarValue.FromNumber(p.Y);
                ctx.Variables["found.score"] = VarValue.FromNumber(score);

                if (a.ClickOnFound)
                {
                    int cx = p.X + a.ClickOffsetX;
                    int cy = p.Y + a.ClickOffsetY;
                    if (jitterPx > 0) { cx += JitterPx(jitterPx); cy += JitterPx(jitterPx); }
                    lastTarget = new POINT { X = cx, Y = cy };
                    DispatchClick(a, cx, cy, ResolveClickMethod(a.ClickMethod, background), humanize, ctx);
                }
                return true;
            }

            if (a.PixelTimeoutMs > 0 && sw.ElapsedMilliseconds >= a.PixelTimeoutMs)
            {
                if (a.AbortRunOnTimeout)
                {
                    string what = a.Kind == ActionKind.FindImage
                        ? "the image"
                        : $"text \"{a.TextQuery}\"";
                    throw new InvalidOperationException($"Timed out finding {what} in {a.DescribeSearchRegion()}.");
                }
                return false; // not performed, like a failed gate
            }

            Thread.Sleep(Math.Max(a.PollIntervalMs, 10));
        }

        return false; // stopped mid-wait
    }

    private static Bitmap DecodeTemplate(byte[]? png)
    {
        if (png is null || png.Length == 0)
            throw new InvalidOperationException("FindImage has no template image — capture or load one in the editor.");
        try
        {
            return new Bitmap(new MemoryStream(png));
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            throw new InvalidOperationException("The FindImage template is not a readable image: " + ex.Message);
        }
    }

    private static Rectangle ResolveSearchRegion(SeqAction a) =>
        a.SearchW <= 0 || a.SearchH <= 0
            ? SystemInformation.VirtualScreen
            : new Rectangle(a.SearchX, a.SearchY, a.SearchW, a.SearchH);

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

    /// <summary>
    /// The backend to try next when a click on <paramref name="method"/> fails. The ladder
    /// runs 2 (UI Automation) → 1 (PostMessage) → 0 (SendInput), and a blocked SendInput
    /// wraps back to 1: each rung falls back toward the least fussy option that still does
    /// something. Today only the 0 → 1 rung is exercised in code — it is the only failure
    /// that surfaces as a <see cref="Win32Exception"/>. Extracted as a pure function so the
    /// policy is unit-testable without driving a real SendInput failure.
    /// </summary>
    internal static int NextBackendOnFailure(int method) => method switch
    {
        2 => 1,
        1 => 0,
        _ => 1,
    };

    private static POINT CursorPos() { GetCursorPos(out POINT p); return p; }

    /// <summary>
    /// Throws <see cref="FailSafeException"/> when the cursor is within a few pixels of any
    /// screen corner — the classic "slam the mouse into a corner to stop everything"
    /// fail-safe. One GetCursorPos P/Invoke per foreground mouse action, deliberately cheap.
    /// </summary>
    private static void CheckCornerFailSafe()
    {
        const int margin = 4;
        GetCursorPos(out POINT p);
        var s = SystemInformation.VirtualScreen;

        bool inCorner =
            (p.X <= s.Left + margin && p.Y <= s.Top + margin) ||
            (p.X <= s.Left + margin && p.Y >= s.Bottom - margin) ||
            (p.X >= s.Right - margin && p.Y <= s.Top + margin) ||
            (p.X >= s.Right - margin && p.Y >= s.Bottom - margin);

        if (inCorner)
            throw new FailSafeException("Stopped by corner fail-safe.");
    }

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

    /// <summary>
    /// Moves the real cursor to (x,y): glides along a natural path when humanization is on,
    /// and teleports (the pre-humanization behavior) otherwise.
    /// </summary>
    private void MoveCursorTo(int x, int y, bool glide)
    {
        if (glide) GlideCursorTo(x, y);
        else MoveToOrFail(x, y);
    }

    /// <summary>
    /// Glides the cursor from its current position to (x,y) along a WindMouse-style path.
    /// The glide is short (distance-derived duration, 20-120 ms) and interruptible, so a
    /// Stop or panic never waits it out. The final position is snapped exactly to (x,y)
    /// and checked, mirroring <see cref="MoveToOrFail"/>'s failure semantics.
    /// </summary>
    private void GlideCursorTo(int x, int y)
    {
        GetCursorPos(out POINT cur);
        if (cur.X == x && cur.Y == y) return;

        var from = new Point(cur.X, cur.Y);
        var to = new Point(x, y);
        int durationMs = Math.Clamp(Distance(from, to) / 2, 20, 120);

        var pts = MousePathGenerator.Path(from, to, Random.Shared).ToList();
        if (pts.Count < 2) { MoveToOrFail(x, y); return; }

        int stepMs = durationMs / (pts.Count - 1);
        for (int i = 1; i < pts.Count - 1; i++)
        {
            if (!keepGoing()) return; // stopped: the run is ending, so don't keep moving
            SetCursorPos(pts[i].X, pts[i].Y); // mid-glide moves are best-effort, like Drag's
            if (stepMs > 0) InterruptibleSleep(stepMs);
        }

        if (!SetCursorPos(x, y))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Could not move the cursor to {x},{y} — the click would have landed at the wrong position.");
    }

    private static int Distance(Point a, Point b) =>
        (int)Math.Round(Math.Sqrt((long)(b.X - a.X) * (b.X - a.X) + (long)(b.Y - a.Y) * (b.Y - a.Y)));

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
