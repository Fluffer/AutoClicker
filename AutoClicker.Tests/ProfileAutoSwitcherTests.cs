using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Wave 3 (F8): per-app profile auto-switching. The foreground-process → profile matching is
/// pure (<see cref="ProfileAutoSwitcher"/>) and tested headlessly; the 1.5 s timer glue and
/// GetForegroundWindow walking live in Form1 and are exercised by hand. The new
/// <see cref="Profile.TargetProcess"/> field is additive JSON and normalized here too.
/// </summary>
public class ProfileAutoSwitcherTests
{
    [Fact]
    public void ChooseTarget_matches_case_insensitively()
    {
        var targets = new[] { "Notepad", "Chrome" };

        Assert.Equal(0, ProfileAutoSwitcher.ChooseTarget(targets, "notepad"));
        Assert.Equal(1, ProfileAutoSwitcher.ChooseTarget(targets, "CHROME"));
    }

    [Fact]
    public void ChooseTarget_ignores_a_trailing_exe_suffix()
    {
        var targets = new[] { "notepad.exe", "explorer" };

        Assert.Equal(0, ProfileAutoSwitcher.ChooseTarget(targets, "notepad"));
        Assert.Equal(0, ProfileAutoSwitcher.ChooseTarget(targets, "NOTEPAD.EXE"));
    }

    [Fact]
    public void ChooseTarget_treats_empty_targets_as_inert()
    {
        var targets = new[] { "", "   ", "calc" };

        Assert.Equal(2, ProfileAutoSwitcher.ChooseTarget(targets, "calc"));
    }

    [Fact]
    public void ChooseTarget_returns_minus_one_when_nothing_matches()
    {
        var targets = new[] { "notepad", "calc" };

        Assert.Equal(-1, ProfileAutoSwitcher.ChooseTarget(targets, "mspaint"));
    }

    [Fact]
    public void ChooseTarget_returns_minus_one_for_a_blank_foreground()
    {
        var targets = new[] { "notepad" };

        Assert.Equal(-1, ProfileAutoSwitcher.ChooseTarget(targets, ""));
        Assert.Equal(-1, ProfileAutoSwitcher.ChooseTarget(targets, "   "));
    }

    [Fact]
    public void Profile_normalizes_a_null_target_process_to_empty()
    {
        var profile = new Profile { TargetProcess = null! };

        profile.Normalize();

        Assert.Equal("", profile.TargetProcess);
    }

    [Fact]
    public void Profile_normalizes_and_trims_target_process()
    {
        var profile = new Profile { TargetProcess = "  Notepad.exe  " };

        profile.Normalize();

        // Trim is applied; the ".exe" suffix is deliberately kept here (NormalizeProcessName
        // strips it only at match time, so the stored value stays human-readable).
        Assert.Equal("Notepad.exe", profile.TargetProcess);
    }
}
