using System.Text;

namespace AutoClicker;

/// <summary>
/// One action the assembler produced, plus whether it replaces the previously emitted action
/// (true only for double-click chaining, where the second click promotes the first instead of
/// appending a new step).
/// </summary>
internal readonly record struct RecordingEmission(SeqAction Action, bool ReplacesLast);

/// <summary>
/// Pure, hook-free core of the macro recorder. The low-level mouse/keyboard hooks translate
/// raw events (with a timestamp in milliseconds since recording started) into calls on this
/// class, and it assembles them into <see cref="SeqAction"/> steps whose
/// <see cref="SeqAction.DelayMs"/> reproduce the recorded pacing. Keeping this separate from
/// the hooks makes it unit-testable without a message loop, and keeping it free of WinForms
/// means it can be reasoned about on its own.
/// </summary>
/// <remarks>
/// Event→action rules:
/// <list type="bullet">
/// <item>A down→up pair with movement ≥ <see cref="DragThresholdPx"/> emits a
/// <see cref="ActionKind.Drag"/> (<c>DragMs</c> = hold time).</item>
/// <item>A stationary press held ≥ <see cref="HoldMinMs"/> emits a
/// <see cref="ActionKind.Click"/> with <c>HoldMs</c> = hold time.</item>
/// <item>A short stationary left click emits a <see cref="ActionKind.Click"/>; two within
/// <see cref="DoubleClickMs"/> and <see cref="DragThresholdPx"/> chain into one
/// <c>DoubleClick</c> click. Short right/middle clicks emit plain clicks.</item>
/// <item>A wheel event emits a <see cref="ActionKind.Scroll"/> (notches = delta / 120).</item>
/// <item>A non-modifier key-down emits a <see cref="ActionKind.Key"/> whose combo carries any
/// held modifiers (<c>Ctrl+C</c>). Auto-repeats and modifier-only presses are ignored.</item>
/// </list>
/// Right-clicks and F8 are ignored here as well as in the controller: the controller swallows
/// them to end recording, and this class refuses to emit them so the stop gesture can never
/// leak into the sequence through either path.
/// </remarks>
internal sealed class RecordingAssembler
{
    /// <summary>Mouse movement, in pixels, below which a down→up pair counts as a click.</summary>
    internal const int DragThresholdPx = 5;

    /// <summary>Two plain left clicks within this many ms chain into one double-click.</summary>
    internal const int DoubleClickMs = 400;

    /// <summary>A press held at least this long becomes a Click with <see cref="SeqAction.HoldMs"/>.</summary>
    internal const int HoldMinMs = 250;

    /// <summary>
    /// Consecutive printable key-downs within this many milliseconds of each other coalesce
    /// into one <see cref="ActionKind.Text"/> action instead of a Key action per character.
    /// </summary>
    internal const int CoalesceWindowMs = 400;

    // Generic virtual-key codes for the tracked modifiers. A low-level keyboard hook reports
    // the generic code for Shift/Control/Menu (not the left/right-specific ones); the Win key
    // reports left/right-specific codes, so both are treated as the one "Win" modifier.
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>The F8 end-recording key: never emitted as an action.</summary>
    private const int VK_F8 = 0x77;

    private readonly List<SeqAction> actions = new();

    // Milliseconds since recording start at which the previous action was emitted. -1 = none.
    private long lastActionTs = -1;

    // Pending mouse-down state per button (0 left, 1 right, 2 middle).
    private readonly int[] downX = new int[3];
    private readonly int[] downY = new int[3];
    private readonly long[] downTs = new long[3];
    private readonly bool[] hasDown = new bool[3];

    // Double-click chaining: time/position of the last plain left click emitted.
    private long lastLeftClickTs = -1;
    private int lastLeftClickX, lastLeftClickY;

    // Every key currently down, for auto-repeat suppression (a held key re-fires key-down).
    private readonly HashSet<int> heldKeys = new();

