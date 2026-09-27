using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Everything the form read from its controls at the moment Start was pressed, captured
/// into one immutable value. Building this before the worker thread means nothing the user
/// changes mid-countdown can silently alter the run that was actually requested.
/// </summary>
public sealed record RunSpec
{
    public bool UseSequence { get; init; }
    public List<SeqAction> Actions { get; init; } = new();
    public bool Background { get; init; }
    public bool Limited { get; init; }
    public int Limit { get; init; } = 1;
    public int JitterPx { get; init; }
    public int JitterPct { get; init; }
    public int StartDelaySeconds { get; init; }
    public bool Hold { get; init; }
    public bool DoubleClick { get; init; }
    public bool UseFixedPos { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int IntervalMs { get; init; }
    public int Button { get; init; }
    public string HotkeyName { get; init; } = "";
    public bool PanicEnabled { get; init; }
    public string RunDescription { get; init; } = "";
    /// <summary>Stop the run when the cursor is parked in a screen corner (corner fail-safe).</summary>
    public bool CornerFailSafe { get; init; }
    /// <summary>Stop the run after this many seconds; 0 = unlimited.</summary>
    public int MaxRunSeconds { get; init; }

    /// <summary>Stop the run after this many performed actions (across all passes); 0 = unlimited.</summary>
    public int MaxActions { get; init; }

    /// <summary>Stop the run when the user moves the mouse (opt-in safety stop).</summary>
    public bool StopOnUserMouseMove { get; init; }

    /// <summary>Write a per-step JSONL audit trail for this run.</summary>
    public bool RunLoggingEnabled { get; init; }

    /// <summary>Playback speed as a percentage of recorded timing (100 = as recorded).</summary>
    public int SpeedPercent { get; init; } = 100;

    /// <summary>First action index to run (0-based) when <see cref="UseSequence"/>; earlier actions are skipped.</summary>
    public int StartIndex { get; init; }

    /// <summary>Last action index to run inclusive, or -1 for "to the end".</summary>
    public int EndIndex { get; init; } = -1;

    /// <summary>
    /// Move the cursor back to where it was when the run started. Restores on normal
    /// completion, the watchdog and a user Stop — NOT on panic or the corner fail-safe,
    /// where the user may be actively moving the mouse to escape.
    /// </summary>
    public bool RestoreCursorAfterRun { get; init; }
}

/// <summary>
/// Owns one run's lifecycle: the busy claim that guards start/stop overlap, the worker
/// thread, the optional start-delay countdown and the panic key. It drives the
/// <see cref="SequenceRunner"/> engine and reports progress only through events; the form
/// keeps the UI (buttons, list highlighting, status label) and marshals whatever must run
/// on the UI thread. The panic key is armed through <paramref name="armPanic"/> and
/// disarmed through <paramref name="disarmPanic"/>, so this class never touches the hotkey
/// registration itself.
/// </summary>
internal sealed class RunController : IDisposable
{
    private const string PanicMessage = "Panic stop — all buttons released.";
    private const string MouseMoveStopMessage = "Stopped: mouse moved (stop-on-move).";

    private readonly SequenceRunner runner;
    private readonly Func<bool> armPanic;
    private readonly Action disarmPanic;
    private readonly Func<string> hotkeyNameProvider;
    private readonly Func<string> panicKeyNameProvider;
    private readonly Action<Action> uiMarshal;

    private Thread? worker;
    private volatile bool running;
    private int busy; // 0 = idle, 1 = a worker owns the engine. Guards start/stop overlap.
    private bool panicStopped;
    private System.Windows.Forms.Timer? startDelayTimer;
    private int startDelayCountdown;
    private readonly System.Diagnostics.Stopwatch runStopwatch = new();
    private int watchdogSeconds; // 0 = unlimited
    private bool watchdogTriggered;
    // Max-actions watchdog, mirroring MaxRunSeconds: counted here from the runner's
    // per-step callbacks, checked in KeepGoing so the stop lands on the same hot path.
    private int maxActions; // 0 = unlimited
    private int performedActions;
    private bool actionsWatchdogTriggered;
    // Stop-on-mouse-move: armed only while a run is actually executing (never during the
    // start-delay countdown, when the user is expected to be repositioning the cursor).
    //
    // NOTE (deferred): BlockInputDuringRun was considered and deliberately NOT shipped. Win32
    // BlockInput() blocks the raw input queue for the whole session, and for a non-elevated
    // process there is no escape hatch — the panic hotkey (a RegisterHotKey) would not fire
    // while input is blocked, so a long run would lock the user out with no way to stop it.
    // Input blocking arrives with true pause/resume in the step debugger (Wave 2).
    private MouseMoveWatcher? mouseMoveWatcher;
    private bool stopOnMouseMove;
    // Status the run should end on, set by the failure/watchdog paths and consumed by
    // OnStopped so the worker's own "Stopped. Press ..." line doesn't overwrite it.
    private string? stopStatus;
    private bool runLoggingEnabled;
    private RunLogger? runLogger;
    private List<SeqAction>? logActions;
    private int currentPass;
    // Cursor restore (opt-in): the position captured at Start, whether to restore it, and
    // whether this run ended on the corner fail-safe (which suppresses the restore).
    private POINT startCursor;
    private bool restoreCursor;
    private bool failSafeStopped;

