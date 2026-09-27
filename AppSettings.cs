using System.Globalization;
using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// Persisted UI state, round-tripped to <see cref="FilePath"/> between launches.
/// </summary>
public sealed class AppSettings
{
    public int IntervalHours { get; set; }
    public int IntervalMinutes { get; set; }
    public int IntervalSeconds { get; set; }
    public int IntervalMilliseconds { get; set; } = 100;

    /// <summary>0 left, 1 right, 2 middle.</summary>
    public int MouseButton { get; set; }

    /// <summary>0 single, 1 double, 2 hold.</summary>
    public int ClickType { get; set; }

    /// <summary>False means "repeat until stopped".</summary>
    public bool RepeatLimited { get; set; }
    public int RepeatCount { get; set; } = 1;

    /// <summary>False means "current cursor location".</summary>
    public bool UsePickedPosition { get; set; }
    public int PickedX { get; set; }
    public int PickedY { get; set; }

    public bool UseSequence { get; set; }
    public bool BackgroundMode { get; set; }
    public bool AnchorNewPoints { get; set; }
    public int JitterPixels { get; set; }
    public int JitterPercent { get; set; }

    /// <summary>Virtual-key code of the start/stop hotkey. F6 by default.</summary>
    public uint HotkeyVk { get; set; } = DefaultHotkeyVk;
    public string HotkeyName { get; set; } = DefaultHotkeyName;

    public int StartDelaySeconds { get; set; }
    public bool PanicKeyEnabled { get; set; } = true;

    /// <summary>Virtual-key code of the panic key. Esc (0x1B) by default; F1-F12 also allowed.</summary>
    public uint PanicKeyVk { get; set; } = DefaultPanicKeyVk;
    public string PanicKeyName { get; set; } = DefaultPanicKeyName;

    /// <summary>Stop the run when the cursor is parked in a screen corner (classic PyAutoGUI-style fail-safe).</summary>
    public bool CornerFailSafe { get; set; } = true;

    /// <summary>Stop the run after this many seconds. 0 = unlimited.</summary>
    public int MaxRunSeconds { get; set; }

    /// <summary>Write a per-step JSONL audit trail for each run (off by default).</summary>
    public bool RunLoggingEnabled { get; set; }

    /// <summary>Minimising hides to the tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }
    public string LastSequencePath { get; set; } = "";

    /// <summary>0 = light (classic), 1 = dark, 2 = follow the system colour mode.</summary>
    public int ColorMode { get; set; }

    /// <summary>Name of the selected profile; "" means none selected (use the ad-hoc sequence).</summary>
    public string ActiveProfileName { get; set; } = "";

    /// <summary>False keeps today's single-sequence behavior; true switches to named profiles.</summary>
    public bool UseProfiles { get; set; }

    private const uint MinFunctionKeyVk = 0x70; // F1
    private const uint MaxFunctionKeyVk = 0x7B; // F12
    private const uint DefaultHotkeyVk = 0x75; // F6
    private const string DefaultHotkeyName = "F6";
    private const uint EscapeKeyVk = 0x1B; // Esc
    private const uint DefaultPanicKeyVk = EscapeKeyVk;
    private const string DefaultPanicKeyName = "Esc";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where settings are read from and written to: <c>Documents\AutoClicker\settings.json</c>.</summary>
    public static string FilePath { get; } = UserDataPaths.SettingsPath;

    /// <summary>
    /// Loads settings from <see cref="FilePath"/>. Never throws: a missing, empty, unreadable,
    /// or corrupt file yields a fresh default instance instead of stopping the app from starting.
    /// </summary>
    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            string json = File.ReadAllText(FilePath);
            settings = string.IsNullOrWhiteSpace(json)
                ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// Writes settings to <see cref="FilePath"/> via a temp file plus rename, so an interrupted
    /// write can't leave a truncated settings file behind. Returns false instead of throwing on
    /// IO/permission failure (the app also ships as MSIX, where writes are redirected).
    /// </summary>
    public bool Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Per-process temp name (mirrors ProfileStore.Save): the GUI and a CLI run can
            // both be saving, and a shared "settings.json.tmp" would let one truncate the
            // file the other is mid-write on.
            string tempPath = $"{FilePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Clamps everything a hand-edited or corrupt file might carry into a sane range.</summary>
    public void Normalize()
    {
        IntervalHours = Math.Clamp(IntervalHours, 0, 999);
        IntervalMinutes = Math.Clamp(IntervalMinutes, 0, 59);
        IntervalSeconds = Math.Clamp(IntervalSeconds, 0, 59);
        IntervalMilliseconds = Math.Clamp(IntervalMilliseconds, 0, 999);
        MouseButton = Math.Clamp(MouseButton, 0, 2);
        ClickType = Math.Clamp(ClickType, 0, 2);
        RepeatCount = Math.Clamp(RepeatCount, 1, 1_000_000);
        JitterPixels = Math.Clamp(JitterPixels, 0, 500);
        JitterPercent = Math.Clamp(JitterPercent, 0, 100);
        StartDelaySeconds = Math.Clamp(StartDelaySeconds, 0, 300);
        MaxRunSeconds = Math.Max(0, MaxRunSeconds);
        ColorMode = Math.Clamp(ColorMode, 0, 2);
        PickedX = Math.Clamp(PickedX, -100_000, 100_000);
        PickedY = Math.Clamp(PickedY, -100_000, 100_000);

        HotkeyName ??= "";
        PanicKeyName ??= "";
        LastSequencePath ??= "";
        ActiveProfileName ??= "";

        if (HotkeyVk < MinFunctionKeyVk || HotkeyVk > MaxFunctionKeyVk)
        {
            HotkeyVk = DefaultHotkeyVk;
            HotkeyName = DefaultHotkeyName;
        }
        else
        {
            string expected = FunctionKeyName(HotkeyVk);
            if (HotkeyName != expected) HotkeyName = expected;
        }

        // Panic key: Esc or F1-F12. Anything else collapses to Esc (the default).
        if (PanicKeyVk != EscapeKeyVk && (PanicKeyVk < MinFunctionKeyVk || PanicKeyVk > MaxFunctionKeyVk))
        {
            PanicKeyVk = DefaultPanicKeyVk;
            PanicKeyName = DefaultPanicKeyName;
        }
        else
        {
            string expected = PanicKeyLabel(PanicKeyVk);
            if (PanicKeyName != expected) PanicKeyName = expected;
        }
    }

    private static string FunctionKeyName(uint vk) =>
        "F" + (vk - MinFunctionKeyVk + 1).ToString(CultureInfo.InvariantCulture);

    private static string PanicKeyLabel(uint vk) =>
        vk == EscapeKeyVk ? "Esc" : FunctionKeyName(vk);
}
