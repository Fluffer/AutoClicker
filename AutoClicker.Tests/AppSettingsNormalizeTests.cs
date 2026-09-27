using Xunit;

namespace AutoClicker.Tests;

public class AppSettingsNormalizeTests
{
    [Fact]
    public void Out_of_range_values_are_clamped_to_sane_ranges()
    {
        var s = new AppSettings
        {
            IntervalHours = 5000,
            IntervalMinutes = 99,
            IntervalSeconds = -3,
            IntervalMilliseconds = 100000,
            RepeatCount = -7,
            JitterPixels = 1000,
            JitterPercent = 500,
            StartDelaySeconds = 9999,
            PickedX = -999999,
            MouseButton = 9,
            ClickType = 9,
        };
        s.Normalize();

        Assert.Equal(999, s.IntervalHours);
        Assert.Equal(59, s.IntervalMinutes);
        Assert.Equal(0, s.IntervalSeconds);
        Assert.Equal(999, s.IntervalMilliseconds);
        Assert.Equal(1, s.RepeatCount);
        Assert.Equal(500, s.JitterPixels);
        Assert.Equal(100, s.JitterPercent);
        Assert.Equal(300, s.StartDelaySeconds);
        Assert.Equal(-100000, s.PickedX);
        Assert.Equal(2, s.MouseButton);
        Assert.Equal(2, s.ClickType);
    }

    [Fact]
    public void Invalid_hotkey_falls_back_to_default_F6()
    {
        var s = new AppSettings { HotkeyVk = 0x41 /* 'A' */, HotkeyName = "A" };
        s.Normalize();
        Assert.Equal(0x75u, s.HotkeyVk); // F6
        Assert.Equal("F6", s.HotkeyName);
    }

    [Fact]
    public void HotkeyName_is_recomputed_from_the_VK_not_trusted_from_disk()
    {
        var s = new AppSettings { HotkeyVk = 0x72 /* F3 */, HotkeyName = "F11" };
        s.Normalize();
        Assert.Equal("F3", s.HotkeyName);
    }

    [Fact]
    public void Null_string_fields_from_hand_edited_files_become_empty()
    {
        var s = new AppSettings { HotkeyName = null!, LastSequencePath = null!, ActiveProfileName = null! };
        s.Normalize();
        Assert.Equal("", s.LastSequencePath);
        Assert.Equal("", s.ActiveProfileName);
        Assert.False(string.IsNullOrEmpty(s.HotkeyName)); // recomputed from the VK, never null
    }

    [Fact]
    public void Panic_key_defaults_to_esc()
    {
        var s = new AppSettings();
        s.Normalize();
        Assert.Equal(0x1Bu, s.PanicKeyVk);
        Assert.Equal("Esc", s.PanicKeyName);
    }

    [Fact]
    public void Invalid_panic_key_falls_back_to_esc()
    {
        var s = new AppSettings { PanicKeyVk = 0x41 /* 'A' */, PanicKeyName = "A" };
        s.Normalize();
        Assert.Equal(0x1Bu, s.PanicKeyVk);
        Assert.Equal("Esc", s.PanicKeyName);
    }

    [Fact]
    public void Panic_key_name_is_recomputed_from_the_vk_not_trusted_from_disk()
    {
        var s = new AppSettings { PanicKeyVk = 0x72 /* F3 */, PanicKeyName = "Esc" };
        s.Normalize();
        Assert.Equal("F3", s.PanicKeyName);
    }
}
