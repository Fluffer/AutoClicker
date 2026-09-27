using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// The recorder's hook-free core: raw mouse/keyboard events (with a timestamp in ms since
/// recording start) become <see cref="SeqAction"/> steps. Exercised directly — no message
/// loop, no hooks — so the event→action and timing rules are pinned without WinForms.
/// </summary>
public class RecordingAssemblerTests
{
    // ---- Mouse: click / double-click / drag / hold / wheel ----

    [Fact]
    public void Stationary_short_left_click_emits_plain_click()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 10, 20, 100);
        RecordingEmission? e = asm.MouseUp(0, 10, 20, 150);

        Assert.NotNull(e);
        var a = e.Value.Action;
        Assert.Equal(ActionKind.Click, a.Kind);
        Assert.Equal(0, a.Button);
        Assert.Equal(10, a.X);
        Assert.Equal(20, a.Y);
        Assert.False(a.DoubleClick);
        Assert.Equal(0, a.HoldMs);
        Assert.Equal(0, a.DelayMs);
        Assert.False(e.Value.ReplacesLast);
        Assert.Single(asm.Actions);
    }

    [Fact]
    public void Middle_click_emits_click_with_button_2()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(2, 10, 10, 100);
        RecordingEmission? e = asm.MouseUp(2, 10, 10, 140);

        Assert.NotNull(e);
        Assert.Equal(ActionKind.Click, e.Value.Action.Kind);
        Assert.Equal(2, e.Value.Action.Button);
    }

    [Fact]
    public void Two_quick_left_clicks_chain_into_double_click()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 10, 10, 100);
        RecordingEmission? first = asm.MouseUp(0, 10, 10, 130);
        asm.MouseDown(0, 10, 10, 300);
        RecordingEmission? second = asm.MouseUp(0, 11, 10, 330);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first.Value.ReplacesLast);
        Assert.True(second.Value.ReplacesLast);
        Assert.Single(asm.Actions);
        Assert.True(asm.Actions[0].DoubleClick);
        // The second click promoted the first action rather than creating a new one.
        Assert.Same(first.Value.Action, second.Value.Action);
    }

    [Fact]
    public void Two_clicks_outside_doubleclick_window_do_not_chain()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 10, 10, 100);
        asm.MouseUp(0, 10, 10, 130);
        asm.MouseDown(0, 10, 10, 900);
        RecordingEmission? e = asm.MouseUp(0, 10, 10, 930);

        Assert.NotNull(e);
        Assert.False(e.Value.ReplacesLast);
        Assert.Equal(2, asm.Actions.Count);
        Assert.False(asm.Actions[1].DoubleClick);
    }

    [Fact]
    public void Two_clicks_far_apart_do_not_chain()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 10, 10, 100);
        asm.MouseUp(0, 10, 10, 130);
        asm.MouseDown(0, 50, 10, 300);
        RecordingEmission? e = asm.MouseUp(0, 50, 10, 330);

        Assert.NotNull(e);
        Assert.False(e.Value.ReplacesLast);
        Assert.Equal(2, asm.Actions.Count);
    }

    [Fact]
    public void Movement_between_down_and_up_emits_drag()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 10, 10, 100);
        RecordingEmission? e = asm.MouseUp(0, 200, 40, 350);

        Assert.NotNull(e);
        var a = e.Value.Action;
        Assert.Equal(ActionKind.Drag, a.Kind);
        Assert.Equal(10, a.X);
        Assert.Equal(10, a.Y);
        Assert.Equal(200, a.EndX);
        Assert.Equal(40, a.EndY);
        Assert.Equal(250, a.DragMs);
    }

    [Fact]
    public void Stationary_press_held_long_emits_hold_click()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 5, 5, 100);
        RecordingEmission? e = asm.MouseUp(0, 6, 5, 500);

        Assert.NotNull(e);
        var a = e.Value.Action;
        Assert.Equal(ActionKind.Click, a.Kind);
        Assert.Equal(400, a.HoldMs);
    }

    [Fact]
    public void Wheel_emits_scroll_with_notches_and_direction()
    {
        var asm = new RecordingAssembler();
        RecordingEmission? e = asm.MouseWheel(120, horizontal: false, 5, 5, 200);

        Assert.NotNull(e);
        Assert.Equal(ActionKind.Scroll, e.Value.Action.Kind);
        Assert.Equal(1, e.Value.Action.ScrollNotches);
        Assert.False(e.Value.Action.Horizontal);
    }

    [Fact]
    public void Negative_wheel_emits_negative_notches_and_horizontal_flag()
    {
        var asm = new RecordingAssembler();
        RecordingEmission? e = asm.MouseWheel(-240, horizontal: true, 5, 5, 200);

        Assert.NotNull(e);
        Assert.Equal(-2, e.Value.Action.ScrollNotches);
        Assert.True(e.Value.Action.Horizontal);
    }

    [Fact]
    public void Fractional_wheel_is_ignored()
    {
        var asm = new RecordingAssembler();
        Assert.Null(asm.MouseWheel(60, horizontal: false, 5, 5, 200));
        Assert.Empty(asm.Actions);
    }

    // ---- Keyboard: bare keys, combos, modifiers, repeats ----

    [Fact]
    public void Bare_key_emits_its_own_name()
    {
        var asm = new RecordingAssembler();
        RecordingEmission? e = asm.KeyDown((int)Keys.A, 100);
        asm.KeyUp((int)Keys.A);

        Assert.NotNull(e);
        Assert.Equal(ActionKind.Key, e.Value.Action.Kind);
        Assert.Equal("A", e.Value.Action.KeyCombo);
        Assert.Single(asm.Actions);
    }

    [Fact]
    public void Modifier_combo_builds_ctrl_plus_key()
    {
        var asm = new RecordingAssembler();
        asm.KeyDown(0x11, 100);                 // Ctrl down
        RecordingEmission? e = asm.KeyDown((int)Keys.C, 120);
        asm.KeyUp((int)Keys.C);
        asm.KeyUp(0x11);

        Assert.NotNull(e);
        Assert.Equal("Ctrl+C", e.Value.Action.KeyCombo);
        Assert.True(InputSender.TryParseCombo(e.Value.Action.KeyCombo, out var mods, out ushort vk, out _));
        Assert.Single(mods);
        Assert.Equal((ushort)0x43, vk);
    }

    [Fact]
    public void Shift_modifier_round_trips()
    {
        var asm = new RecordingAssembler();
        asm.KeyDown(0x10, 0);                    // Shift down
        RecordingEmission? e = asm.KeyDown((int)Keys.A, 0);
        asm.KeyUp((int)Keys.A);
        asm.KeyUp(0x10);

        Assert.NotNull(e);
        Assert.Equal("Shift+A", e.Value.Action.KeyCombo);
        Assert.True(InputSender.TryParseCombo(e.Value.Action.KeyCombo, out _, out ushort vk, out _));
        Assert.Equal((ushort)Keys.A, vk);
    }

    [Fact]
    public void Multiple_modifiers_are_ordered_ctrl_shift_alt_win()
    {
        var asm = new RecordingAssembler();
        asm.KeyDown(0x5B, 0); // Win
        asm.KeyDown(0x12, 0); // Alt
        asm.KeyDown(0x10, 0); // Shift
        asm.KeyDown(0x11, 0); // Ctrl
        RecordingEmission? e = asm.KeyDown((int)Keys.R, 0);

        Assert.NotNull(e);
        Assert.Equal("Ctrl+Shift+Alt+Win+R", e.Value.Action.KeyCombo);
    }

    [Fact]
    public void Modifier_only_press_is_ignored()
    {
        var asm = new RecordingAssembler();
        asm.KeyDown(0x10, 100);
        asm.KeyUp(0x10);
        Assert.Empty(asm.Actions);
    }

    [Fact]
    public void Held_key_repeat_is_suppressed()
    {
        var asm = new RecordingAssembler();
        asm.KeyDown((int)Keys.A, 100);
        Assert.Null(asm.KeyDown((int)Keys.A, 120)); // auto-repeat
        asm.KeyUp((int)Keys.A);
        RecordingEmission? second = asm.KeyDown((int)Keys.A, 400);
        asm.KeyUp((int)Keys.A);

        Assert.NotNull(second);
        Assert.Equal(2, asm.Actions.Count);
        Assert.Equal(300, asm.Actions[1].DelayMs); // 400 - 100
    }

    [Fact]
    public void Right_click_is_never_recorded()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(1, 10, 10, 100);
        Assert.Null(asm.MouseUp(1, 10, 10, 150));
        Assert.Empty(asm.Actions);
    }

    [Fact]
    public void F8_is_never_recorded()
    {
        var asm = new RecordingAssembler();
        Assert.Null(asm.KeyDown(0x77, 100));
        asm.KeyUp(0x77);
        Assert.Empty(asm.Actions);
    }

    // ---- Timing ----

    [Fact]
    public void DelayMs_accumulates_as_gap_from_previous_action()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 10, 10, 100);
        asm.MouseUp(0, 10, 10, 150);   // click @150 → delay 0
        asm.KeyDown((int)Keys.A, 250); // key @250 → delay 100
        asm.KeyUp((int)Keys.A);
        asm.MouseDown(0, 10, 10, 500);
        asm.MouseUp(0, 10, 10, 520);   // click @520 → delay 270

        Assert.Equal(3, asm.Actions.Count);
        Assert.Equal(0, asm.Actions[0].DelayMs);
        Assert.Equal(100, asm.Actions[1].DelayMs);
        Assert.Equal(270, asm.Actions[2].DelayMs);
    }

    [Fact]
    public void DelayMs_is_clamped_to_zero_for_out_of_order_timestamps()
    {
        var asm = new RecordingAssembler();
        asm.MouseDown(0, 10, 10, 500);
        asm.MouseUp(0, 10, 10, 600);
        asm.MouseWheel(120, false, 10, 10, 100); // earlier timestamp → clamped to 0

        Assert.Equal(2, asm.Actions.Count);
        Assert.Equal(0, asm.Actions[1].DelayMs);
    }

    // ---- Round-trip: everything the recorder emits must parse back through the player ----

    [Theory]
    [InlineData((int)Keys.A)]
    [InlineData((int)Keys.Z)]
    [InlineData((int)Keys.D0)]
    [InlineData((int)Keys.D9)]
    [InlineData((int)Keys.F1)]
    [InlineData((int)Keys.F12)]
    [InlineData((int)Keys.Return)]
    [InlineData((int)Keys.Escape)]
    [InlineData((int)Keys.Tab)]
    [InlineData((int)Keys.Space)]
    [InlineData((int)Keys.Left)]
    [InlineData((int)Keys.Up)]
    [InlineData((int)Keys.Right)]
    [InlineData((int)Keys.Down)]
    [InlineData((int)Keys.Delete)]
    [InlineData((int)Keys.Home)]
    [InlineData((int)Keys.End)]
    [InlineData((int)Keys.PageUp)]
    [InlineData((int)Keys.PageDown)]
    [InlineData((int)Keys.Back)]
    [InlineData((int)Keys.Insert)]
    public void Recorded_key_name_round_trips_through_combo_parser(int vk)
    {
        var asm = new RecordingAssembler();
        RecordingEmission? e = asm.KeyDown(vk, 0);

        Assert.NotNull(e);
        string combo = e.Value.Action.KeyCombo;
        Assert.True(InputSender.TryParseCombo(combo, out _, out ushort parsed, out string error),
            $"{combo}: {error}");
        Assert.Equal((ushort)vk, parsed);
        asm.KeyUp(vk);
    }
}
