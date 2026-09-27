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
    /// its whole block instead of the one action a gate is meant to suppress.
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

    /// <summary><see cref="ActionKind.WaitPixel"/> only: give up after this long. 0 waits indefinitely.</summary>
    public int PixelTimeoutMs { get; set; }

    /// <summary><see cref="ActionKind.WaitPixel"/> only: abort the whole run if the wait times out.</summary>
    public bool AbortRunOnTimeout { get; set; }

    /// <summary><see cref="ActionKind.WaitPixel"/> only: how often to re-sample while waiting.</summary>
    public int PollIntervalMs { get; set; } = 50;

    // ---- Control flow ----
    /// <summary>
    /// <see cref="ActionKind.Repeat"/> only: how many times to run the block this header
    /// opens. 0 means "until <see cref="ActionKind.Break"/> (or the run is stopped)".
    /// </summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>
    /// <see cref="ActionKind.IfElse"/> only: the condition expression, evaluated by the
    /// expression engine. When it is false the block is skipped past its EndBlock.
    /// There is deliberately no Else branch — write two IfElse blocks with inverted
    /// conditions instead.
    /// </summary>
    public string ConditionExpr { get; set; } = "";

    /// <summary><see cref="ActionKind.SetVar"/> only: the variable to assign.</summary>
    public string VarName { get; set; } = "";

    /// <summary><see cref="ActionKind.SetVar"/> only: the value expression to evaluate and store.</summary>
    public string ValueExpr { get; set; } = "";

    /// <summary><see cref="ActionKind.GotoLabel"/>/<see cref="ActionKind.Label"/> only: the label name.</summary>
    public string Label { get; set; } = "";

    /// <summary>Wait AFTER this action, before the next one.</summary>
    public int DelayMs { get; set; }

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
        HoldMs = Math.Max(0, HoldMs);
        ClickMethod = Math.Clamp(ClickMethod, 0, 2);
        DragMs = Math.Max(0, DragMs);
        if (!Enum.IsDefined(Kind)) Kind = ActionKind.Click;
        KeyCombo ??= "";
        Text ??= "";
        WindowClass ??= "";
        WindowTitle ??= "";
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
            _ => Kind.ToString(),
        };

        if (Kind == ActionKind.WaitPixel || IsControlKind(Kind) || Condition == PixelCondition.None) return baseDesc;

        string suffix = Condition == PixelCondition.IfMatch
            ? $"if {DescribeColor()} at {CondX},{CondY}"
            : $"unless {DescribeColor()} at {CondX},{CondY}";
        return $"{baseDesc} ({suffix})";
    }

    /// <summary>Target column for the sequence list.</summary>
    public string DescribeTarget()
    {
        // WaitPixel has no click target of its own; show the pixel it samples instead.
        if (Kind == ActionKind.WaitPixel) return string.Create(CultureInfo.InvariantCulture, $"{CondX}, {CondY}");
        if (Kind is ActionKind.Key or ActionKind.Text or ActionKind.Wait || IsControlKind(Kind)) return "—";
        string pos = string.Create(CultureInfo.InvariantCulture, $"{X}, {Y}");
        return WindowRelative ? $"{pos} in {Ellipsis(WindowLabel, 24)}" : pos;
    }

    /// <summary>The <see cref="CondColor"/> in <c>#RRGGBB</c> form, shared by the UI and the list.</summary>
    public string DescribeColor() => string.Create(CultureInfo.InvariantCulture, $"#{CondColor:X6}");

    public string WindowLabel =>
        !string.IsNullOrEmpty(WindowTitle) ? WindowTitle :
        !string.IsNullOrEmpty(WindowClass) ? WindowClass : "(unknown window)";

    /// <summary>True for the kinds that structure flow instead of performing input.</summary>
    internal static bool IsControlKind(ActionKind kind) => kind is
        ActionKind.Repeat or ActionKind.EndBlock or ActionKind.IfElse or ActionKind.SetVar
        or ActionKind.Break or ActionKind.GotoLabel or ActionKind.Label;

    private static string Ellipsis(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");

    /// <summary>A ListView subitem can't show line breaks, so make them visible instead.</summary>
    private static string SingleLine(string s) =>
        s.Replace("\r\n", "↵", StringComparison.Ordinal)
         .Replace('\n', '↵')
         .Replace('\r', '↵')
         .Replace('\t', '→');
}