    // Pending coalesced text (Bartels "capture keyboard as combined text"): consecutive
    // printable key-downs accumulate here and flush as ONE Text action when a
    // non-coalescible event arrives, a typing gap exceeds CoalesceWindowMs, or recording
    // stops. pendingStartTs is the first character's timestamp (the Text action's delay is
    // measured from it); pendingLastTs is the last character's (the next action's delay is
    // measured from it).
    private readonly StringBuilder pendingText = new();
    private long pendingStartTs = -1;
    private long pendingLastTs = -1;

    // Text actions flushed as a side effect of a non-coalescible event. The controller
    // drains these after each event so the recorded list keeps recording order (a word
    // typed before a click must appear before the click).
    private readonly Queue<RecordingEmission> flushedEmissions = new();

    /// <summary>The actions assembled so far, in recording order.</summary>
    internal IReadOnlyList<SeqAction> Actions => actions;

    /// <summary>Records a button-down. Never emits (a gesture is only complete on its up).</summary>
    internal void MouseDown(int button, int x, int y, long timestampMs)
    {
        // The right button is the stop gesture: never recorded (see class remarks).
        if (button is < 0 or > 2 || button == 1) return;

        // A new down while one is already pending drops the old one. The controller only feeds
        // downs it wants recorded, so this is defensive against odd event sequences.
        downX[button] = x;
        downY[button] = y;
        downTs[button] = timestampMs;
        hasDown[button] = true;
    }

    /// <summary>
    /// Completes a down→up pair. Returns the action emitted, or null when there was no pending
    /// down for the button (a filtered or lost down). For a double-click chain the returned
    /// action is the PREVIOUS one (now promoted), flagged <see cref="RecordingEmission.ReplacesLast"/>.
    /// </summary>
    internal RecordingEmission? MouseUp(int button, int x, int y, long timestampMs)
    {
        // A mouse gesture is never coalescible: any pending typed text becomes its own
        // action before this gesture's action is emitted.
        FlushAndEnqueue();
        if (button is < 0 or > 2 || !hasDown[button]) return null;
        hasDown[button] = false;

        int sx = downX[button], sy = downY[button];
        long held = timestampMs - downTs[button];

        int dx = x - sx, dy = y - sy;
        bool moved = dx * dx + dy * dy > DragThresholdPx * DragThresholdPx;

        if (moved)
        {
            var drag = new SeqAction
            {
                Kind = ActionKind.Drag,
                Button = button,
                X = sx, Y = sy,
                EndX = x, EndY = y,
                DragMs = ClampMs(held),
            };
            ResetChain();
            Emit(drag, timestampMs);
            return new RecordingEmission(drag, false);
        }

        if (held >= HoldMinMs)
        {
            var hold = new SeqAction
            {
                Kind = ActionKind.Click,
                Button = button,
                X = sx, Y = sy,
                HoldMs = ClampMs(held),
            };
            ResetChain();
            Emit(hold, timestampMs);
            return new RecordingEmission(hold, false);
        }

        // Short, stationary press = a click. Double-click chaining applies to the left button
        // only; right/middle clicks are emitted as plain clicks.
        if (button == 0 && lastLeftClickTs >= 0 && timestampMs - lastLeftClickTs <= DoubleClickMs)
        {
            int cdx = sx - lastLeftClickX, cdy = sy - lastLeftClickY;
            if (cdx * cdx + cdy * cdy <= DragThresholdPx * DragThresholdPx)
            {
                // Promote the click already in the list instead of emitting a second one: the
                // runner plays its own double-click (a 15 ms gap), so a single Click with
                // DoubleClick=true reproduces the gesture.
                SeqAction prev = actions[^1];
                prev.DoubleClick = true;
                lastLeftClickTs = -1; // a third click starts a fresh chain
                return new RecordingEmission(prev, true);
            }
        }

        var click = new SeqAction
        {
            Kind = ActionKind.Click,
            Button = button,
            X = sx, Y = sy,
        };
        if (button == 0)
        {
            lastLeftClickTs = timestampMs;
            lastLeftClickX = sx;
            lastLeftClickY = sy;
        }
        else
        {
            ResetChain();
        }
        Emit(click, timestampMs);
        return new RecordingEmission(click, false);
    }

