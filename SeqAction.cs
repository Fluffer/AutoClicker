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
        DragMs = Math.Max(0, DragMs);
        if (!Enum.IsDefined(Kind)) Kind = ActionKind.Click;
        KeyCombo ??= "";
        Text ??= "";
        WindowClass ??= "";
        WindowTitle ??= "";
    }

    public string ButtonName => Button switch { 1 => "Right", 2 => "Middle", _ => "Left" };

    /// <summary>Human-readable summary for the sequence list.</summary>
    public string Describe() => Kind switch
    {
        ActionKind.Click => DoubleClick ? $"Double-click {ButtonName}"
                          : HoldMs > 0 ? $"Hold {ButtonName} {HoldMs} ms"
                          : $"Click {ButtonName}",
        ActionKind.Drag => $"Drag {ButtonName} to {EndX},{EndY} over {DragMs} ms",
        ActionKind.Scroll => $"Scroll {(Horizontal ? "horizontally " : "")}{ScrollNotches:+#;-#;0} notches",
        ActionKind.Key => $"Press {KeyCombo}",
        ActionKind.Text => $"Type \"{Ellipsis(SingleLine(Text), 32)}\"",
        ActionKind.Wait => "Wait",
        _ => Kind.ToString(),
    };

    /// <summary>Target column for the sequence list.</summary>
    public string DescribeTarget()
    {
        if (Kind is ActionKind.Key or ActionKind.Text or ActionKind.Wait) return "—";
        string pos = string.Create(CultureInfo.InvariantCulture, $"{X}, {Y}");
        return WindowRelative ? $"{pos} in {Ellipsis(WindowLabel, 24)}" : pos;
    }

    public string WindowLabel =>
        !string.IsNullOrEmpty(WindowTitle) ? WindowTitle :
        !string.IsNullOrEmpty(WindowClass) ? WindowClass : "(unknown window)";

    private static string Ellipsis(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");

    /// <summary>A ListView subitem can't show line breaks, so make them visible instead.</summary>
    private static string SingleLine(string s) =>
        s.Replace("\r\n", "↵", StringComparison.Ordinal)
         .Replace('\n', '↵')
         .Replace('\r', '↵')
         .Replace('\t', '→');
}