    public RunController(Func<bool> armPanic, Action disarmPanic, Func<string> hotkeyNameProvider,
        Func<string> panicKeyNameProvider, Action<Action> uiMarshal)
    {
        this.armPanic = armPanic;
        this.disarmPanic = disarmPanic;
        this.hotkeyNameProvider = hotkeyNameProvider;
        this.panicKeyNameProvider = panicKeyNameProvider;
        this.uiMarshal = uiMarshal;
        runner = new SequenceRunner(KeepGoing, OnRunnerStep, OnRunnerStepStarting, OnRunnerPassComplete);
    }

    /// <summary>Raised when the status line should change.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the running state changes (buttons follow this).</summary>
    public event Action<bool>? RunningChanged;

    /// <summary>Raised immediately before a step is attempted, with its index.</summary>
    public event Action<int>? StepStarting;

    /// <summary>Raised once a step has been attempted: (index, wasPerformed). index -1 means the run ended.</summary>
    public event Action<int, bool>? StepCompleted;

    /// <summary>Raised when a run's worker finishes (on the worker thread).</summary>
    public event Action? RunFinished;

    public bool IsRunning => running;

    public bool IsBusy => Volatile.Read(ref busy) != 0;

    /// <summary>Claims the engine and starts (or schedules) the run described by <paramref name="spec"/>.</summary>
    public bool TryStart(RunSpec spec)
    {
        // Claim the engine. `running` alone is not enough: after Stop, the previous worker
        // is still unwinding and its finally-block sets running = false — which would
        // immediately kill a run started in that window. busy is only released by Finish(),
        // once the old worker is genuinely done — or, if the run never leaves its start-delay
        // countdown, by CancelPendingStartDelay instead (Finish() never runs for that thread).
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;

        panicStopped = false;
        stopStatus = null;
        watchdogTriggered = false;
        watchdogSeconds = spec.MaxRunSeconds;
        actionsWatchdogTriggered = false;
        maxActions = spec.MaxActions;
        performedActions = 0;
        stopOnMouseMove = spec.StopOnUserMouseMove;
        mouseMoveWatcher = null;
        runLoggingEnabled = spec.RunLoggingEnabled;
        failSafeStopped = false;
        restoreCursor = spec.RestoreCursorAfterRun;
        // Captured once, before any worker (or even a start-delay countdown) can move the
        // cursor — this is "where the user left it", not wherever a delay happened to end.
        if (restoreCursor) GetCursorPos(out startCursor);
        running = true;
        RunningChanged?.Invoke(true);

        bool panicArmed = armPanic();
        string panicWarn = spec.PanicEnabled && !panicArmed
            ? $" {panicKeyNameProvider()} is already claimed by another app — panic key OFF."
            : "";

        // Build the thread now, capturing every run parameter at the moment Start was
        // pressed — not after a delay elapses — so nothing the user changes mid-countdown
        // can silently alter the run that was actually requested.
        if (spec.UseSequence)
        {
            var options = new RunOptions
            {
                Background = spec.Background,
                Limited = spec.Limited,
                Limit = spec.Limit,
                JitterPixels = spec.JitterPx,
                JitterPercent = spec.JitterPct,
                SpeedPercent = spec.SpeedPercent,
                StartIndex = spec.StartIndex,
                EndIndex = spec.EndIndex,
                CornerFailSafe = spec.CornerFailSafe
            };
            worker = new Thread(() => RunSequenceWorker(spec.Actions, options)) { IsBackground = true };
        }
        else
        {
            bool hold = spec.Hold;
            var options = new SingleRunOptions
            {
                Hold = hold,
                DoubleClick = spec.DoubleClick,
                Button = spec.Button,
                UseFixedPosition = spec.UseFixedPos,
                X = spec.X,
                Y = spec.Y,
                IntervalMs = spec.IntervalMs,
                // A repeat count is meaningless for press-and-hold: computed at the call site,
                // same as before the engine was extracted, so RunSingle itself stays dumb about it.
                Limited = spec.Limited && !hold,
                Limit = spec.Limit,
                JitterPixels = spec.JitterPx,
                JitterPercent = spec.JitterPct,
                SpeedPercent = spec.SpeedPercent
            };
            worker = new Thread(() => RunSingleWorker(options)) { IsBackground = true };
        }

        if (spec.StartDelaySeconds <= 0)
        {
            StatusChanged?.Invoke(spec.RunDescription + panicWarn);
            StartWorkerThread();
            return true;
        }

        startDelayCountdown = spec.StartDelaySeconds;
        startDelayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        StatusChanged?.Invoke($"Starting in {startDelayCountdown} s... press {spec.HotkeyName} or {panicKeyNameProvider()} to cancel.{panicWarn}");
        startDelayTimer.Tick += (_, _) =>
        {
            startDelayCountdown--;
            if (startDelayCountdown > 0)
            {
                StatusChanged?.Invoke($"Starting in {startDelayCountdown} s... press {spec.HotkeyName} or {panicKeyNameProvider()} to cancel.{panicWarn}");
                return;
            }
            startDelayTimer!.Stop();
            startDelayTimer.Dispose();
            startDelayTimer = null;
            StatusChanged?.Invoke(spec.RunDescription + panicWarn);
            StartWorkerThread();
        };
        startDelayTimer.Start();
        return true;
    }

