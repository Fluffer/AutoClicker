namespace AutoClicker;

/// <summary>
/// How a positioned <see cref="ActionKind.Click"/> resolves its target. The selector (a UI
/// Automation identity captured at record time) is the most semantic signal available; the
/// point (screen or window-anchored coordinates) is the fallback.
/// </summary>
internal enum ClickResolution
{
    /// <summary>Find the element by its recorded selector and invoke it.</summary>
    SelectorInvoke,

    /// <summary>Click at the resolved coordinates (screen or window-anchored).</summary>
    Point,

    /// <summary>
    /// The action is window-anchored and its window is gone, so there is no point to click —
    /// the runner fails the step loudly instead of clicking a stale position.
    /// </summary>
    PointUnresolvedAnchor,
}

/// <summary>
/// Pure self-healing playback policy. The decision logic is separated from the UI Automation
/// calls (which live in <see cref="UiaInvoker"/> and cannot be tested headlessly) so the
/// fallback rules can be unit-tested on their own. <see cref="SequenceRunner"/> implements the
/// same policy inline for a Click: it short-circuits on a matched selector and otherwise falls
/// through to the coordinate path, whose own anchor check reports a missing window.
/// </summary>
internal static class SelectorResolver
{
    /// <summary>
    /// True when this action carries a usable self-healing selector: the user opted in
    /// (<see cref="SeqAction.PreferSelector"/>) and at least one selector field was recorded.
    /// </summary>
    internal static bool HasSelector(SeqAction a) =>
        a.PreferSelector
        && (!string.IsNullOrEmpty(a.SelAutomationId)
            || !string.IsNullOrEmpty(a.SelName)
            || !string.IsNullOrEmpty(a.SelClass));

    /// <summary>
    /// Picks the resolution path from the four facts that decide it.
    /// <list type="number">
    /// <item>A matched selector wins outright — it is the most semantic target and even beats a
    /// resolved anchor, since a control can move inside a window that has not.</item>
    /// <item>Otherwise the coordinate path is used. When the action is anchored to a window that
    /// no longer resolves (<paramref name="anchorResolved"/> is false), there is no point left to
    /// click and the run must fail loudly rather than click a stale position.</item>
    /// </list>
    /// </summary>
    /// <param name="preferSelector">The user opted into self-healing for this action.</param>
    /// <param name="hasSelector">At least one selector field is present.</param>
    /// <param name="selectorFound">The selector matched a live element (and it was invoked).</param>
    /// <param name="anchorResolved">The window anchor resolved (true for a screen-relative point).</param>
    internal static ClickResolution ChooseResolution(bool preferSelector, bool hasSelector,
        bool selectorFound, bool anchorResolved)
    {
        if (preferSelector && hasSelector && selectorFound)
            return ClickResolution.SelectorInvoke;
        return anchorResolved ? ClickResolution.Point : ClickResolution.PointUnresolvedAnchor;
    }
}
