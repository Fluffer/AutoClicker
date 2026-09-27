using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Wave 3 (F6): the collapsed-sections persistence is a pure semicolon-separated string on
/// <see cref="AppSettings.CollapsedSections"/>, so the parse/serialize round-trip is tested
/// directly. The <see cref="CollapsibleSection"/> toggle itself is WinForms and is exercised
/// by hand, not here.
/// </summary>
public class CollapsedSectionsTests
{
    [Fact]
    public void Parse_empty_and_null_yield_no_sections()
    {
        Assert.Empty(CollapsedSections.Parse(null));
        Assert.Empty(CollapsedSections.Parse(""));
        Assert.Empty(CollapsedSections.Parse("   "));
    }

    [Fact]
    public void Parse_splits_on_semicolons_and_trims()
    {
        var set = CollapsedSections.Parse("interval; repeat ;cursor;;");

        Assert.Equal(3, set.Count);
        Assert.Contains("interval", set);
        Assert.Contains("repeat", set);
        Assert.Contains("cursor", set);
    }

    [Fact]
    public void Serialize_sorts_and_deduplicates()
    {
        string value = CollapsedSections.Serialize(new[] { "runoptions", "interval", "interval", "cursor" });

        Assert.Equal("cursor;interval;runoptions", value);
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        const string original = "cursor;interval;profiles;repeat;runoptions";

        string roundTripped = CollapsedSections.Serialize(CollapsedSections.Parse(original));

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void AppSettings_normalizes_null_collapsed_sections_to_empty()
    {
        var settings = new AppSettings { CollapsedSections = null! };

        settings.Normalize();

        Assert.Equal("", settings.CollapsedSections);
    }

    [Fact]
    public void AppSettings_defaults_to_everything_expanded()
    {
        var settings = new AppSettings();
        Assert.Equal("", settings.CollapsedSections);
    }
}
