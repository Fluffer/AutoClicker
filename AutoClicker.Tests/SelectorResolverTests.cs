using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// Self-healing selector policy: the pure decision function and the .acseq round-trip of the
/// new selector fields. The UIA lookup itself is untestable headlessly and deliberately not
/// exercised here.
/// </summary>
public class SelectorResolverTests
{
    // ---- Selector serialization round-trip (no format bump; unknown fields are ignored) ----

    [Fact]
    public void Selector_fields_round_trip_through_acseq()
    {
        var a = new SeqAction
        {
            Kind = ActionKind.Click,
            X = 10,
            Y = 20,
            SelAutomationId = "btnLogin",
            SelName = "Log in",
            SelClass = "Button",
            PreferSelector = true,
        };

        string json = SequenceFile.Serialize(new[] { a });
        var back = SequenceFile.Deserialize(json);

        Assert.Single(back);
        Assert.Equal("btnLogin", back[0].SelAutomationId);
        Assert.Equal("Log in", back[0].SelName);
        Assert.Equal("Button", back[0].SelClass);
        Assert.True(back[0].PreferSelector);
    }

    [Fact]
    public void Selector_fields_default_to_empty_and_not_preferred()
    {
        var a = new SeqAction();

        Assert.Equal("", a.SelAutomationId);
        Assert.Equal("", a.SelName);
        Assert.Equal("", a.SelClass);
        Assert.False(a.PreferSelector);
    }

    [Fact]
    public void Null_selector_strings_from_hand_edited_files_become_empty()
    {
        var a = new SeqAction { SelAutomationId = null!, SelName = null!, SelClass = null! };
        a.Normalize();
        Assert.Equal("", a.SelAutomationId);
        Assert.Equal("", a.SelName);
        Assert.Equal("", a.SelClass);
    }

    [Fact]
    public void Older_file_without_selector_fields_loads_with_defaults()
    {
        // What a pre-self-healing build wrote: no Sel* fields, no PreferSelector.
        const string json = """{"FormatVersion":3,"Actions":[{"Kind":0,"X":1,"Y":2}]}""";
        var actions = SequenceFile.Deserialize(json);

        Assert.Single(actions);
        Assert.Equal("", actions[0].SelAutomationId);
        Assert.False(actions[0].PreferSelector);
    }

    // ---- HasSelector ----

    [Fact]
    public void HasSelector_requires_prefer_and_at_least_one_field()
    {
        Assert.False(SelectorResolver.HasSelector(new SeqAction { PreferSelector = false, SelAutomationId = "x" }));
        Assert.False(SelectorResolver.HasSelector(new SeqAction { PreferSelector = true }));
        Assert.False(SelectorResolver.HasSelector(new SeqAction
            { PreferSelector = true, SelAutomationId = "", SelName = "", SelClass = "" }));
        Assert.True(SelectorResolver.HasSelector(new SeqAction { PreferSelector = true, SelName = "OK" }));
    }

    // ---- ChooseResolution ----

    [Fact]
    public void Matched_selector_wins_over_coordinates()
    {
        Assert.Equal(ClickResolution.SelectorInvoke,
            SelectorResolver.ChooseResolution(preferSelector: true, hasSelector: true, selectorFound: true, anchorResolved: true));
        Assert.Equal(ClickResolution.SelectorInvoke,
            SelectorResolver.ChooseResolution(preferSelector: true, hasSelector: true, selectorFound: true, anchorResolved: false));
    }

    [Fact]
    public void Selector_not_preferred_or_absent_never_wins()
    {
        // Prefer off → point, even though a selector matched (it was never attempted).
        Assert.Equal(ClickResolution.Point,
            SelectorResolver.ChooseResolution(preferSelector: false, hasSelector: true, selectorFound: true, anchorResolved: true));
        // No selector recorded → point.
        Assert.Equal(ClickResolution.Point,
            SelectorResolver.ChooseResolution(preferSelector: true, hasSelector: false, selectorFound: false, anchorResolved: true));
    }

    [Fact]
    public void Miss_falls_back_to_the_point_path()
    {
        Assert.Equal(ClickResolution.Point,
            SelectorResolver.ChooseResolution(preferSelector: true, hasSelector: true, selectorFound: false, anchorResolved: true));
    }

    [Fact]
    public void Miss_with_a_gone_anchor_fails_loudly()
    {
        Assert.Equal(ClickResolution.PointUnresolvedAnchor,
            SelectorResolver.ChooseResolution(preferSelector: true, hasSelector: true, selectorFound: false, anchorResolved: false));
        // Even without a selector, an anchored action whose window is gone is unresolvable.
        Assert.Equal(ClickResolution.PointUnresolvedAnchor,
            SelectorResolver.ChooseResolution(preferSelector: false, hasSelector: false, selectorFound: false, anchorResolved: false));
    }
}
