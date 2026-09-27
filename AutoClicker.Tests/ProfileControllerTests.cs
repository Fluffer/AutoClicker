using Xunit;

namespace AutoClicker.Tests;

// Both ProfileStoreTests and ProfileControllerTests redirect the process-global
// ProfileStore.FilePath. xUnit runs test CLASSES in parallel by default, so they must
// share a collection to stay serialized against each other.
[CollectionDefinition("ProfileStore")]
public sealed class ProfileStoreCollection { }

/// <summary>
/// Exercises <see cref="ProfileController"/> without WinForms: the controller is a plain
/// class, so the profile business rules can be verified directly.
/// </summary>
[Collection("ProfileStore")]
public class ProfileControllerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalPath;

    public ProfileControllerTests()
    {
        _originalPath = ProfileStore.FilePath;
        _dir = Path.Combine(Path.GetTempPath(), "AutoClickerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ProfileStore.FilePath = Path.Combine(_dir, "profiles.json");
    }

    public void Dispose()
    {
        ProfileStore.FilePath = _originalPath;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteProfiles(string json) => File.WriteAllText(ProfileStore.FilePath, json);

    [Fact]
    public void SwitchTo_saves_the_outgoing_profiles_edits()
    {
        WriteProfiles("""[{"Name":"A","Actions":[]},{"Name":"B","Actions":[]}]""");
        var controller = new ProfileController();
        controller.Load(new AppSettings { ActiveProfileName = "A" });

        var points = new List<SeqAction> { new() { X = 1, Y = 2 } };
        controller.SwitchTo(1, points);

        // Profile A's in-memory actions must now hold the edits that were on screen.
        Assert.Single(controller.Profiles[0].Actions);
        Assert.Equal(1, controller.Profiles[0].Actions[0].X);
        Assert.Equal(1, controller.ActiveProfileIndex);
    }

    [Fact]
    public void Rename_picks_a_unique_name_when_it_collides()
    {
        WriteProfiles("""[{"Name":"Farm","Actions":[]},{"Name":"Login","Actions":[]}]""");
        var controller = new ProfileController();
        controller.Load(new AppSettings { ActiveProfileName = "Farm" });

        var result = controller.RenameProfile("Login");

        Assert.Equal("Login (2)", controller.Profiles[0].Name);
        Assert.Equal("Renamed to \"Login (2)\".", result.Status);
    }

    [Fact]
    public void SetHotkey_rejects_a_collision_with_the_main_hotkey()
    {
        WriteProfiles("""[{"Name":"Farm","Actions":[]}]""");
        var controller = new ProfileController();
        controller.Load(new AppSettings { ActiveProfileName = "Farm" });

        var result = controller.SetHotkey(0, 0x75, 0x75, 0); // F6 == the main hotkey F6

        Assert.False(result.RegisterHotkeys);
        Assert.Equal("F6 is already the start/stop hotkey — pick a different key.", result.Status);
        Assert.Equal(0u, controller.Profiles[0].HotkeyVk); // unchanged
    }

    [Fact]
    public void Save_is_refused_after_load_failure()
    {
        WriteProfiles("{ this is not json");
        var controller = new ProfileController();
        controller.Load(new AppSettings { ActiveProfileName = "" });

        Assert.True(controller.LoadFailed);

        controller.NewProfile("New", new List<SeqAction>());

        // The profile exists in memory but nothing may be written back over the preserved file.
        Assert.Single(controller.Profiles);
        Assert.False(File.Exists(ProfileStore.FilePath));
        Assert.True(File.Exists(ProfileStore.FilePath + ".corrupt"));
    }
}
