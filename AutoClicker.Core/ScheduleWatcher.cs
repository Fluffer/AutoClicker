using System.Diagnostics;

namespace AutoClicker;

/// <summary>
/// Fires scheduled runs while the app is open. A background timer checks every 30 seconds
/// against <see cref="ScheduleStore"/>; a due schedule launches the already-headless CLI
/// (<c>AutoClicker.exe --run file --repeat 1</c> or <c>--profile name --repeat 1</c>) as a
/// child process, so a scheduled run behaves exactly like a manual one and can't deadlock
/// the GUI. A closed app fires nothing — Windows Task Scheduler is the future path for
/// unattended runs.
/// </summary>
/// <remarks>
/// The process-spawn approach was chosen over an in-process engine call or a Quartz.NET
/// dependency: reusing the CLI means no new package, no cross-thread engine sharing, and
/// every scheduled run gets the CLI's own panic handling and console output for free.
/// </remarks>
internal sealed class ScheduleWatcher : IDisposable
{
    private readonly System.Threading.Timer timer;
    private readonly Action<string> reportStatus;
    private readonly Dictionary<string, DateTime> lastFired = new();

    public ScheduleWatcher(Action<string> reportStatus)
    {
        this.reportStatus = reportStatus;
        timer = new System.Threading.Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start() => timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));

    public void Stop() => timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void Tick()
    {
        try
        {
            List<ScheduledRun> schedules = ScheduleStore.Load();
            DateTime now = DateTime.Now;
            DateTime currentMinute = now.Date.AddHours(now.Hour).AddMinutes(now.Minute);

            foreach (ScheduledRun s in schedules)
            {
                if (!s.IsDue(now)) continue;
                string key = s.IdentityKey;
                // The 30 s tick can hit the same minute twice; fire at most once per minute.
                if (lastFired.TryGetValue(key, out DateTime fired) && fired == currentMinute) continue;
                lastFired[key] = currentMinute;
                Launch(s);
            }
        }
        catch
        {
            // The scheduler must never take the app down with it.
        }
    }

    private void Launch(ScheduledRun s)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrWhiteSpace(s.ProfileName))
            {
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add(s.ProfileName);
            }
            else
            {
                psi.ArgumentList.Add("--run");
                psi.ArgumentList.Add(s.SequencePath);
            }
            psi.ArgumentList.Add("--repeat");
            psi.ArgumentList.Add("1");

            Process.Start(psi);
            reportStatus($"Scheduled run \"{s.Name}\" started ({DateTime.Now:HH:mm:ss}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            reportStatus($"Scheduled run \"{s.Name}\" failed to start: {ex.Message}");
        }
    }

    public void Dispose() => timer.Dispose();
}
