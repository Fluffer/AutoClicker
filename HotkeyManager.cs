using System.Globalization;
using System.Text;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// Owns every global hotkey the app uses: the main start/stop toggle (F1-F12, optionally
/// with Ctrl/Alt/Shift/Win modifiers), the F8 end-recording key, the Esc panic key (armed
/// only during a run) and one hotkey per profile. Registration is separated from the form
/// because RegisterHotKey needs a real HWND, which does not exist during the constructor —
/// callers pass a lazy <see cref="Func{IntPtr}"/> and this class resolves it at the moment
/// each key is claimed. Status strings are reported through <paramref name="reportStatus"/>,
/// which the form wires to a label (null-safe, because the handle can be created before
/// BuildUi finishes and the label exists).
/// </summary>
internal sealed class HotkeyManager
{
    // Win32 RegisterHotKey fsModifiers bitmask values (MOD_*). HotkeyModifiers in settings
    // stores this exact bitmask. Profile hotkeys and the F8/panic keys always use 0.
    internal const uint ModAlt = 0x1;
    internal const uint ModCtrl = 0x2;
    internal const uint ModShift = 0x4;
    internal const uint ModWin = 0x8;

    /// <summary>All four modifier bits; the settings value is masked to this on load.</summary>
    internal const uint ModMask = 0xF;

    private const int HOTKEY_TOGGLE = 0xB001;
    private const int HOTKEY_RECEND = 0xB002;
    private const int HOTKEY_PANIC = 0xB003;
    private const int HOTKEY_PAUSE = 0xB004;
    // Per-profile hotkey ids start well clear of the three fixed ids above so a profile
    // can never collide with the toggle/end-recording/panic hotkeys by id.
    private const int ProfileHotkeyIdBase = 0xB010;
    private const uint VK_F7 = 0x76;
    private const uint VK_F8 = 0x77;

    private readonly Func<IntPtr> hwndProvider;
    private readonly Action<string> reportStatus;

    private bool panicRegistered;
    private bool pauseRegistered;
    private int profileCount;
    private readonly List<int> registeredProfileHotkeyIds = new();

    /// <summary>
    /// When false, <see cref="RegisterProfiles"/> only unregisters what it held and
    /// returns — the old "not using profiles" early-out from RegisterProfileHotkeys.
    /// </summary>
    public bool ProfilesEnabled { get; set; }

    public HotkeyManager(Func<IntPtr> hwndProvider, Action<string> reportStatus)
    {
        this.hwndProvider = hwndProvider;
        this.reportStatus = reportStatus;
    }

    /// <summary>Raised on the UI thread when the main toggle hotkey is pressed.</summary>
    public event Action? TogglePressed;

    /// <summary>Raised on the UI thread when the F8 end-recording hotkey is pressed.</summary>
    public event Action? EndRecordingPressed;

    /// <summary>Raised on the UI thread when the armed panic hotkey is pressed.</summary>
    public event Action? PanicPressed;

    /// <summary>Raised on the UI thread when the armed F7 pause/step hotkey is pressed.</summary>
    public event Action? PausePressed;

    /// <summary>Raised on the UI thread when a profile hotkey is pressed, with its profile index.</summary>
    public event Action<int>? ProfileHotkeyPressed;

    /// <summary>Routes a WM_HOTKEY id to the right event. Returns true when the id was handled.</summary>
    public bool TryHandle(int id)
    {
        if (id == HOTKEY_TOGGLE) { TogglePressed?.Invoke(); return true; }
        if (id == HOTKEY_RECEND) { EndRecordingPressed?.Invoke(); return true; }
        if (id == HOTKEY_PANIC) { PanicPressed?.Invoke(); return true; }
        if (id == HOTKEY_PAUSE) { PausePressed?.Invoke(); return true; }
        if (id >= ProfileHotkeyIdBase && id < ProfileHotkeyIdBase + profileCount)
        {
            ProfileHotkeyPressed?.Invoke(id - ProfileHotkeyIdBase);
            return true;
        }
        return false;
    }

    /// <summary>Registers the main start/stop hotkey and the F8 end-recording hotkey.</summary>
    public void RegisterMain(uint hotkeyVk, uint modifiers, string hotkeyName)
    {
        IntPtr hwnd = hwndProvider();
        UnregisterHotKey(hwnd, HOTKEY_TOGGLE);
        UnregisterHotKey(hwnd, HOTKEY_RECEND);
        bool okToggle = RegisterHotKey(hwnd, HOTKEY_TOGGLE, modifiers, hotkeyVk);
        // F8 ends recording. With modifiers the plain F8 stays free (Ctrl+F8 doesn't
        // clash with F8), so the skip only applies to an unmodified F8 main hotkey.
        bool okRecEnd = (hotkeyVk == VK_F8 && modifiers == 0)
            || RegisterHotKey(hwnd, HOTKEY_RECEND, 0, VK_F8);

        if (!okToggle)
            reportStatus($"{hotkeyName} is already claimed by another app — global hotkey OFF.\r\nUse the Start/Stop buttons, or pick a different key.");
        else if (!okRecEnd)
            reportStatus($"Ready. Press {hotkeyName} to start/stop. (F8 is taken — right-click ends recording.)");
    }

