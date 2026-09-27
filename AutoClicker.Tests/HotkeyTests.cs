using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// F1 (Wave 1): the start/stop hotkey gained Ctrl/Alt/Shift/Win modifiers. These tests pin
/// the Normalize rules (what a hand-edited settings file may carry) and the combo display
/// string builder, both pure.
/// </summary>
public class HotkeyTests
{
    // ---- AppSettings.Normalize: modifier combos ----

    private static AppSettings Normalized(uint vk, uint mods)
    {
        var s = new AppSettings { HotkeyVk = vk, HotkeyModifiers = mods, HotkeyName = "" };
        s.Normalize();
        return s;
    }

    [Fact]
    public void Bare_letter_without_modifiers_falls_back_to_F6()
    {
        var s = Normalized(0x43, 0); // 'C'
        Assert.Equal(0x75u, s.HotkeyVk);
        Assert.Equal(0u, s.HotkeyModifiers);
        Assert.Equal("F6", s.HotkeyName);
    }

    [Fact]
    public void Bare_digit_without_modifiers_falls_back_to_F6()
    {
        var s = Normalized(0x30, 0); // '0'
        Assert.Equal(0x75u, s.HotkeyVk);
        Assert.Equal(0u, s.HotkeyModifiers);
    }

    [Fact]
    public void Letter_with_modifiers_is_kept()
    {
        var s = Normalized(0x43, HotkeyManager.ModCtrl | HotkeyManager.ModAlt); // Ctrl+Alt+C
        Assert.Equal(0x43u, s.HotkeyVk);
        Assert.Equal(HotkeyManager.ModCtrl | HotkeyManager.ModAlt, s.HotkeyModifiers);
        Assert.Equal("Ctrl+Alt+C", s.HotkeyName);
    }

    [Fact]
    public void Digit_with_modifier_is_kept()
    {
        var s = Normalized(0x30, HotkeyManager.ModCtrl); // Ctrl+0
        Assert.Equal(0x30u, s.HotkeyVk);
        Assert.Equal("Ctrl+0", s.HotkeyName);
    }

    [Fact]
    public void Function_key_alone_is_kept()
    {
        var s = Normalized(0x75, 0); // F6
        Assert.Equal(0x75u, s.HotkeyVk);
        Assert.Equal("F6", s.HotkeyName);
    }

    [Fact]
    public void Function_key_with_modifiers_is_kept()
    {
        var s = Normalized(0x70, HotkeyManager.ModShift); // Shift+F1
        Assert.Equal(0x70u, s.HotkeyVk);
        Assert.Equal("Shift+F1", s.HotkeyName);
    }

    [Fact]
    public void Modifiers_are_masked_to_four_bits()
    {
        var s = Normalized(0x75, 0x1F); // stray high bits
        Assert.Equal(0xFu, s.HotkeyModifiers);
    }

    [Fact]
    public void Non_key_with_modifiers_falls_back_to_F6()
    {
        var s = Normalized(0x5B, HotkeyManager.ModCtrl); // Win-key VK with Ctrl — not a letter/digit/F-key
        Assert.Equal(0x75u, s.HotkeyVk);
        Assert.Equal(0u, s.HotkeyModifiers);
    }

    // ---- FormatHotkey: combo display string ----

    [Theory]
    [InlineData(0x75, 0u, "F6")]
    [InlineData(0x75, 0x3u, "Ctrl+Alt+F6")]
    [InlineData(0x58, 0x6u, "Ctrl+Shift+X")] // 0x58 = 'X'
    [InlineData(0x30, 0x8u, "Win+0")]
    [InlineData(0x5A, 0x4u, "Shift+Z")]
    public void FormatHotkey_builds_the_display_combo(uint vk, uint mods, string expected) =>
        Assert.Equal(expected, HotkeyManager.FormatHotkey(vk, mods));
}
