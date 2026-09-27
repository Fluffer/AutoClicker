using System.Text.Json;
using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Round-trip and forward-compatibility rules for .acseq files. The version gate matters
/// because the previous format had none: an unknown action kind from a future build used
/// to collapse to a Click at its coordinates.
/// </summary>
public class SequenceFileTests
{
    private static List<SeqAction> Sample() => new()
    {
        new SeqAction { Kind = ActionKind.Click, X = 10, Y = 20, Button = 1 },
        new SeqAction { Kind = ActionKind.Key, KeyCombo = "Ctrl+C", DelayMs = 250 },
        new SeqAction { Kind = ActionKind.WaitPixel, Condition = PixelCondition.IfMatch, CondX = 5, CondY = 6 },
    };

    [Fact]
    public void Round_trip_preserves_actions_and_declares_current_version()
    {
        var original = Sample();
        string json = SequenceFile.Serialize(original);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal(SequenceFile.CurrentVersion, doc.RootElement.GetProperty("FormatVersion").GetInt32());

        var back = SequenceFile.Deserialize(json);
        Assert.Equal(original.Count, back.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Kind, back[i].Kind);
            Assert.Equal(original[i].X, back[i].X);
            Assert.Equal(original[i].KeyCombo, back[i].KeyCombo);
            Assert.Equal(original[i].DelayMs, back[i].DelayMs);
            Assert.Equal(original[i].Condition, back[i].Condition);
        }
    }

    [Fact]
    public void Legacy_bare_array_still_loads_as_version_1()
    {
        // Exactly what every release up to 1.1.0 wrote: a bare JSON array.
        const string legacy = """[{"X":1,"Y":2,"Button":0,"DoubleClick":false,"HoldMs":0}]""";
        var actions = SequenceFile.Deserialize(legacy);
        Assert.Single(actions);
        Assert.Equal(ActionKind.Click, actions[0].Kind); // absent Kind defaults to Click
        Assert.Equal(1, actions[0].X);
    }

    [Fact]
    public void Future_version_is_refused_with_a_clear_message()
    {
        const string future = """{"FormatVersion":99,"Actions":[{"Kind":7,"X":1,"Y":2}]}""";
        var ex = Assert.Throws<SequenceFile.UnsupportedVersionException>(
            () => SequenceFile.Deserialize(future));
        Assert.Contains("newer version", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void Null_elements_are_dropped_not_fatal()
    {
        const string json = """[{"X":1,"Y":2}, null, {"X":3,"Y":4}]""";
        var actions = SequenceFile.Deserialize(json);
        Assert.Equal(2, actions.Count);
    }

    [Fact]
    public void Garbage_is_a_JsonException_not_a_crash()
    {
        Assert.ThrowsAny<JsonException>(() => SequenceFile.Deserialize("not json at all"));
        Assert.ThrowsAny<JsonException>(() => SequenceFile.Deserialize("42"));
        Assert.ThrowsAny<JsonException>(() => SequenceFile.Deserialize("""{"FormatVersion":1}"""));
    }

    [Fact]
    public void Out_of_range_values_are_normalized_on_load()
    {
        const string json = """[{"Button":9,"DelayMs":-5,"PollIntervalMs":1}]""";
        var actions = SequenceFile.Deserialize(json);
        Assert.Equal(2, actions[0].Button);
        Assert.Equal(0, actions[0].DelayMs);
        Assert.Equal(10, actions[0].PollIntervalMs);
    }

    [Fact]
    public void Format_version_1_envelope_still_loads()
    {
        const string v1 = """{"FormatVersion":1,"Actions":[{"Kind":0,"X":4,"Y":5}]}""";
        var actions = SequenceFile.Deserialize(v1);
        Assert.Single(actions);
        Assert.Equal(ActionKind.Click, actions[0].Kind);
        Assert.Equal(4, actions[0].X);
    }

    [Fact]
    public void Format_version_2_file_loads_control_flow_kinds()
    {
        const string v2 = """{"FormatVersion":2,"Actions":[{"Kind":7,"RepeatCount":3},{"Kind":11}]}""";
        var actions = SequenceFile.Deserialize(v2);
        Assert.Equal(2, actions.Count);
        Assert.Equal(ActionKind.Repeat, actions[0].Kind);
        Assert.Equal(3, actions[0].RepeatCount);
        Assert.Equal(ActionKind.Break, actions[1].Kind);
    }

    [Fact]
    public void Format_version_4_is_refused()
    {
        const string future = """{"FormatVersion":4,"Actions":[{"Kind":7}]}""";
        var ex = Assert.Throws<SequenceFile.UnsupportedVersionException>(
            () => SequenceFile.Deserialize(future));
        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public void Format_version_3_file_loads_visual_targeting_kinds()
    {
        const string v3 = """{"FormatVersion":3,"Actions":[{"Kind":14,"MatchThreshold":0.9},{"Kind":15,"TextQuery":"Submit"}]}""";
        var actions = SequenceFile.Deserialize(v3);
        Assert.Equal(2, actions.Count);
        Assert.Equal(ActionKind.FindImage, actions[0].Kind);
        Assert.Equal(0.9, actions[0].MatchThreshold);
        Assert.Equal(ActionKind.FindText, actions[1].Kind);
        Assert.Equal("Submit", actions[1].TextQuery);
    }

    [Fact]
    public void Round_trip_preserves_FindImage_and_FindText_fields()
    {
        byte[] png = [1, 2, 3, 4, 5, 6, 7, 8];
        var original = new List<SeqAction>
        {
            new()
            {
                Kind = ActionKind.FindImage,
                TemplatePng = png,
                MatchThreshold = 0.9,
                SearchX = 1, SearchY = 2, SearchW = 300, SearchH = 200,
                ClickOnFound = true, ClickOffsetX = 5, ClickOffsetY = -3,
            },
            new() { Kind = ActionKind.FindText, TextQuery = "Submit", RegexQuery = true, ClickOnFound = false },
        };

        string json = SequenceFile.Serialize(original);
        var back = SequenceFile.Deserialize(json);

        Assert.Equal(2, back.Count);
        Assert.Equal(ActionKind.FindImage, back[0].Kind);
        Assert.Equal(png, back[0].TemplatePng);
        Assert.Equal(0.9, back[0].MatchThreshold);
        Assert.Equal(1, back[0].SearchX);
        Assert.Equal(2, back[0].SearchY);
        Assert.Equal(300, back[0].SearchW);
        Assert.Equal(200, back[0].SearchH);
        Assert.True(back[0].ClickOnFound);
        Assert.Equal(5, back[0].ClickOffsetX);
        Assert.Equal(-3, back[0].ClickOffsetY);

        Assert.Equal(ActionKind.FindText, back[1].Kind);
        Assert.Equal("Submit", back[1].TextQuery);
        Assert.True(back[1].RegexQuery);
        Assert.False(back[1].ClickOnFound);
    }

    [Fact]
    public void Round_trip_preserves_every_control_flow_kind()
    {
        var original = new List<SeqAction>
        {
            new() { Kind = ActionKind.Repeat, RepeatCount = 5 },
            new() { Kind = ActionKind.SetVar, VarName = "counter", ValueExpr = "counter + 1" },
            new() { Kind = ActionKind.IfElse, ConditionExpr = "counter < 5" },
            new() { Kind = ActionKind.Break },
            new() { Kind = ActionKind.EndBlock },
            new() { Kind = ActionKind.GotoLabel, Label = "farm" },
            new() { Kind = ActionKind.Label, Label = "farm" },
        };

        string json = SequenceFile.Serialize(original);
        var back = SequenceFile.Deserialize(json);

        Assert.Equal(original.Count, back.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Kind, back[i].Kind);
            Assert.Equal(original[i].RepeatCount, back[i].RepeatCount);
            Assert.Equal(original[i].VarName, back[i].VarName);
            Assert.Equal(original[i].ValueExpr, back[i].ValueExpr);
            Assert.Equal(original[i].ConditionExpr, back[i].ConditionExpr);
            Assert.Equal(original[i].Label, back[i].Label);
        }
    }
}
