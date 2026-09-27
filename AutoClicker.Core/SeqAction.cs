using System.Globalization;
using System.Text.Json.Serialization;

namespace AutoClicker;

public enum ActionKind
{
    Click = 0,
    Drag = 1,
    Scroll = 2,
    Key = 3,
    Text = 4,
    Wait = 5,
    WaitPixel = 6,
    // Control flow. Values 7-13 must stay stable: FormatVersion 1 files never contain
    // them, and FormatVersion 2 files rely on their exact numeric meanings.
    Repeat = 7,
    EndBlock = 8,
    IfElse = 9,
    SetVar = 10,
    Break = 11,
    GotoLabel = 12,
    Label = 13,
    // Visual targeting. Values 14-15 must stay stable: FormatVersion 3 files rely on their
    // exact numeric meanings.
    FindImage = 14,
    FindText = 15,
    // Debugging / control flow (Wave 2). Values 16-17 must stay stable: FormatVersion 4
    // files rely on their exact numeric meanings.
    Breakpoint = 16,
    Else = 17,
}

/// <summary>
/// Whether a pixel-color check gates an action, and which way.
/// </summary>
/// <remarks>
/// The same enum drives two mechanisms depending on <see cref="SeqAction.Kind"/>:
/// on any ordinary action it is a gate (<see cref="None"/> always runs, <see cref="IfMatch"/>/
/// <see cref="IfNoMatch"/> run only when the pixel does/doesn't match); on a
/// <see cref="ActionKind.WaitPixel"/> action the same values instead describe what to wait
/// for, since a wait step has no action of its own to gate.
/// </remarks>
public enum PixelCondition
{
    None = 0,
    IfMatch = 1,
    IfNoMatch = 2,
}

/// <summary>
/// One step in a sequence.
/// </summary>
/// <remarks>
/// The property names are the on-disk <c>.acseq</c> format. Files written by the
/// click-only version of the app deserialize cleanly: they carry no <see cref="Kind"/>,
/// which defaults to <see cref="ActionKind.Click"/>, and every field they do carry
/// still means the same thing.
/// </remarks>
public sealed class SeqAction
{
    public ActionKind Kind { get; set; } = ActionKind.Click;

    /// <summary>Screen coordinates, or client coordinates when <see cref="WindowRelative"/>.</summary>
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>0 left, 1 right, 2 middle.</summary>
    public int Button { get; set; }

    public bool DoubleClick { get; set; }

    /// <summary>Click only: keep the button down this long before releasing.</summary>
    public int HoldMs { get; set; }

    /// <summary>0 = real input (SendInput), 1 = PostMessage, 2 = UI Automation invoke.</summary>
    public int ClickMethod { get; set; }

    // ---- Drag ----
    public int EndX { get; set; }
    public int EndY { get; set; }
    /// <summary>How long the travel from (X,Y) to (EndX,EndY) takes.</summary>
    public int DragMs { get; set; } = 300;

    // ---- Scroll ----
    /// <summary>Wheel notches; positive scrolls up (or right when horizontal).</summary>
    public int ScrollNotches { get; set; } = 3;
    public bool Horizontal { get; set; }

    // ---- Keyboard ----
    /// <summary>A combo such as <c>Ctrl+C</c>, <c>Alt+Tab</c>, <c>F5</c>, <c>Enter</c>.</summary>
    public string KeyCombo { get; set; } = "";
    /// <summary>Literal text to type, sent as Unicode so layout doesn't matter.</summary>
    public string Text { get; set; } = "";

    // ---- Window anchoring ----
    /// <summary>
    /// When true, X/Y (and EndX/EndY) are client coordinates inside the window
    /// identified by <see cref="WindowClass"/> + <see cref="WindowTitle"/>, resolved
    /// fresh on every pass. Sequences then survive the window being moved or the
    /// screen resolution changing.
    /// </summary>
    public bool WindowRelative { get; set; }
    public string WindowClass { get; set; } = "";
    public string WindowTitle { get; set; } = "";

