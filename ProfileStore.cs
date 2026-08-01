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

    private const uint MinFunctionKeyVk = 0x70; // F1
    private const uint MaxFunctionKeyVk = 0x7B; // F12
    private const int MaxNameLength = 60;

    /// <summary>Clamps anything a hand-edited or corrupt file might carry into a sane range.</summary>
    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        if (Name.Length > MaxNameLength) Name = Name[..MaxNameLength];

        Actions ??= new List<SeqAction>();
        foreach (SeqAction action in Actions) action.Normalize();

        // Only F1-F12 or "none" are valid; anything else (stale VK from a hand-edited
        // file, a key that's no longer a function key) collapses to "no hotkey".
        if (HotkeyVk != 0 && (HotkeyVk < MinFunctionKeyVk || HotkeyVk > MaxFunctionKeyVk)) HotkeyVk = 0;

        // Always recompute from the VK rather than trusting the stored name, so a
        // hand-edited file can't show "F3" for a key that's actually F7.
        HotkeyName = HotkeyVk == 0 ? "" : FunctionKeyName(HotkeyVk);
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

    /// <summary>Where profiles are read from and written to: <c>%AppData%\AutoClicker\profiles.json</c>.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoClicker", "profiles.json");

    /// <summary>
    /// Loads profiles from <see cref="FilePath"/>. Never throws: a missing, empty, unreadable,
    /// or corrupt file yields an empty list instead of stopping the app from starting.
    /// </summary>
    public static List<Profile> Load()
    {
        List<Profile> loaded;
        try
        {
            string json = File.ReadAllText(FilePath);
            loaded = string.IsNullOrWhiteSpace(json)
                ? new List<Profile>()
                : JsonSerializer.Deserialize<List<Profile>>(json, JsonOptions) ?? new List<Profile>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            loaded = new List<Profile>();
        }

        var result = new List<Profile>();
        var claimedHotkeys = new HashSet<uint>();
        foreach (Profile profile in loaded)
        {
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

            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(profiles, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
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