    public void Stop()
    {
        running = false;
        CancelPendingStartDelay();
    }

    // The whole point of the panic key is that it releases any button the worker may have
    // left held, so it must set running = false AND release the buttons AND surface the
    // panic message, on top of cancelling a pending start-delay.
    public void PanicStop()
    {
        panicStopped = true;
        CancelPendingStartDelay();
        running = false;
        InputSender.ReleaseAllButtons();
        StatusChanged?.Invoke(PanicMessage);
    }

    // Cancels a pending start-delay countdown, if any. The thread built in TryStart was
    // never started in that case, so Finish() will never run for it — this is the only
    // path that releases `busy` and restores the UI for a countdown that never went live.
    public bool CancelPendingStartDelay()
    {
        if (startDelayTimer == null) return false;
        startDelayTimer.Stop();
        startDelayTimer.Dispose();
        startDelayTimer = null;
        worker = null; // discard the never-started thread
        Interlocked.Exchange(ref busy, 0);
        OnStopped();
        return true;
    }

    /// <summary>Joins the worker thread, if any, up to <paramref name="timeoutMs"/> milliseconds.</summary>
    public void JoinWorker(int timeoutMs) => worker?.Join(timeoutMs);

    public void Dispose()
    {
        startDelayTimer?.Dispose();
        startDelayTimer = null;
        mouseMoveWatcher?.Stop();
        mouseMoveWatcher = null;
    }

    private void StartWorkerThread()
    {
        // The watchdog counts from when the worker actually starts — not from when Start
        // was pressed — so a long start-delay countdown never eats into MaxRunSeconds.
        runStopwatch.Restart();

        // Stop-on-mouse-move is armed here, not in TryStart, so moving the mouse during
        // the start-delay countdown (to position it) never cancels the run. Installed on
        // the UI thread — the same thread this method runs on — which is where the hook
        // callback will be delivered.
        if (stopOnMouseMove)
        {
            var watcher = new MouseMoveWatcher();
            watcher.Moved += OnUserMouseMoved;
            if (!watcher.Start())
            {
                watcher.Moved -= OnUserMouseMoved;
                StatusChanged?.Invoke("Stop-on-mouse-move unavailable (mouse hook busy) — running without it.");
            }
            else
            {
                mouseMoveWatcher = watcher;
            }
        }

        try
        {
            worker!.Start();
        }
        catch (Exception ex)
        {
            running = false;
            worker = null;
            disarmPanic();
            Interlocked.Exchange(ref busy, 0);
            OnStopped();
            StatusChanged?.Invoke("Could not start: " + ex.Message);
        }
    }

    // SequenceRunner itself never catches exceptions — it propagates them so the caller
    // decides how to report an abort. This is that decision: surface it to the status label
    // and always release the engine, exactly as the pre-extraction code did.
    private void RunSingleWorker(SingleRunOptions options)
    {
        try { runner.RunSingle(options); }
        catch (Exception ex) { ReleaseAfterFailure(ex); }
        finally { Finish(); }
    }

    private void RunSequenceWorker(List<SeqAction> acts, RunOptions options)
    {
        // The logger is created on the worker thread so its clock starts at run start, and
        // disposed here so its file is flushed exactly once per run, even on an abort.
        currentPass = 0;
        runLogger = runLoggingEnabled ? RunLogger.TryCreate() : null;
        logActions = runLogger is not null ? acts : null;
        try { runner.RunSequence(acts, options); }
        catch (Exception ex) { ReleaseAfterFailure(ex); }
        finally
        {
            runLogger?.Dispose();
            runLogger = null;
            logActions = null;
            Finish();
        }
    }