    // ---- Self-healing selector (opt-in) ----
    // Captured at record time by snapshotting what UI Automation sees at the click point.
    // Playback only uses them when PreferSelector is set (off by default, so existing
    // sequences keep their exact current behavior). Older builds simply ignore these extra
    // fields when reading a file — they don't self-heal, but nothing breaks.
    public string SelAutomationId { get; set; } = "";
    public string SelName { get; set; } = "";
    public string SelClass { get; set; } = "";
    /// <summary>
    /// Opt-in: resolve this Click through its recorded UIA selector before falling back to
    /// coordinates. Off by default; enable in the editor or set it when authoring by hand.
    /// </summary>
    public bool PreferSelector { get; set; }

    // ---- Pixel condition / WaitPixel ----
    /// <summary>
    /// On any action, gates whether it runs at all: <see cref="PixelCondition.None"/> always
    /// runs it, <see cref="PixelCondition.IfMatch"/>/<see cref="PixelCondition.IfNoMatch"/> run
    /// it only when the pixel at (<see cref="CondX"/>,<see cref="CondY"/>) does/doesn't match
    /// <see cref="CondColor"/>. On a <see cref="ActionKind.WaitPixel"/> action there is nothing
    /// else to gate, so this instead says what to wait for: <see cref="PixelCondition.IfMatch"/>
    /// blocks until the pixel matches, <see cref="PixelCondition.IfNoMatch"/> blocks until it
    /// stops matching. A <see cref="ActionKind.WaitPixel"/> left at <see cref="PixelCondition.None"/>
    /// has nothing to wait for and the engine should treat it as a no-op.
    /// </summary>
    /// <remarks>
    /// A gate is IGNORED on control-flow kinds (<see cref="ActionKind.Repeat"/>, EndBlock,
    /// IfElse, SetVar, Break, GotoLabel, Label): they don't perform input, so there is
    /// nothing for a pixel colour to gate, and a "closed" loop header would silently skip
    /// its whole block instead of the one action a gate is meant to suppress. It is also
    /// ignored on the wait kinds <see cref="ActionKind.FindImage"/>/<see cref="ActionKind.FindText"/>,
    /// which — like <see cref="ActionKind.WaitPixel"/> — already have their own success
    /// criterion (the template/text appearing) and use the wait fields instead.
    /// </remarks>
    public PixelCondition Condition { get; set; } = PixelCondition.None;

    /// <summary>
    /// Screen coordinates of the pixel to sample. Unlike <see cref="X"/>/<see cref="Y"/>, these
    /// are never window-anchored: <see cref="WindowRelative"/> applies only to X/Y/EndX/EndY.
    /// </summary>
    public int CondX { get; set; }
    public int CondY { get; set; }

    /// <summary>Color to compare against, packed as 0xRRGGBB.</summary>
    public int CondColor { get; set; }

    /// <summary>Maximum allowed per-channel delta (0-255) for a color to still "match".</summary>
    public int CondTolerance { get; set; } = 10;

    /// <summary>
    /// Wait kinds (<see cref="ActionKind.WaitPixel"/>/<see cref="ActionKind.FindImage"/>/
    /// <see cref="ActionKind.FindText"/>): give up after this long. 0 waits indefinitely.
    /// </summary>
    public int PixelTimeoutMs { get; set; }

    /// <summary>Wait kinds: abort the whole run if the wait times out.</summary>
    public bool AbortRunOnTimeout { get; set; }

    /// <summary>Wait kinds: how often to re-sample/re-capture while waiting.</summary>
    public int PollIntervalMs { get; set; } = 50;

    // ---- Control flow ----
    /// <summary>
    /// <see cref="ActionKind.Repeat"/> only: how many times to run the block this header
    /// opens. 0 means "until <see cref="ActionKind.Break"/> (or the run is stopped)".
    /// </summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>
    /// <see cref="ActionKind.IfElse"/> only: the condition expression, evaluated by the
    /// expression engine. When it is false the block is skipped to a matching
    /// <see cref="ActionKind.Else"/> (if one exists between it and its EndBlock) and
    /// otherwise past its EndBlock.
    /// </summary>
    public string ConditionExpr { get; set; } = "";

