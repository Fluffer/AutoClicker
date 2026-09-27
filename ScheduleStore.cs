using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// Loads and saves the user's scheduled runs, mirroring <see cref="ProfileStore"/>'s
/// null-safety, corrupt-file preservation and atomic temp-file+rename write.
/// </summary>
internal static class ScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Moves an unreadable schedules file aside so a later save can't overwrite it.</summary>
    private static void PreserveUnreadableFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            string kept = FilePath + ".corrupt";
            for (int i = 2; File.Exists(kept) && i < 100; i++) kept = $"{FilePath}.corrupt{i}";
            File.Move(FilePath, kept, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing more we can do; the caller is already told not to save.
        }
    }

    /// <summary>Where schedules are read from and written to: <c>Documents\AutoClicker\schedules.json</c>.</summary>
    public static string FilePath { get; internal set; } = UserDataPaths.SchedulesPath;

    /// <summary>Loads schedules from <see cref="FilePath"/>. Never throws; a bad file yields an empty list.</summary>
    public static List<ScheduledRun> Load()
    {
        List<ScheduledRun> loaded;
        try
        {
            string json = File.ReadAllText(FilePath);
            loaded = string.IsNullOrWhiteSpace(json)
                ? new List<ScheduledRun>()
                : JsonSerializer.Deserialize<List<ScheduledRun>>(json, JsonOptions) ?? new List<ScheduledRun>();
        }
        catch (FileNotFoundException) { loaded = new List<ScheduledRun>(); }
        catch (DirectoryNotFoundException) { loaded = new List<ScheduledRun>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or NotSupportedException
                                      or ArgumentException or System.Security.SecurityException)
        {
            PreserveUnreadableFile();
            loaded = new List<ScheduledRun>();
        }

        var result = new List<ScheduledRun>();
        foreach (ScheduledRun schedule in loaded)
        {
            if (schedule is null) continue; // a literal null element must not brick startup
            schedule.Normalize();
            if (schedule.Name.Length == 0) continue; // unusable without a name
            result.Add(schedule);
        }
        return result;
    }

    /// <summary>Writes schedules atomically (temp file + rename). Returns false instead of throwing.</summary>
    public static bool Save(IReadOnlyList<ScheduledRun> schedules)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tempPath = $"{FilePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(schedules, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or JsonException
                                      or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