    /// <summary>Unregisters every hotkey this manager may have claimed.</summary>
    public void UnregisterAll()
    {
        IntPtr hwnd = hwndProvider();
        UnregisterHotKey(hwnd, HOTKEY_TOGGLE);
        UnregisterHotKey(hwnd, HOTKEY_RECEND);
        UnregisterPanic();
        UnregisterPause();
        UnregisterProfiles();
    }

    // The panic key is deliberately NOT registered at startup. RegisterHotKey grabs the
    // key globally, stealing it from every other app on the machine for as long as this
    // process is open — fine for the main hotkey/F8 (uncommon, user-chosen), but the panic
    // key (Esc by default, any F-key) is used constantly elsewhere (closing dialogs,
    // cancelling menus, games). So it is only ever armed for the duration of an actual run —
    // claimed when a run starts, released when it stops or the form closes — never at startup.
    public bool RegisterPanic(bool enabled, uint panicVk)
    {
        if (!enabled) { panicRegistered = false; return true; } // not requested; not a failure
        panicRegistered = RegisterHotKey(hwndProvider(), HOTKEY_PANIC, 0, panicVk);
        return panicRegistered;
    }

    public void UnregisterPanic()
    {
        if (!panicRegistered) return;
        UnregisterHotKey(hwndProvider(), HOTKEY_PANIC);
        panicRegistered = false;
    }

    // F7 toggles pause/resume during a run. Registered run-scoped (armed when a run starts,
    // released when it stops) for the same reason as the panic key: F7 is a common key
    // elsewhere (IDEs, games) and must not be claimed while idle.
    public bool RegisterPause()
    {
        pauseRegistered = RegisterHotKey(hwndProvider(), HOTKEY_PAUSE, 0, VK_F7);
        return pauseRegistered;
    }

    public void UnregisterPause()
    {
        if (!pauseRegistered) return;
        UnregisterHotKey(hwndProvider(), HOTKEY_PAUSE);
        pauseRegistered = false;
    }

    // Registers one global hotkey per profile that has one assigned, using ids
    // ProfileHotkeyIdBase + index so they can never collide with HOTKEY_TOGGLE/RECEND/PANIC.
    // Always unregisters everything currently held first: the profile count and hotkey
    // assignments can both change between calls (New/Rename/Duplicate/Delete/hotkey edit),
    // and a stale registration would otherwise keep claiming a key globally forever — the
    // same leak RegisterHotKey risks everywhere else it's used.
    public void RegisterProfiles(IReadOnlyList<Profile> profiles, uint mainVk, uint mainModifiers)
    {
        UnregisterProfiles();
        if (!ProfilesEnabled) return;

        profileCount = profiles.Count;
        IntPtr hwnd = hwndProvider();
        for (int i = 0; i < profiles.Count; i++)
        {
            uint vk = profiles[i].HotkeyVk;
            if (vk == 0) continue;
            // A collision with the main hotkey or F8 is already rejected at assignment time
            // (ProfileController.SetHotkey), so reaching here with one is only possible via
            // a hand-edited profiles.json — skip it rather than register something that
            // would silently steal F6/F8 from the rest of the app. Profile hotkeys are
            // registered unmodified, so they only clash with an unmodified main hotkey;
            // Ctrl+F6 and a profile F6 can coexist.
            if ((mainModifiers == 0 && vk == mainVk) || vk == VK_F8) continue;

            int id = ProfileHotkeyIdBase + i;
            if (RegisterHotKey(hwnd, id, 0, vk))
                registeredProfileHotkeyIds.Add(id);
            else
                reportStatus($"{profiles[i].HotkeyName} is already claimed by another app — \"{profiles[i].Name}\" hotkey OFF.");
        }
    }

    /// <summary>
    /// The display string for a hotkey combo, e.g. <c>Ctrl+Alt+F6</c> or <c>F6</c>. Extracted
    /// pure so the settings Normalize and the tests share one authoritative formatting rule
    /// (modifier order is fixed: Ctrl, Alt, Shift, Win).
    /// </summary>
    internal static string FormatHotkey(uint vk, uint modifiers)
    {
        var sb = new StringBuilder();
        if ((modifiers & ModCtrl) != 0) sb.Append("Ctrl+");
        if ((modifiers & ModAlt) != 0) sb.Append("Alt+");
        if ((modifiers & ModShift) != 0) sb.Append("Shift+");
        if ((modifiers & ModWin) != 0) sb.Append("Win+");
        sb.Append(KeyName(vk));
        return sb.ToString();
    }

    /// <summary>F1-F12, letters and digits by name; anything else by its numeric code.</summary>
    private static string KeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x7B => "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        _ => vk.ToString(CultureInfo.InvariantCulture),
    };

    public void UnregisterProfiles()
    {
        foreach (int id in registeredProfileHotkeyIds) UnregisterHotKey(hwndProvider(), id);
        registeredProfileHotkeyIds.Clear();
    }
}