    /// <summary><see cref="ActionKind.SetVar"/> only: the variable to assign.</summary>
    public string VarName { get; set; } = "";

    /// <summary><see cref="ActionKind.SetVar"/> only: the value expression to evaluate and store.</summary>
    public string ValueExpr { get; set; } = "";

    /// <summary><see cref="ActionKind.GotoLabel"/>/<see cref="ActionKind.Label"/> only: the label name.</summary>
    public string Label { get; set; } = "";

    // ---- Visual targeting (FindImage / FindText) ----

    /// <summary><see cref="ActionKind.FindImage"/> only: the template, PNG-encoded.</summary>
    /// <remarks>Serialized as base64 in JSON (<see cref="System.Text.Json"/> handles byte arrays automatically).</remarks>
    public byte[]? TemplatePng { get; set; }

    /// <summary><see cref="ActionKind.FindImage"/> only: minimum similarity (0..1) to count as found.</summary>
    public double MatchThreshold { get; set; } = 0.85;

    /// <summary>
    /// <see cref="ActionKind.FindImage"/>/<see cref="ActionKind.FindText"/>: the search rectangle
    /// in screen coordinates. Width/height 0 mean the full virtual screen.
    /// </summary>
    public int SearchX { get; set; }
    public int SearchY { get; set; }
    public int SearchW { get; set; }
    public int SearchH { get; set; }

    /// <summary>
    /// <see cref="ActionKind.FindImage"/>/<see cref="ActionKind.FindText"/>: click the found
    /// target's center once it is located. The click shares this action's
    /// <see cref="Button"/>/<see cref="DoubleClick"/>/<see cref="HoldMs"/>/<see cref="ClickMethod"/>
    /// semantics; when false the action only resolves and stores the location for later steps.
    /// </summary>
    public bool ClickOnFound { get; set; }

    /// <summary><see cref="ActionKind.FindImage"/>/<see cref="ActionKind.FindText"/>: added to the found center before clicking.</summary>
    public int ClickOffsetX { get; set; }
    public int ClickOffsetY { get; set; }

    /// <summary><see cref="ActionKind.FindText"/> only: the text to look for, matched case-insensitively.</summary>
    public string TextQuery { get; set; } = "";

    /// <summary><see cref="ActionKind.FindText"/> only: treat <see cref="TextQuery"/> as a regular expression.</summary>
    public bool RegexQuery { get; set; }

    /// <summary>Wait AFTER this action, before the next one.</summary>
    public int DelayMs { get; set; }

    /// <summary>
    /// Per-action ±% randomization of <see cref="DelayMs"/> at run time. 0 = use the run's
    /// global timing jitter instead; anything higher wins over the global value (the runner
    /// documents this precedence). 0..100.
    /// </summary>
    public int DelayRandomPercent { get; set; }

    /// <summary>Free-form note shown in the sequence list's Comment column. Purely cosmetic.</summary>
    public string Comment { get; set; } = "";

    /// <summary>
    /// Last window this action resolved to, so a fast sequence doesn't EnumWindows on
    /// every single pass. Runtime-only, and always revalidated before use.
    /// </summary>
    [JsonIgnore]
    internal IntPtr ResolvedWindow { get; set; }

    public SeqAction Clone() => (SeqAction)MemberwiseClone();

