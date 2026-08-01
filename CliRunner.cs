using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// Headless entry point for scripted/scheduled runs: <c>AutoClicker.exe --run seq.acseq --repeat 10</c>.
/// Drives the same <see cref="SequenceRunner"/> the GUI uses — no engine logic is duplicated
/// here — so a sequence behaves identically whether it was launched by hand or by Task
/// Scheduler. Everything below talks to <see cref="Console"/>, never to a WinForms control.
/// </summary>
internal static class CliRunner
{
    private const int ExitSuccess = 0;
    private const int ExitRuntimeFailure = 1;
    private const int ExitUsage = 2;

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static volatile bool stopRequested;

    /// <summary>
    /// Entry point called by <see cref="Program.Main"/> whenever the process was launched
    /// with arguments. Returns the process exit code.
    /// </summary>
    public static int Run(string[] args)
    {
        // This project is a WinExe, so the process starts with no console attached and
        // Console.WriteLine goes nowhere. Borrow the launching terminal's console instead —
        // and if there isn't one (e.g. launched from Explorer with arguments), degrade to
        // running silently rather than failing.
        bool attached = AttachConsole(AttachParentProcess);
        if (attached)
        {
            // AttachConsole only reattaches the OS-level handles; the CLR's Console.Out/Error
            // were already opened against the (nonexistent) handles this process started with,
            // so writes still vanish until the standard streams are reopened.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }

        // Ctrl+C must stop the run cleanly rather than let the runtime kill the process
        // mid-click or mid-drag: cancel the default termination, flip the keep-going flag,
        // and let SequenceRunner unwind on its own.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopRequested = true;
        };

        try
        {
            return RunCore(args);
        }
        finally
        {
            // A run killed mid-drag or mid-hold would otherwise leave a mouse button
            // physically down system-wide with no window left to click to recover it.
            InputSender.ReleaseAllButtons();
            if (attached) FreeConsole();
        }
    }

    private static int RunCore(string[] args)
    {
        string? runFile = null;
        string? profileName = null;
        bool doList = false;
        bool doHelp = false;
        bool doVersion = false;

        bool repeatSpecified = false;
        int repeat = 1;
        bool untilStopped = false;
        bool background = false;
        int jitterPx = 0;
        int jitterPct = 0;
        int startDelay = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? error;
            switch (arg)
            {
                case "--help" or "-h" or "-?":
                    doHelp = true;
                    break;
                case "--version":
                    doVersion = true;
                    break;
                case "--list":
                    doList = true;
                    break;
                case "--run":
                    if (!TryTakeValue(args, ref i, arg, out runFile, out error)) return UsageError(error);
                    break;
                case "--profile":
                    if (!TryTakeValue(args, ref i, arg, out profileName, out error)) return UsageError(error);
                    break;
                case "--repeat":
                    if (!TryTakeIntValue(args, ref i, arg, out repeat, out error)) return UsageError(error);
                    repeatSpecified = true;
                    break;
                case "--until-stopped":
                    untilStopped = true;
                    break;
                case "--background":
                    background = true;
                    break;
                case "--jitter-px":
                    if (!TryTakeIntValue(args, ref i, arg, out jitterPx, out error)) return UsageError(error);
                    break;
                case "--jitter-pct":
                    if (!TryTakeIntValue(args, ref i, arg, out jitterPct, out error)) return UsageError(error);
                    break;
                case "--start-delay":
                    if (!TryTakeIntValue(args, ref i, arg, out startDelay, out error)) return UsageError(error);
                    break;
                default:
                    return UsageError($"Unknown option '{arg}'.");
            }
        }

        if (doHelp) { PrintHelp(); return ExitSuccess; }
        if (doVersion) { PrintVersion(); return ExitSuccess; }
        if (doList) return ListProfiles();

        if (repeatSpecified && untilStopped)
            return UsageError("--repeat and --until-stopped are mutually exclusive.");
        if (repeat is < 1 or > 1_000_000)
            return UsageError("--repeat must be between 1 and 1000000.");
        if (jitterPx is < 0 or > 500)
            return UsageError("--jitter-px must be between 0 and 500.");
        if (jitterPct is < 0 or > 100)
            return UsageError("--jitter-pct must be between 0 and 100.");
        if (startDelay is < 0 or > 300)
            return UsageError("--start-delay must be between 0 and 300.");

        if (runFile is null && profileName is null)
            return UsageError("Specify --run <file>, --profile <name>, --list, or --help.");
        if (runFile is not null && profileName is not null)
            return UsageError("Specify only one of --run or --profile.");

        List<SeqAction> actions;
        string sourceLabel;
        if (runFile is not null)
        {
            if (!File.Exists(runFile))
                return UsageError($"Sequence file not found: {runFile}");

            try
            {
                var loaded = JsonSerializer.Deserialize<List<SeqAction>>(File.ReadAllText(runFile));
                if (loaded is null)
                {
                    Console.Error.WriteLine($"'{runFile}' contains no actions.");
                    return ExitRuntimeFailure;
                }
                actions = loaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Failed to read '{runFile}': {ex.Message}");
                return ExitRuntimeFailure;
            }
            sourceLabel = Path.GetFileName(runFile);
        }
        else
        {
            List<Profile> profiles = ProfileStore.Load();
            Profile? profile = profiles.Find(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                string available = profiles.Count == 0
                    ? "(none saved)"
                    : string.Join(", ", profiles.Select(p => p.Name));
                return UsageError($"Profile '{profileName}' not found. Available profiles: {available}");
            }
            actions = profile.Actions;
            sourceLabel = profile.Name;
        }

