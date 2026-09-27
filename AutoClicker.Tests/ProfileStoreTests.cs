using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// The C5 regression: a corrupt or hand-edited profiles.json must never brick startup.
/// Before the fix, a literal null element threw NullReferenceException OUTSIDE the
/// try/catch in Load, and the app crashed on every launch until the file was deleted.
/// </summary>
/// <remarks>
/// ProfileStore.FilePath is process-global, so every test here redirects it to a unique
/// temp file and restores it afterward. xUnit runs test CLASSES in parallel but tests
/// within a class serially — keeping all FilePath mutation in this one class is what
/// makes that safe.
/// </remarks>
[Collection("ProfileStore")]
public class ProfileStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalPath;

    public ProfileStoreTests()
    {
        _originalPath = ProfileStore.FilePath;
        _dir = Path.Combine(Path.GetTempPath(), "AutoClickerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Redirect BEFORE any test body runs: without this, tests would read and
        // (worse) overwrite the developer's real %AppData% profiles.
        ProfileStore.FilePath = Path.Combine(_dir, "profiles.json");
    }

    public void Dispose()
    {
        ProfileStore.FilePath = _originalPath;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteProfiles(string json) => File.WriteAllText(ProfileStore.FilePath, json);

    [Fact]
    public void Null_element_does_not_crash_and_is_skipped()
    {
        WriteProfiles("""[{"Name":"Farm loop","HotkeyVk":113,"Actions":[]}, null]""");

        var profiles = ProfileStore.Load(out bool failed);

        Assert.False(failed);
        Assert.Single(profiles);
        Assert.Equal("Farm loop", profiles[0].Name);
    }

    [Fact]
    public void Null_action_inside_profile_is_removed_before_normalize()
    {
        WriteProfiles("""[{"Name":"P","Actions":[{"X":1,"Y":2}, null]}]""");

        var profiles = ProfileStore.Load(out _);

        Assert.Single(profiles);
        Assert.Single(profiles[0].Actions);
    }

    [Fact]
    public void Corrupt_json_reports_failure_and_preserves_the_file()
    {
        WriteProfiles("{ this is not json");

        var profiles = ProfileStore.Load(out bool failed);

        Assert.True(failed);
        Assert.Empty(profiles);
        // The user's data must be moved aside, never overwritten by a later save.
        Assert.False(File.Exists(ProfileStore.FilePath));
        Assert.True(File.Exists(ProfileStore.FilePath + ".corrupt"));
    }

    [Fact]
    public void Missing_file_is_a_fresh_install_not_a_failure()
    {
        var profiles = ProfileStore.Load(out bool failed);
        Assert.False(failed);
        Assert.Empty(profiles);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var input = new List<Profile>
        {
            new() { Name = "Login macro", HotkeyVk = 0x72, Actions = { new SeqAction { X = 5, Y = 6 } } },
        };

        Assert.True(ProfileStore.Save(input));
        var loaded = ProfileStore.Load(out bool failed);

        Assert.False(failed);
        Assert.Single(loaded);
        Assert.Equal("Login macro", loaded[0].Name);
        Assert.Equal(0x72u, loaded[0].HotkeyVk);
        Assert.Equal("F3", loaded[0].HotkeyName);
        Assert.Single(loaded[0].Actions);
    }

    [Fact]
    public void Duplicate_hotkeys_are_stripped_from_later_profiles()
    {
        WriteProfiles("""[{"Name":"A","HotkeyVk":113},{"Name":"B","HotkeyVk":113}]""");

        var profiles = ProfileStore.Load(out _);

        Assert.Equal(2, profiles.Count);
        Assert.Equal(113u, profiles[0].HotkeyVk);
        Assert.Equal(0u, profiles[1].HotkeyVk); // second claimant loses the key
    }

    [Fact]
    public void Hand_edited_hotkey_outside_F1_F12_is_cleared()
    {
        WriteProfiles("""[{"Name":"A","HotkeyVk":65}]"""); // 'A' — not a function key

        var profiles = ProfileStore.Load(out _);

        Assert.Equal(0u, profiles[0].HotkeyVk);
        Assert.Equal("", profiles[0].HotkeyName);
    }

    [Fact]
    public void UniqueName_appends_numeric_suffixes_case_insensitively()
    {
        var existing = new List<Profile> { new() { Name = "Farm" }, new() { Name = "farm (2)" } };
        // Matching is case-insensitive; the returned name keeps the caller's casing
        // and only increments past every existing variant.
        Assert.Equal("farm (3)", ProfileStore.UniqueName(existing, "farm"));
        Assert.Equal("Farm (3)", ProfileStore.UniqueName(existing, "Farm"));
        Assert.Equal("New one", ProfileStore.UniqueName(existing, "New one"));
    }
}