    /// <summary>
    /// A run that ended by throwing may have died between a button-down and its matching
    /// up — an elevated foreground window makes the release itself throw, for instance.
    /// Release everything before reporting, so a failed run can't leave a button held.
    /// The CLI already does this unconditionally; the GUI previously relied on the user
    /// noticing and hitting the panic key.
    /// </summary>
    private void ReleaseAfterFailure(Exception ex)
    {
        InputSender.ReleaseAllButtons();
        // A fail-safe stop is a deliberate, user-triggered stop, not an error — report its
        // message verbatim (e.g. "Stopped by corner fail-safe.") rather than with the
        // "Stopped —" error prefix. The status is surfaced by OnStopped (called from
        // Finish), which runs right after this on the same worker thread.
        failSafeStopped = ex is FailSafeException;
        stopStatus = ex is FailSafeException ? ex.Message : "Stopped — " + ex.Message;
    }

    // Surfaces the engine's per-step callbacks as events; the form subscribes to drive the
    // list highlighting.
    private void OnRunnerStepStarting(int index) => StepStarting?.Invoke(index);

    // Surfaces the engine's per-step completion (or suppression) as an event; index -1
    // means the run ended and any highlight should be cleared.
    private void OnRunnerStep(int index, bool performed)
    {
        if (index >= 0 && performed) performedActions++;
        LogStep(index, performed);
        StepCompleted?.Invoke(index, performed);
    }

    private void OnRunnerPassComplete() => currentPass++;

    // Appends the step to the run's JSONL audit trail, when logging is on. Runs on the
    // worker thread (the engine calls onStep synchronously), so no marshalling is needed.
    private void LogStep(int index, bool performed)
    {
        if (runLogger is null || logActions is null || index < 0 || index >= logActions.Count) return;
        SeqAction a = logActions[index];
        int? x = null, y = null;
        if (a.Kind is ActionKind.Click or ActionKind.Drag or ActionKind.Scroll or ActionKind.WaitPixel)
        {
            x = a.X;
            y = a.Y;
        }
        runLogger.Log(currentPass, index, a.Kind.ToString(), a.Describe(), performed, x, y);
    }

    private bool KeepGoing()
    {
        // The watchdog is checked here, on the hot path every action/sleep slice polls, so
        // a max-run-time stop lands within ~20 ms of the deadline without a second timer.
        if (running && watchdogSeconds > 0 && runStopwatch.Elapsed.TotalSeconds >= watchdogSeconds)
        {
            watchdogTriggered = true;
            return false;
        }
        // The max-actions watchdog uses the same mechanism, counting performed actions.
        if (running && MaxActionsReached(performedActions, maxActions))
        {
            actionsWatchdogTriggered = true;
            return false;
        }
        return running;
    }

    /// <summary>True when a positive performed-action budget has been reached. Pure, for tests.</summary>
    internal static bool MaxActionsReached(int performed, int maxActions) =>
        maxActions > 0 && performed >= maxActions;

    // The stop-on-mouse-move watcher fires on the UI thread (the thread that installed the
    // hook); setting stopStatus here means OnStopped reports it, and Stop() is idempotent
    // so a duplicate fire between Stop and OnStopped is harmless.
    private void OnUserMouseMoved()
    {
        stopStatus = MouseMoveStopMessage;
        Stop();
    }

    private void Finish()
    {
        running = false;
        RestoreCursorIfNeeded();
        RunFinished?.Invoke();
        uiMarshal(OnStopped);
        // Released last: TryStart must not be able to claim the engine until this worker
        // has finished touching `running`.
        Interlocked.Exchange(ref busy, 0);
    }

    /// <summary>
    /// Restores the cursor to where it was when the run started, when the user opted in.
    /// Deliberately skipped on a panic and on the corner fail-safe: both mean the user is
    /// reaching for the mouse to stop the run, and yanking it back out from under them is
    /// worse than leaving it. Best-effort — a failure here is ignored.
    /// </summary>
    private void RestoreCursorIfNeeded()
    {
        if (!restoreCursor || panicStopped || failSafeStopped) return;
        SetCursorPos(startCursor.X, startCursor.Y);
    }

    private void OnStopped()
    {
        disarmPanic();
        mouseMoveWatcher?.Stop();
        mouseMoveWatcher = null;
        RunningChanged?.Invoke(false);
        // Confirming the buttons were force-released is the whole point of the panic key,
        // so don't let the worker's own async "Stopped" message land on top of that. A
        // watchdog stop and a failure/fail-safe stop likewise keep their specific message
        // instead of the generic "Stopped. Press ..." line.
        string status = panicStopped ? PanicMessage
            : watchdogTriggered ? "Watchdog: max run time reached."
            : actionsWatchdogTriggered ? "Watchdog: max actions reached."
            : stopStatus ?? $"Stopped. Press {hotkeyNameProvider()} to start.";
        StatusChanged?.Invoke(status);
    }
}
