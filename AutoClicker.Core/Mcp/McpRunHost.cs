using System.Diagnostics;
using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// Hosts one background sequence run for the MCP server, in the same shape the CLI and GUI
/// use: a <see cref="SequenceRunner"/> driven with a <see cref="RunOptions"/>, with
/// cancellation standing in for the GUI's Stop button / the CLI's Ctrl+C. <c>run_sequence</c>
/// starts a run and returns immediately; <c>get_state</c> polls this host's snapshot, and
/// <c>stop_run</c> cancels it. One run at a time, like the GUI.
/// </summary>
internal sealed class McpRunHost
{
    private readonly object gate = new();
    private CancellationTokenSource? cts;
    private Task? runTask;
    private string? runId;
    private bool running;
    private string status = "idle";
    private string completion = "";
    private string currentTarget = "";
    private int stepsDone;
    private readonly Stopwatch stopwatch = new();

    /// <summary>Starts a background run. Returns a fresh run id, or null when a run is already active.</summary>
    public string? StartRun(IReadOnlyList<SeqAction> actions, RunOptions options, string sourceLabel)
    {
        lock (gate)
        {
            if (running) return null;

            var localCts = new CancellationTokenSource();
            cts = localCts;
            runId = Guid.NewGuid().ToString("N");
            running = true;
            status = $"running {sourceLabel}";
            completion = "";
            currentTarget = "";
            stepsDone = 0;
            stopwatch.Restart();

            var runner = new SequenceRunner(
                keepGoing: () => !localCts.IsCancellationRequested,
                onStep: (idx, performed) =>
                {
                    if (idx < 0) return; // end-of-run marker
                    lock (gate)
                    {
                        if (performed) stepsDone++;
                        currentTarget = idx < actions.Count
                            ? $"{actions[idx].Describe()} at {actions[idx].DescribeTarget()}"
                            : "";
                    }
                });

            runTask = Task.Run(() =>
            {
                try
                {
                    runner.RunSequence(actions, options);
                    lock (gate) completion = "completed";
                }
#pragma warning disable CA1031 // Any engine exception is a run abort, reported through get_state.
                catch (Exception ex)
                {
                    lock (gate) completion = "aborted: " + ex.Message;
                }
#pragma warning restore CA1031
                finally
                {
                    lock (gate)
                    {
                        running = false;
                        status = "idle";
                        stopwatch.Stop();
                    }
                }
            });

            return runId;
        }
    }

    /// <summary>Cancels the active run. Returns false when nothing was running.</summary>
    public bool StopRun()
    {
        lock (gate)
        {
            if (!running || cts is null) return false;
            cts.Cancel();
            return true;
        }
    }

    /// <summary>A JSON snapshot for the <c>get_state</c> tool.</summary>
    public string GetStateJson()
    {
        lock (gate)
        {
            return JsonSerializer.Serialize(new
            {
                state = running ? "running" : "idle",
                run_id = running ? runId : null,
                status,
                completion,
                current_target = currentTarget,
                steps_done = stepsDone,
                elapsed_ms = stopwatch.ElapsedMilliseconds,
            });
        }
    }
}
