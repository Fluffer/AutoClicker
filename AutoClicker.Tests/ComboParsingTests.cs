using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Combo parsing is the most hand-input-sensitive surface in the engine: every Key action
/// and the action editor's validation funnel through it.
/// </summary>
public class ComboParsingTests
{
    [Theory]
    [InlineData("Ctrl+C", 1, (ushort)0x43)]           // C
    [InlineData("Ctrl+Shift+F5", 2, (ushort)0x74)]    // F5
    [InlineData("Alt+Tab", 1, (ushort)0x09)]
    [InlineData("Enter", 0, (ushort)0x0D)]
    [InlineData("Esc", 0, (ushort)0x1B)]
    [InlineData("ctrl+alt+del", 2, (ushort)0x2E)]     // case-insensitive, Del alias
    [InlineData("Win+R", 1, (ushort)0x52)]
    [InlineData("5", 0, (ushort)0x35)]                // bare digit
    public void Valid_combos_parse(string combo, int modCount, ushort expectedVk)
    {
        bool ok = InputSender.TryParseCombo(combo, out var mods, out ushort vk, out string error);
        Assert.True(ok, error);
        Assert.Equal(modCount, mods.Count);
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Shift")]       // modifiers only
    [InlineData("Ctrl+A+B")]         // two non-modifier keys
    [InlineData("Ctrl+NotAKey")]     // unresolvable key
    public void Invalid_combos_are_rejected_with_a_message(string combo)
    {
        bool ok = InputSender.TryParseCombo(combo, out _, out _, out string error);
        Assert.False(ok);
        Assert.NotEqual("", error);
        Assert.NotNull(InputSender.DescribeComboProblem(combo));
    }

    [Fact]
    public void Valid_combo_has_no_described_problem()
    {
        Assert.Null(InputSender.DescribeComboProblem("Ctrl+C"));
    }
}
