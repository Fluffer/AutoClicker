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

    private readonly SequenceRunner runner;
    private readonly Func<bool> armPanic;
    private readonly Action disarmPanic;
    private readonly Func<string> hotkeyNameProvider;
    private readonly Action<Action> uiMarshal;

    private Thread? worker;
    private volatile bool running;
    private int busy; // 0 = idle, 1 = a worker owns the engine. Guards start/stop overlap.
    private bool panicStopped;
    private System.Windows.Forms.Timer? startDelayTimer;
    private int startDelayCountdown;

    public RunController(Func<bool> armPanic, Action disarmPanic, Func<string> hotkeyNameProvider, Action<Action> uiMarshal)
    {
        this.armPanic = armPanic;
        this.disarmPanic = disarmPanic;
        this.hotkeyNameProvider = hotkeyNameProvider;
        this.uiMarshal = uiMarshal;
        runner = new SequenceRunner(KeepGoing, OnRunnerStep, OnRunnerStepStarting);
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
        running = true;
        RunningChanged?.Invoke(true);

        bool panicArmed = armPanic();
        string panicWarn = spec.PanicEnabled && !panicArmed
            ? " Esc is already claimed by another app — panic key OFF."
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
                JitterPercent = spec.JitterPct
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
                JitterPercent = spec.JitterPct
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
        StatusChanged?.Invoke($"Starting in {startDelayCountdown} s... press {spec.HotkeyName} or Esc to cancel.{panicWarn}");
        startDelayTimer.Tick += (_, _) =>
        {
            startDelayCountdown--;
            if (startDelayCountdown > 0)
            {
                StatusChanged?.Invoke($"Starting in {startDelayCountdown} s... press {spec.HotkeyName} or Esc to cancel.{panicWarn}");
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
    }

    private void StartWorkerThread()
    {
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
        try { runner.RunSequence(acts, options); }
        catch (Exception ex) { ReleaseAfterFailure(ex); }
        finally { Finish(); }
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
        uiMarshal(() => StatusChanged?.Invoke("Stopped — " + ex.Message));
    }

    // Surfaces the engine's per-step callbacks as events; the form subscribes to drive the
    // list highlighting.
    private void OnRunnerStepStarting(int index) => StepStarting?.Invoke(index);

    // Surfaces the engine's per-step completion (or suppression) as an event; index -1
    // means the run ended and any highlight should be cleared.
    private void OnRunnerStep(int index, bool performed) => StepCompleted?.Invoke(index, performed);

    private bool KeepGoing() => running;

    private void Finish()
    {
        running = false;
        RunFinished?.Invoke();
        uiMarshal(OnStopped);
        // Released last: TryStart must not be able to claim the engine until this worker
        // has finished touching `running`.
        Interlocked.Exchange(ref busy, 0);
    }

    private void OnStopped()
    {
        disarmPanic();
        RunningChanged?.Invoke(false);
        // Confirming the buttons were force-released is the whole point of the panic key,
        // so don't let the worker's own async "Stopped" message land on top of that.
        StatusChanged?.Invoke(panicStopped ? PanicMessage : $"Stopped. Press {hotkeyNameProvider()} to start.");
    }
}