        foreach (SeqAction a in actions) a.Normalize();
        if (actions.Count == 0)
            return UsageError($"'{sourceLabel}' has no actions.");

        if (startDelay > 0 && !RunCountdown(startDelay))
        {
            Console.WriteLine("Cancelled before starting.");
            return ExitSuccess;
        }

        string passDesc = untilStopped ? "until stopped (Ctrl+C)" : repeat == 1 ? "once" : $"{repeat} passes";
        Console.WriteLine($"Running '{sourceLabel}' ({actions.Count} action(s), {passDesc})...");

        var options = new RunOptions
        {
            Background = background,
            Limited = !untilStopped,
            Limit = repeat,
            JitterPixels = jitterPx,
            JitterPercent = jitterPct,
        };

        int performedCount = 0;
        int stepCount = 0;
        var runner = new SequenceRunner(
            keepGoing: () => !stopRequested,
            onStep: (idx, performed) =>
            {
                if (idx < 0) return; // end-of-run marker, not a step
                stepCount++;
                if (performed) performedCount++;
            });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            runner.RunSequence(actions, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Run aborted: {ex.Message}");
            return ExitRuntimeFailure;
        }
        stopwatch.Stop();

        int passesCompleted = stepCount / actions.Count;
        Console.WriteLine(FormattableString.Invariant(
            $"Done: {performedCount} action(s) performed, {passesCompleted} pass(es) completed, elapsed {stopwatch.Elapsed:mm\\:ss\\.fff}."));
        return ExitSuccess;
    }

    /// <summary>Counts down once per second so the user can focus a target window. Returns false if Ctrl+C fired.</summary>
    private static bool RunCountdown(int seconds)
    {
        for (int s = seconds; s > 0; s--)
        {
            if (stopRequested) return false;
            Console.WriteLine($"Starting in {s}...");
            var sw = Stopwatch.StartNew();
            while (!stopRequested && sw.ElapsedMilliseconds < 1000) Thread.Sleep(20);
            if (stopRequested) return false;
        }
        return true;
    }

    private static int ListProfiles()
    {
        List<Profile> profiles = ProfileStore.Load();
        if (profiles.Count == 0)
        {
            Console.WriteLine("No saved profiles.");
            return ExitSuccess;
        }

        foreach (Profile p in profiles)
        {
            string hotkey = string.IsNullOrEmpty(p.HotkeyName) ? "" : $" (hotkey {p.HotkeyName})";
            Console.WriteLine($"{p.Name} — {p.Actions.Count} action(s){hotkey}");
        }
        return ExitSuccess;
    }

    private static void PrintVersion()
    {
        Assembly asm = typeof(CliRunner).Assembly;
        string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
        Console.WriteLine($"Auto Clicker {version}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Auto Clicker - command-line mode

            Usage:
              AutoClicker.exe                              Launch the GUI (no arguments)
              AutoClicker.exe --run <file.acseq> [options]  Run a sequence file
              AutoClicker.exe --profile <name> [options]    Run a saved profile
              AutoClicker.exe --list                        List saved profiles
              AutoClicker.exe --help | -h | -?               Show this help
              AutoClicker.exe --version                      Show version

            Options:
              --repeat <n>        Run the sequence n times (default: 1)
              --until-stopped      Repeat until Ctrl+C (mutually exclusive with --repeat)
              --background         Use background mode (PostMessage) instead of real input
              --jitter-px <n>      Position jitter, +/- pixels, 0-500 (default: 0)
              --jitter-pct <n>     Timing jitter, +/- percent, 0-100 (default: 0)
              --start-delay <s>    Wait s seconds before starting, 0-300 (default: 0)

            Exit codes:
              0   Success (including a clean Ctrl+C stop)
              1   Runtime failure (the engine threw, or the sequence file could not be read)
              2   Bad usage (unknown/missing/invalid option, missing file, unknown profile)
            """);
    }

    private static int UsageError(string? message)
    {
        if (!string.IsNullOrEmpty(message)) Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'AutoClicker.exe --help' for usage.");
        return ExitUsage;
    }

    private static bool TryTakeValue(string[] args, ref int i, string flag, out string? value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            error = $"Option '{flag}' requires a value.";
            return false;
        }
        value = args[++i];
        error = null;
        return true;
    }

    private static bool TryTakeIntValue(string[] args, ref int i, string flag, out int value, out string? error)
    {
        if (!TryTakeValue(args, ref i, flag, out string? raw, out error))
        {
            value = 0;
            return false;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"Option '{flag}' expects a whole number, got '{raw}'.";
            return false;
        }
        return true;
    }
}
