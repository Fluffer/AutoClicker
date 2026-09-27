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
}
