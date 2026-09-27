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

    [Fact]
    public void Control_flow_fields_are_normalized()
    {
        var a = new SeqAction
        {
            Kind = ActionKind.Repeat,
            RepeatCount = -3,
            ConditionExpr = null!,
            VarName = null!,
            ValueExpr = null!,
            Label = null!,
        };
        a.Normalize();
        Assert.Equal(0, a.RepeatCount);
        Assert.Equal("", a.ConditionExpr);
        Assert.Equal("", a.VarName);
        Assert.Equal("", a.ValueExpr);
        Assert.Equal("", a.Label);
    }

    [Fact]
    public void FindImage_and_FindText_fields_are_normalized()
    {
        var a = new SeqAction
        {
            Kind = ActionKind.FindImage,
            MatchThreshold = 7.5,       // > 1
            SearchX = -999999,
            SearchY = 999999,
            SearchW = -4,
            SearchH = 999999,
            ClickOffsetX = -999999,
            ClickOffsetY = 999999,
            TextQuery = null!,
        };
        a.Normalize();

        Assert.Equal(1.0, a.MatchThreshold);
        Assert.Equal(-100000, a.SearchX);
        Assert.Equal(100000, a.SearchY);
        Assert.Equal(0, a.SearchW);
        Assert.Equal(100000, a.SearchH);
        Assert.Equal(-100000, a.ClickOffsetX);
        Assert.Equal(100000, a.ClickOffsetY);
        Assert.Equal("", a.TextQuery);

        // A negative threshold clamps to 0, and a NaN (a hand-corrupted file) snaps back to the default.
        var neg = new SeqAction { MatchThreshold = -1 };
        neg.Normalize();
        Assert.Equal(0.0, neg.MatchThreshold);

        var nan = new SeqAction { MatchThreshold = double.NaN };
        nan.Normalize();
        Assert.Equal(0.85, nan.MatchThreshold);
    }

    [Fact]
    public void Delay_random_percent_and_comment_are_normalized()
    {
        var a = new SeqAction { DelayRandomPercent = -5, Comment = null! };
        a.Normalize();
        Assert.Equal(0, a.DelayRandomPercent);
        Assert.Equal("", a.Comment);

        var over = new SeqAction { DelayRandomPercent = 500 };
        over.Normalize();
        Assert.Equal(100, over.DelayRandomPercent);
    }

    [Fact]
    public void Describe_appends_per_action_jitter_suffix_only_when_set()
    {
        var plain = new SeqAction { Kind = ActionKind.Wait, DelayMs = 100 };
        Assert.DoesNotContain("±", plain.Describe());

        var jittered = new SeqAction { Kind = ActionKind.Wait, DelayMs = 100, DelayRandomPercent = 25 };
        Assert.EndsWith("(±25%)", jittered.Describe());
    }
}
