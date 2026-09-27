using Xunit;

namespace AutoClicker.Tests;

public class SeqActionNormalizeTests
{
    [Fact]
    public void Out_of_range_fields_are_clamped()
    {
        var a = new SeqAction
        {
            Button = 9,
            ClickMethod = 7,
            DelayMs = -5,
            HoldMs = -1,
            DragMs = -100,
            CondColor = 0x1FFFFFF, // > white
            CondTolerance = 999,
            PollIntervalMs = 1,    // below the 10 ms floor
            CondX = -999999,
        };
        a.Normalize();

        Assert.Equal(2, a.Button);
        Assert.Equal(2, a.ClickMethod);
        Assert.Equal(0, a.DelayMs);
        Assert.Equal(0, a.HoldMs);
        Assert.Equal(0, a.DragMs);
        Assert.Equal(0xFFFFFF, a.CondColor);
        Assert.Equal(255, a.CondTolerance);
        Assert.Equal(10, a.PollIntervalMs);
        Assert.Equal(-100000, a.CondX);
    }

    [Fact]
    public void Undefined_kind_collapses_to_Click()
    {
        var a = new SeqAction { Kind = (ActionKind)999 };
        a.Normalize();
        Assert.Equal(ActionKind.Click, a.Kind);
    }

    [Fact]
    public void Undefined_condition_collapses_to_None()
    {
        var a = new SeqAction { Condition = (PixelCondition)42 };
        a.Normalize();
        Assert.Equal(PixelCondition.None, a.Condition);
    }

    [Fact]
    public void Null_strings_from_hand_edited_files_become_empty()
    {
        var a = new SeqAction { KeyCombo = null!, Text = null!, WindowClass = null!, WindowTitle = null! };
        a.Normalize();
        Assert.Equal("", a.KeyCombo);
        Assert.Equal("", a.Text);
        Assert.Equal("", a.WindowClass);
        Assert.Equal("", a.WindowTitle);
    }

    [Fact]
    public void Clone_is_a_deep_copy_of_value_fields()
    {
        var a = new SeqAction { X = 10, Y = 20, KeyCombo = "Ctrl+C" };
        var b = a.Clone();
        b.X = 999;
        b.KeyCombo = "Ctrl+V";
        Assert.Equal(10, a.X);
        Assert.Equal("Ctrl+C", a.KeyCombo);
    }
}
