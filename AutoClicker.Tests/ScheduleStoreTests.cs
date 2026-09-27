using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// ScheduleStore.FilePath is process-global, so tests redirect it to a temp file and restore
/// it afterward — the same seam pattern ProfileStoreTests uses.
/// </summary>
[Collection("ScheduleStore")]
public class ScheduleStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalPath;

    public ScheduleStoreTests()
    {
        _originalPath = ScheduleStore.FilePath;
        _dir = Path.Combine(Path.GetTempPath(), "AutoClickerSchedules_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ScheduleStore.FilePath = Path.Combine(_dir, "schedules.json");
    }

    public void Dispose()
    {
        ScheduleStore.FilePath = _originalPath;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Missing_file_is_a_fresh_install()
    {
        Assert.Empty(ScheduleStore.Load());
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var input = new List<ScheduledRun>
        {
            new()
            {
                Name = "Morning run",
                Time = "08:30",
                DaysOfWeek = { [1] = true, [3] = true }, // Mon + Wed
                ProfileName = "Login macro",
                Enabled = true,
            },
        };

        Assert.True(ScheduleStore.Save(input));
        var loaded = ScheduleStore.Load();

        Assert.Single(loaded);
        Assert.Equal("Morning run", loaded[0].Name);
        Assert.Equal("08:30", loaded[0].Time);
        Assert.True(loaded[0].DaysOfWeek[1]);
        Assert.True(loaded[0].DaysOfWeek[3]);
        Assert.False(loaded[0].DaysOfWeek[0]);
        Assert.Equal("Login macro", loaded[0].ProfileName);
    }

    [Fact]
    public void Null_element_is_skipped_and_corrupt_json_is_preserved()
    {
        File.WriteAllText(ScheduleStore.FilePath, "[{\"Name\":\"A\"}, null]");
        var list = ScheduleStore.Load();
        Assert.Single(list);

        File.WriteAllText(ScheduleStore.FilePath, "{ not json");
        Assert.Empty(ScheduleStore.Load());
        Assert.False(File.Exists(ScheduleStore.FilePath));
        Assert.True(File.Exists(ScheduleStore.FilePath + ".corrupt"));
    }

    [Fact]
    public void Hand_edited_days_array_is_normalized_to_length_seven()
    {
        File.WriteAllText(ScheduleStore.FilePath, """[{"Name":"A","DaysOfWeek":[true]}]""");

        var loaded = ScheduleStore.Load();

        Assert.Single(loaded);
        Assert.Equal(7, loaded[0].DaysOfWeek.Length);
        Assert.True(loaded[0].DaysOfWeek[0]);
        Assert.False(loaded[0].DaysOfWeek[1]);
    }

    [Fact]
    public void IsDue_respects_time_day_and_enabled_flag()
    {
        var s = new ScheduledRun { Time = "09:00", Enabled = true };
        s.Normalize();
        s.DaysOfWeek[(int)DayOfWeek.Monday] = true;

        Assert.True(s.IsDue(new DateTime(2026, 9, 28, 9, 0, 30)));   // Mon 09:00:30
        Assert.False(s.IsDue(new DateTime(2026, 9, 28, 9, 1, 0)));   // wrong minute
        Assert.False(s.IsDue(new DateTime(2026, 9, 29, 9, 0, 0)));   // Tuesday not selected
        s.Enabled = false;
        Assert.False(s.IsDue(new DateTime(2026, 9, 28, 9, 0, 30)));  // disabled
    }
}
