using System.Globalization;

namespace AutoClicker;

/// <summary>
/// Canonical locations for persistent user data, plus the one-time migration that moves
/// legacy data out of the old location.
/// </summary>
/// <remarks>
/// Everything lives under <c>Documents\AutoClicker</c> rather than <c>%AppData%\AutoClicker</c>:
/// an MSIX full-trust package virtualizes AppData into the package, and uninstalling the
/// package deletes those virtualized files — settings, profiles and schedules would all
/// vanish on uninstall. Documents is a known-folder-brokered location: packaged and
/// unpackaged builds both read and write the real folder, and its contents survive an
/// uninstall.
/// </remarks>
internal static class UserDataPaths
{
    private const string MigrationMarker = ".migrated";
    private static bool ensured;
    private static string? migrationNotice;

    /// <summary>The new home of all persistent data: <c>%USERPROFILE%\Documents\AutoClicker</c>.</summary>
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoClicker");

    /// <summary>The legacy (pre-MSIX-fix) home: <c>%AppData%\AutoClicker</c>.</summary>
    public static string LegacyRootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoClicker");

    public static string SettingsPath => Path.Combine(RootDir, "settings.json");
    public static string ProfilesPath => Path.Combine(RootDir, "profiles.json");
    public static string SchedulesPath => Path.Combine(RootDir, "schedules.json");
    public static string LogsDir => Path.Combine(RootDir, "logs");

    /// <summary>
    /// Runs the one-time legacy → Documents migration. Safe to call repeatedly: it only
    /// runs once per process, and <see cref="Migrate"/> itself is guarded by a marker so a
    /// later process never re-migrates. Called from <see cref="Program.Main"/> before any
    /// store is read.
    /// </summary>
    public static void EnsureMigrated()
    {
        if (ensured) return;
        ensured = true;
        migrationNotice = Migrate(LegacyRootDir, RootDir) ? "Migrated data to Documents\\AutoClicker." : null;
    }

    /// <summary>The migration status line to show once, then cleared. Null when nothing moved.</summary>
    public static string? TakeMigrationNotice()
    {
        string? notice = migrationNotice;
        migrationNotice = null;
        return notice;
    }

    /// <summary>
    /// Moves the contents of <paramref name="legacyDir"/> into <paramref name="newRootDir"/>,
    /// leaving a marker file behind so the move never runs twice. Skips when the legacy
    /// location is missing, already migrated, or the new location already holds data (which
    /// must not be overwritten). Parameterized so tests can point at temp directories.
    /// Returns true when anything was moved.
    /// </summary>
    public static bool Migrate(string legacyDir, string newRootDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir)) return false;
            if (File.Exists(Path.Combine(newRootDir, MigrationMarker))) return false;
            if (Directory.Exists(newRootDir) && Directory.EnumerateFileSystemEntries(newRootDir).Any())
                return false; // don't overwrite data the new location already has

            Directory.CreateDirectory(newRootDir);
            foreach (string entry in Directory.EnumerateFileSystemEntries(legacyDir))
            {
                string dest = Path.Combine(newRootDir, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, dest);
                else File.Move(entry, dest, overwrite: true);
            }

            File.WriteAllText(Path.Combine(newRootDir, MigrationMarker),
                "AutoClicker legacy-data migration marker — "
                + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException
                                      or System.Security.SecurityException)
        {
            // Migration is best-effort: the app still starts, just without the legacy data.
            return false;
        }
    }
}