    /// <summary>Records a wheel event. <paramref name="wheelDelta"/> is the raw signed delta.</summary>
    internal RecordingEmission? MouseWheel(int wheelDelta, bool horizontal, int x, int y, long timestampMs)
    {
        // Same as MouseUp: a scroll is not coalescible, so pending text flushes first.
        FlushAndEnqueue();
        int notches = wheelDelta / Native.WHEEL_DELTA;
        if (notches == 0) return null; // a fractional notch has no playable equivalent

        var scroll = new SeqAction
        {
            Kind = ActionKind.Scroll,
            ScrollNotches = notches,
            Horizontal = horizontal,
            X = x, Y = y,
        };
        ResetChain();
        Emit(scroll, timestampMs);
        return new RecordingEmission(scroll, false);
    }

    /// <summary>
    /// Records a key-down. Modifier-only presses and auto-repeats are ignored. A printable
    /// character with no held modifiers coalesces into the pending text buffer; everything
    /// else (a modifier combo like <c>Ctrl+C</c>, a non-printable key) flushes that buffer
    /// and is emitted as a <see cref="ActionKind.Key"/> action whose combo carries any held
    /// modifiers. F8 is never emitted. Returns the action emitted, or null.
    /// </summary>
    internal RecordingEmission? KeyDown(int vk, long timestampMs)
    {
        if (vk == VK_F8) return null;       // the end-recording key is never an action
        if (!heldKeys.Add(vk)) return null; // auto-repeat: the key is already down
        if (IsModifier(vk)) return null;    // modifier-only presses have no action kind

        // Only emit keys whose Keys-enum name round-trips through InputSender.TryParseCombo.
        if (!Enum.IsDefined((Keys)vk)) return null;

        // A printable character with no held modifier coalesces into the pending text
        // buffer; it becomes an action only when the buffer is flushed (a non-coalescible
        // event, a typing gap longer than CoalesceWindowMs, or the recording ending).
        if (!HeldModifierDown() && PrintableChar(vk) is char ch)
        {
            if (pendingText.Length > 0 && timestampMs - pendingLastTs > CoalesceWindowMs)
            {
                // A pause longer than the coalescing window ends the previous word, so the
                // pause survives replay instead of being collapsed away.
                FlushAndEnqueue();
            }
            if (pendingText.Length == 0) pendingStartTs = timestampMs;
            pendingText.Append(ch);
            pendingLastTs = timestampMs;
            return null;
        }

        // A modifier combo or a non-printable key: any pending typed text is its own
        // action, and this key is a separate Key action.
        FlushAndEnqueue();

        string name = ((Keys)vk).ToString();
        List<string> mods = HeldModifiers();
        string combo = mods.Count == 0 ? name : string.Join("+", mods) + "+" + name;

        var key = new SeqAction { Kind = ActionKind.Key, KeyCombo = combo };
        ResetChain();
        Emit(key, timestampMs);
        return new RecordingEmission(key, false);
    }

    /// <summary>Records a key-up so repeat suppression and modifier tracking stay accurate.</summary>
    internal void KeyUp(int vk) => heldKeys.Remove(vk);

