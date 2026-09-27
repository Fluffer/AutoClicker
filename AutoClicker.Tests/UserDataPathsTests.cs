using Xunit;

namespace AutoClicker.Tests;

public class UserDataPathsTests
{
    private static string TempDir(string tag) =>
        Path.Combine(Path.GetTempPath(), $"ac_{tag}_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Migrate_moves_files_and_logs_and_writes_marker()
    {
        string legacy = TempDir("legacy");
        string fresh = TempDir("docs");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "logs"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(legacy, "profiles.json"), "[]");
            File.WriteAllText(Path.Combine(legacy, "logs", "run-1.jsonl"), "{}");

            bool moved = UserDataPaths.Migrate(legacy, fresh);

            Assert.True(moved);
            Assert.True(File.Exists(Path.Combine(fresh, "settings.json")));
            Assert.True(File.Exists(Path.Combine(fresh, "profiles.json")));
            Assert.True(File.Exists(Path.Combine(fresh, "logs", "run-1.jsonl")));
            Assert.True(File.Exists(Path.Combine(fresh, ".migrated")));
            // Moved, not copied: the legacy copies are gone.
            Assert.False(File.Exists(Path.Combine(legacy, "settings.json")));
        }
        finally
        {
            try { Directory.Delete(legacy, recursive: true); } catch (IOException) { }
            try { Directory.Delete(fresh, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Migrate_is_a_no_op_when_legacy_is_missing()
    {
        string legacy = TempDir("none");
        string fresh = TempDir("docs");
        try
        {
            Assert.False(UserDataPaths.Migrate(legacy, fresh));
            Assert.False(Directory.Exists(fresh)); // nothing to migrate into
        }
        finally
        {
            try { Directory.Delete(fresh, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Migrate_does_not_overwrite_existing_new_data()
    {
        string legacy = TempDir("legacy");
        string fresh = TempDir("docs");
        try
        {
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "old");
            Directory.CreateDirectory(fresh);
            File.WriteAllText(Path.Combine(fresh, "settings.json"), "new");

            Assert.False(UserDataPaths.Migrate(legacy, fresh));
            Assert.Equal("new", File.ReadAllText(Path.Combine(fresh, "settings.json")));
        }
        finally
        {
            try { Directory.Delete(legacy, recursive: true); } catch (IOException) { }
            try { Directory.Delete(fresh, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Migrate_is_idempotent_once_marker_exists()
    {
        string legacy = TempDir("legacy");
        string fresh = TempDir("docs");
        try
        {
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
            Assert.True(UserDataPaths.Migrate(legacy, fresh));

            // A later process finds the marker and refuses to migrate again.
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "late.json"), "{}");
            Assert.False(UserDataPaths.Migrate(legacy, fresh));
        }
        finally
        {
            try { Directory.Delete(legacy, recursive: true); } catch (IOException) { }
            try { Directory.Delete(fresh, recursive: true); } catch (IOException) { }
        }
    }
}
