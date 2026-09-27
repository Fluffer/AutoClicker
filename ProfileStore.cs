using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// One named, hotkey-bound sequence. Profiles let a user switch between several
/// sequences at will (e.g. F1 runs a farming loop, F2 runs a login macro) instead
/// of the single ad-hoc sequence the app supported before profiles existed.
/// </summary>
public sealed class Profile
{
    public string Name { get; set; } = "";

    /// <summary>Virtual-key code of this profile's hotkey; 0 means none.</summary>
    public uint HotkeyVk { get; set; }
    public string HotkeyName { get; set; } = "";
    public List<SeqAction> Actions { get; set; } = new();

    /// <summary>
    /// Process name (without ".exe", case-insensitive) whose foreground presence
    /// auto-switches to this profile. "" = no auto-switch. Additive field: older profiles.json
    /// files simply lack it and deserialize to "".
    /// </summary>
    public string TargetProcess { get; set; } = "";

    private const uint MinFunctionKeyVk = 0x70; // F1
    private const uint MaxFunctionKeyVk = 0x7B; // F12
    private const int MaxNameLength = 60;

    /// <summary>Clamps anything a hand-edited or corrupt file might carry into a sane range.</summary>
    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        if (Name.Length > MaxNameLength) Name = Name[..MaxNameLength];

        Actions ??= new List<SeqAction>();
        // Same null-element hazard as in ProfileStore.Load: a literal null in the Actions
        // array must not survive to the NRE below (and Normalize is called from catch-free
        // paths in the CLI too).
        Actions.RemoveAll(a => a is null);
        foreach (SeqAction action in Actions) action.Normalize();

        // Only F1-F12 or "none" are valid; anything else (stale VK from a hand-edited
        // file, a key that's no longer a function key) collapses to "no hotkey".
        if (HotkeyVk != 0 && (HotkeyVk < MinFunctionKeyVk || HotkeyVk > MaxFunctionKeyVk)) HotkeyVk = 0;

        // Always recompute from the VK rather than trusting the stored name, so a
        // hand-edited file can't show "F3" for a key that's actually F7.
        HotkeyName = HotkeyVk == 0 ? "" : FunctionKeyName(HotkeyVk);

        TargetProcess = (TargetProcess ?? "").Trim();
    }

    private static string FunctionKeyName(uint vk) =>
        "F" + (vk - MinFunctionKeyVk + 1).ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Loads and saves the user's named sequence profiles, separate from <see cref="AppSettings"/>.
/// </summary>
internal static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Moves an unreadable profiles file aside so a later save cannot overwrite it. Best
    /// effort and silent: this runs on the failure path already, and a failure to preserve
    /// must not turn into a second failure on top of the first.
    /// </summary>
    private static void PreserveUnreadableFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            string kept = FilePath + ".corrupt";
            // Keep every casualty rather than overwriting an earlier one.
            for (int i = 2; File.Exists(kept) && i < 100; i++) kept = $"{FilePath}.corrupt{i}";
            File.Move(FilePath, kept, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing more we can do; the caller is already told not to save.
        }
    }

    /// <summary>Where profiles are read from and written to: <c>Documents\AutoClicker\profiles.json</c>.</summary>
    public static string FilePath { get; internal set; } = UserDataPaths.ProfilesPath;

    /// <summary>
    /// Loads profiles from <see cref="FilePath"/>. Never throws: a missing, empty, unreadable,
    /// or corrupt file yields an empty list instead of stopping the app from starting.
    /// </summary>
    public static List<Profile> Load() => Load(out _);

    /// <summary>
    /// As <see cref="Load()"/>, but reports whether an existing file failed to load.
    /// </summary>
    /// <param name="loadFailed">
    /// True when a file was present but could not be read or parsed. Callers MUST NOT save
    /// over the file in that case: an empty list saved atomically over a profile collection
    /// that merely failed to parse destroys every profile permanently. A missing file is
    /// not a failure — that is just a fresh install.
    /// </param>
    public static List<Profile> Load(out bool loadFailed)
    {
        loadFailed = false;
        List<Profile> loaded;
        try
        {
            string json = File.ReadAllText(FilePath);
            loaded = string.IsNullOrWhiteSpace(json)
                ? new List<Profile>()
                : JsonSerializer.Deserialize<List<Profile>>(json, JsonOptions) ?? new List<Profile>();
        }
        catch (FileNotFoundException) { loaded = new List<Profile>(); }
        catch (DirectoryNotFoundException) { loaded = new List<Profile>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or JsonException or NotSupportedException
                                      or ArgumentException or System.Security.SecurityException)
        {
            // The file exists but we could not use it. Preserve it under a new name before
            // anything can overwrite it — this is the user's recorded work, and a parse
            // error is far more likely to be a bug here than genuinely lost data.
            loadFailed = true;
            PreserveUnreadableFile();
            loaded = new List<Profile>();
        }

        var result = new List<Profile>();
        var claimedHotkeys = new HashSet<uint>();
        foreach (Profile profile in loaded)
        {
            // A JSON array element can be literal null ("[...] , null]" in a hand-edited
            // file). Normalize() would NRE on it and, being outside the try above, brick
            // every launch until the user found and deleted the file by hand.
            if (profile is null) continue;

            profile.Normalize();
            if (profile.Name.Length == 0) continue; // unusable without a name

            // Two profiles claiming the same hotkey means one would silently never
            // fire; keep whichever came first and strip the hotkey from the rest.
            if (profile.HotkeyVk != 0 && !claimedHotkeys.Add(profile.HotkeyVk))
            {
                profile.HotkeyVk = 0;
                profile.HotkeyName = "";
            }

            result.Add(profile);
        }

        return result;
    }

    /// <summary>
    /// Writes profiles to <see cref="FilePath"/> via a temp file plus rename, so an interrupted
    /// write can't leave a truncated profile collection behind. Returns false instead of
    /// throwing on IO/permission failure (the app also ships as MSIX, where writes are redirected).
    /// </summary>
    public static bool Save(IReadOnlyList<Profile> profiles)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Per-process temp name: the GUI and a CLI run can both be saving, and a shared
            // "profiles.json.tmp" would let one truncate the file the other is mid-write on.
            string tempPath = $"{FilePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(profiles, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or JsonException
                                      or ArgumentException or System.Security.SecurityException)
        {
            // JsonException belongs here too: serialization failing must return false like
            // any other write failure, not escape a method documented as never throwing.
            return false;
        }
    }

    /// <summary>Returns <paramref name="desired"/> if free, otherwise appends " (2)", " (3)"... until unique.</summary>
    public static string UniqueName(IEnumerable<Profile> existing, string desired)
    {
        var names = new HashSet<string>(existing.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(desired)) return desired;

        int suffix = 2;
        string candidate;
        do
        {
            candidate = $"{desired} ({suffix})";
            suffix++;
        } while (names.Contains(candidate));

        return candidate;
    }
}