    private static bool IsModifier(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN;

    private bool HeldModifierDown() =>
        heldKeys.Contains(VK_CONTROL) || heldKeys.Contains(VK_SHIFT) ||
        heldKeys.Contains(VK_MENU) || heldKeys.Contains(VK_LWIN) || heldKeys.Contains(VK_RWIN);

    /// <summary>
    /// The unshifted character a key types on a US layout, for the keys worth coalescing
    /// (letters, digits, space, punctuation). Letters map to lowercase — a bare letter with
    /// no modifier types lowercase. Null for everything else (which stays a Key action).
    /// </summary>
    private static char? PrintableChar(int vk) => vk switch
    {
        >= (int)Keys.A and <= (int)Keys.Z => (char)('a' + (vk - (int)Keys.A)),
        >= (int)Keys.D0 and <= (int)Keys.D9 => (char)('0' + (vk - (int)Keys.D0)),
        (int)Keys.Space => ' ',
        (int)Keys.OemMinus => '-',
        (int)Keys.Oemplus => '=',
        (int)Keys.Oemcomma => ',',
        (int)Keys.OemPeriod => '.',
        (int)Keys.OemQuestion => '/',
        (int)Keys.OemSemicolon => ';',
        (int)Keys.OemQuotes => '\'',
        (int)Keys.OemOpenBrackets => '[',
        (int)Keys.OemCloseBrackets => ']',
        (int)Keys.OemBackslash => '\\',
        (int)Keys.Oemtilde => '`',
        _ => null,
    };

    /// <summary>
    /// Flushes the pending coalesced text as one <see cref="ActionKind.Text"/> action.
    /// Called by the controller when recording stops (via <see cref="Flush"/>) so text typed
    /// right before F8/right-click isn't lost.
    /// </summary>
    internal SeqAction? Flush() => FlushPendingText();

    /// <summary>
    /// Text actions flushed as a side effect of a non-coalescible event, in recording order.
    /// The controller drains this after each event and reports the actions before the
    /// event's own action.
    /// </summary>
    internal IReadOnlyList<RecordingEmission> TakeFlushedEmissions()
    {
        if (flushedEmissions.Count == 0) return Array.Empty<RecordingEmission>();
        var list = new List<RecordingEmission>(flushedEmissions.Count);
        while (flushedEmissions.Count > 0) list.Add(flushedEmissions.Dequeue());
        return list;
    }

    private void FlushAndEnqueue()
    {
        if (FlushPendingText() is { } text)
            flushedEmissions.Enqueue(new RecordingEmission(text, false));
    }

    /// <summary>
    /// Turns the pending text buffer into a Text action. DelayMs is measured from the FIRST
    /// character (the gap before the word); afterwards the action clock advances to the LAST
    /// character, so the next action's delay is measured from the end of the word, not its
    /// beginning — keeping replay timing exact.
    /// </summary>
    private SeqAction? FlushPendingText()
    {
        if (pendingText.Length == 0) return null;
        var text = new SeqAction { Kind = ActionKind.Text, Text = pendingText.ToString() };
        pendingText.Clear();
        ResetChain();
        text.DelayMs = lastActionTs < 0 ? 0 : (int)Math.Max(0, pendingStartTs - lastActionTs);
        text.Normalize();
        actions.Add(text);
        lastActionTs = pendingLastTs;
        pendingStartTs = -1;
        pendingLastTs = -1;
        return text;
    }

    /// <summary>Held modifiers in the canonical combo order the parser and UI both expect.</summary>
    private List<string> HeldModifiers()
    {
        var mods = new List<string>(4);
        if (heldKeys.Contains(VK_CONTROL)) mods.Add("Ctrl");
        if (heldKeys.Contains(VK_SHIFT)) mods.Add("Shift");
        if (heldKeys.Contains(VK_MENU)) mods.Add("Alt");
        if (heldKeys.Contains(VK_LWIN) || heldKeys.Contains(VK_RWIN)) mods.Add("Win");
        return mods;
    }

    /// <summary>Any non-left-click emission breaks a left double-click chain.</summary>
    private void ResetChain() => lastLeftClickTs = -1;

    /// <summary>Appends an action, computing its DelayMs from the gap since the previous one.</summary>
    private void Emit(SeqAction action, long timestampMs)
    {
        action.DelayMs = lastActionTs < 0 ? 0 : (int)Math.Max(0, timestampMs - lastActionTs);
        action.Normalize();
        actions.Add(action);
        lastActionTs = timestampMs;
    }

    private static int ClampMs(long ms) => (int)Math.Clamp(ms, 0, int.MaxValue);
}
