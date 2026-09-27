using Microsoft.UI.Dispatching;

namespace AutoClicker.WinUI;

/// <summary>
/// The shared pick countdown: <paramref name="seconds"/> one-second ticks on the UI
/// dispatcher, then a completion callback. Extracted from M2b1's <c>MainWindow.StartPick</c>
/// so the M3 dialogs (action editor position pick / pixel pick / template capture) drive the
/// same mechanism with their own per-tick UI — button captions instead of the status line —
/// without duplicating the timer code. Both originals show the full count immediately, then
/// decrement once per second and fire the callback when the count reaches zero, so
/// <see cref="Start"/> invokes <paramref name="onTick"/> once up front and then on every
/// tick. <see cref="Dispose"/> cancels WITHOUT firing the completion callback — that is how
/// a window teardown or a dialog closed mid-countdown drops its pending pick, matching the
/// old <c>_pickTimer?.Stop(); _pickDone = null;</c> cleanup.
/// </summary>
internal sealed class PickCountdown : IDisposable
{
    private readonly DispatcherQueueTimer timer;
    private readonly Action<int>? onTick;
    private readonly Action onDone;
    private int remaining;

    private PickCountdown(DispatcherQueue queue, int seconds, Action<int>? onTick, Action onDone)
    {
        this.onTick = onTick;
        this.onDone = onDone;
        remaining = seconds;
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += OnTick;
    }

    /// <summary>Starts the countdown and reports the full count once, immediately.</summary>
    public static PickCountdown Start(DispatcherQueue queue, int seconds, Action<int>? onTick, Action onDone)
    {
        var countdown = new PickCountdown(queue, seconds, onTick, onDone);
        onTick?.Invoke(seconds);
        countdown.timer.Start();
        return countdown;
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (--remaining > 0)
        {
            onTick?.Invoke(remaining);
            return;
        }
        sender.Stop();
        sender.Tick -= OnTick;
        onDone();
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= OnTick;
    }
}
