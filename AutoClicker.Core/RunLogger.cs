using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// Appends one JSON object per engine step to a per-run JSONL file, giving a machine-readable
/// audit trail of every run. Each line is a flat object: <c>t</c> (ms since run start),
/// <c>pass</c>, <c>ip</c>, <c>kind</c>, <c>describe</c>, <c>performed</c>, and <c>x</c>/<c>y</c>
/// when the action has coordinates. Never throws — logging is best-effort and must not break
/// a run — so every failure (missing disk, permission, closed stream) is swallowed.
/// </summary>
internal sealed class RunLogger : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();
    private StreamWriter? writer;

    /// <summary>Directory JSONL files are written to. Tests redirect this to a temp folder.</summary>
    internal static string DirectoryPath { get; set; } = UserDataPaths.LogsDir;

    /// <summary>The JSONL file this logger writes to, or null when it failed to open.</summary>
    public string? FilePath { get; }

    /// <summary>Creates a logger for a new run, or null when the log directory can't be used.</summary>
    public static RunLogger? TryCreate()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath,
                "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
            return new RunLogger(new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true }, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private RunLogger(StreamWriter writer, string path)
    {
        this.writer = writer;
        FilePath = path;
    }

    /// <summary>Appends one step's entry. <paramref name="note"/> and the coordinates are optional.</summary>
    public void Log(int pass, int ip, string kind, string describe, bool performed,
        int? x = null, int? y = null, string? note = null)
    {
        if (writer is null) return;

        lock (gate)
        {
            if (writer is null) return;
            try
            {
                var entry = new Dictionary<string, object?>
                {
                    ["t"] = clock.ElapsedMilliseconds,
                    ["pass"] = pass,
                    ["ip"] = ip,
                    ["kind"] = kind,
                    ["describe"] = describe,
                    ["performed"] = performed,
                };
                if (x.HasValue) entry["x"] = x.Value;
                if (y.HasValue) entry["y"] = y.Value;
                if (note is not null) entry["note"] = note;
                writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Never let logging break a run.
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer?.Dispose();
            writer = null;
        }
    }
}