    /// <summary>Clamps anything a hand-edited or corrupt file might carry.</summary>
    public void Normalize()
    {
        Button = Math.Clamp(Button, 0, 2);
        DelayMs = Math.Max(0, DelayMs);
        DelayRandomPercent = Math.Clamp(DelayRandomPercent, 0, 100);
        Comment ??= "";
        HoldMs = Math.Max(0, HoldMs);
        ClickMethod = Math.Clamp(ClickMethod, 0, 2);
        DragMs = Math.Max(0, DragMs);
        if (!Enum.IsDefined(Kind)) Kind = ActionKind.Click;
        KeyCombo ??= "";
        Text ??= "";
        WindowClass ??= "";
        WindowTitle ??= "";
        SelAutomationId ??= "";
        SelName ??= "";
        SelClass ??= "";
        if (!Enum.IsDefined(Condition)) Condition = PixelCondition.None;
        CondX = Math.Clamp(CondX, -100000, 100000);
        CondY = Math.Clamp(CondY, -100000, 100000);
        CondColor = Math.Clamp(CondColor, 0x000000, 0xFFFFFF);
        CondTolerance = Math.Clamp(CondTolerance, 0, 255);
        PixelTimeoutMs = Math.Max(0, PixelTimeoutMs);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 10, 60000);
        RepeatCount = Math.Max(0, RepeatCount);
        ConditionExpr ??= "";
        VarName ??= "";
        ValueExpr ??= "";
        Label ??= "";
        MatchThreshold = double.IsNaN(MatchThreshold) ? 0.85 : Math.Clamp(MatchThreshold, 0.0, 1.0);
        SearchX = Math.Clamp(SearchX, -100000, 100000);
        SearchY = Math.Clamp(SearchY, -100000, 100000);
        SearchW = Math.Clamp(SearchW, 0, 100000);
        SearchH = Math.Clamp(SearchH, 0, 100000);
        ClickOffsetX = Math.Clamp(ClickOffsetX, -100000, 100000);
        ClickOffsetY = Math.Clamp(ClickOffsetY, -100000, 100000);
        TextQuery ??= "";
    }

    public string ButtonName => Button switch { 1 => "Right", 2 => "Middle", _ => "Left" };

    /// <summary>Human-readable summary for the sequence list.</summary>
    /// <remarks>
    /// A gate on a non-<see cref="ActionKind.WaitPixel"/> action appends a compact suffix
    /// (e.g. "(if #1E90FF at 400,300)") so the condition is visible without opening the editor.
    /// <see cref="ActionKind.WaitPixel"/> never gets that suffix: it *is* the gate, so its
    /// <see cref="Condition"/> is described as the wait itself instead.
    /// </remarks>
    public string Describe()
    {
        string baseDesc = Kind switch
        {
            ActionKind.Click => DoubleClick ? $"Double-click {ButtonName}"
                              : HoldMs > 0 ? $"Hold {ButtonName} {HoldMs} ms"
                              : $"Click {ButtonName}",
            ActionKind.Drag => $"Drag {ButtonName} to {EndX},{EndY} over {DragMs} ms",
            ActionKind.Scroll => $"Scroll {(Horizontal ? "horizontally " : "")}{ScrollNotches:+#;-#;0} notches",
            ActionKind.Key => $"Press {KeyCombo}",
            ActionKind.Text => $"Type \"{Ellipsis(SingleLine(Text), 32)}\"",
            ActionKind.Wait => "Wait",
            ActionKind.WaitPixel => Condition switch
            {
                PixelCondition.IfMatch => $"Wait until {DescribeColor()} at {CondX},{CondY}",
                PixelCondition.IfNoMatch => $"Wait while {DescribeColor()} at {CondX},{CondY}",
                _ => "Wait for pixel (no condition set)",
            },
            ActionKind.Repeat => RepeatCount > 0 ? $"Repeat {RepeatCount}×" : "Repeat until Break",
            ActionKind.EndBlock => "End block",
            ActionKind.IfElse => $"If {ConditionExpr}",
            ActionKind.SetVar => $"Set {VarName} = {ValueExpr}",
            ActionKind.Break => "Break",
            ActionKind.GotoLabel => $"Goto {Label}",
            ActionKind.Label => $"Label {Label}",
            ActionKind.FindImage => $"Find image ({Percent(MatchThreshold)}){(ClickOnFound ? " → click" : "")}",
            ActionKind.FindText => $"Find text \"{Ellipsis(SingleLine(TextQuery), 32)}\"{(ClickOnFound ? " → click" : "")}",
            ActionKind.Breakpoint => "Breakpoint",
            ActionKind.Else => "Else",
            _ => Kind.ToString(),
        };

        if (IsWaitKind(Kind) || IsControlKind(Kind) || Condition == PixelCondition.None)
        {
            // The ±% only shows when the action overrides the run's global timing jitter,
            // so a default (0) sequence stays as quiet as it always was.
            return DelayRandomPercent > 0 ? $"{baseDesc} (±{DelayRandomPercent}%)" : baseDesc;
        }

        string suffix = Condition == PixelCondition.IfMatch
            ? $"if {DescribeColor()} at {CondX},{CondY}"
            : $"unless {DescribeColor()} at {CondX},{CondY}";
        string gated = $"{baseDesc} ({suffix})";
        return DelayRandomPercent > 0 ? $"{gated} (±{DelayRandomPercent}%)" : gated;
    }

    /// <summary>Target column for the sequence list.</summary>
    public string DescribeTarget()
    {
        // WaitPixel has no click target of its own; show the pixel it samples instead.
        if (Kind == ActionKind.WaitPixel) return string.Create(CultureInfo.InvariantCulture, $"{CondX}, {CondY}");
        // FindImage targets a region; FindText targets a query string.
        if (Kind == ActionKind.FindImage) return DescribeSearchRegion();
        if (Kind == ActionKind.FindText) return Ellipsis(TextQuery, 32);
        if (Kind is ActionKind.Key or ActionKind.Text or ActionKind.Wait || IsControlKind(Kind)) return "—";
        string pos = string.Create(CultureInfo.InvariantCulture, $"{X}, {Y}");
        return WindowRelative ? $"{pos} in {Ellipsis(WindowLabel, 24)}" : pos;
    }

    /// <summary>The <see cref="CondColor"/> in <c>#RRGGBB</c> form, shared by the UI and the list.</summary>
    public string DescribeColor() => string.Create(CultureInfo.InvariantCulture, $"#{CondColor:X6}");

    public string WindowLabel =>
        !string.IsNullOrEmpty(WindowTitle) ? WindowTitle :
        !string.IsNullOrEmpty(WindowClass) ? WindowClass : "(unknown window)";

    /// <summary>
    /// One concatenated blob of everything a user could meaningfully search for in this
    /// action: the list's Action and Target columns, plus the key combo, typed text and
    /// comment. Kept here (rather than in the form) so Find &amp; Replace can search a plain
    /// string and the contents are unit-testable.
    /// </summary>
    public string SearchableText() =>
        Describe() + "\n" + DescribeTarget() + "\n" + KeyCombo + "\n" + Text + "\n" + Comment;

    /// <summary>True for the kinds that structure flow instead of performing input.</summary>
    internal static bool IsControlKind(ActionKind kind) => kind is
        ActionKind.Repeat or ActionKind.EndBlock or ActionKind.IfElse or ActionKind.SetVar
        or ActionKind.Break or ActionKind.GotoLabel or ActionKind.Label
        or ActionKind.Breakpoint or ActionKind.Else;

    /// <summary>
    /// True for the kinds whose own success criterion replaces a pixel gate: each waits for
    /// something on screen (a colour, a template, some text) and has nothing else to gate.
    /// </summary>
    internal static bool IsWaitKind(ActionKind kind) => kind is
        ActionKind.WaitPixel or ActionKind.FindImage or ActionKind.FindText;

    /// <summary>The search rectangle as a compact list/error string.</summary>
    internal string DescribeSearchRegion() =>
        SearchW <= 0 || SearchH <= 0
            ? "full screen"
            : string.Create(CultureInfo.InvariantCulture, $"{SearchW}×{SearchH} at {SearchX}, {SearchY}");

    private static string Ellipsis(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");

    /// <summary>0.85 → "85%".</summary>
    private static string Percent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(value * 100)}%");


    /// <summary>A ListView subitem can't show line breaks, so make them visible instead.</summary>
    private static string SingleLine(string s) =>
        s.Replace("\r\n", "↵", StringComparison.Ordinal)
         .Replace('\n', '↵')
         .Replace('\r', '↵')
         .Replace('\t', '→');
}
