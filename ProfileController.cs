namespace AutoClicker;

/// <summary>
/// What a profile operation changed, for the form to apply to its controls. Only the
/// form touches controls; this controller reports the outcome and leaves every UI update
/// (combo refresh, point list, hotkey re-registration, status line) to the caller.
/// </summary>
public sealed record ProfileOpResult
{
    /// <summary>Status line to show, if any.</summary>
    public string? Status { get; init; }

    /// <summary>Profile name to re-select in the combo after it is repopulated, if any.</summary>
    public string? SelectProfileName { get; init; }

    /// <summary>When non-null, the form replaces its in-memory <c>points</c> with this list.</summary>
    public List<SeqAction>? NewPoints { get; init; }

    /// <summary>True when profile hotkeys must be re-registered after the operation.</summary>
    public bool RegisterHotkeys { get; init; }

    /// <summary>True when profile hotkeys must be unregistered after the operation.</summary>
    public bool UnregisterHotkeys { get; init; }

    /// <summary>True when the form should reload the ad-hoc sequence (profiles turned off).</summary>
    public bool ReloadAdHocSequence { get; init; }
}

/// <summary>
/// Profile business logic with no WinForms dependency: holds the loaded profiles, the
/// active profile index and the load-failed gate, and performs every mutation (switch,
/// create, rename, duplicate, delete, save-points, hotkey assignment). Message boxes and
/// control updates are deliberately left to the form, so this class can be constructed
/// and driven directly in tests.
/// </summary>
internal sealed class ProfileController
{
    private const uint VK_F8 = 0x77;

    // Named, hotkey-switchable sequences. Empty by default: existing users have no
    // profiles.json yet, and the "use profiles" option defaults to off, so this never
    // changes behavior until the user opts in.
    private List<Profile> profiles = new();
    private bool loadFailed;

    public IReadOnlyList<Profile> Profiles => profiles;

    /// <summary>Index into <see cref="Profiles"/> of the profile currently loaded into the form's points; -1 = none.</summary>
    public int ActiveProfileIndex { get; private set; } = -1;

    /// <summary>Set when profiles.json existed but could not be read; every save is then refused.</summary>
    public bool LoadFailed => loadFailed;

    /// <summary>Name of the active profile, or null when none is selected.</summary>
    public string? ActiveProfileName =>
        ActiveProfileIndex >= 0 && ActiveProfileIndex < profiles.Count ? profiles[ActiveProfileIndex].Name : null;

    /// <summary>Index of the profile with the given name, or -1.</summary>
    public int IndexByName(string name) => profiles.FindIndex(p => p.Name == name);

    /// <summary>
    /// Loads profiles from <see cref="ProfileStore"/> and selects the profile named
    /// <paramref name="settings"/>.ActiveProfileName (falling back to the first profile).
    /// </summary>
    public void Load(AppSettings settings)
    {
        profiles = ProfileStore.Load(out loadFailed);
        ActiveProfileIndex = -1;
        if (profiles.Count > 0)
        {
            int idx = profiles.FindIndex(p => p.Name == settings.ActiveProfileName);
            if (idx < 0) idx = 0;
            ActiveProfileIndex = idx;
        }
    }

    // Auto-saves the outgoing profile's current edits before loading the new one, so
    // switching profiles can never silently discard a recorded sequence — the worst
    // outcome this feature could produce. Chosen over a confirm/discard prompt because a
    // profile switch is meant to be a quick, frequent action (that's the point of having
    // several), and a prompt on every switch would defeat that.
    public ProfileOpResult SwitchTo(int newIndex, List<SeqAction> currentPoints)
    {
        if (newIndex == ActiveProfileIndex) return new ProfileOpResult();
        if (ActiveProfileIndex >= 0 && ActiveProfileIndex < profiles.Count)
            SaveCurrentPointsToProfile(ActiveProfileIndex, currentPoints);

        ActiveProfileIndex = newIndex;
        if (newIndex >= 0 && newIndex < profiles.Count)
        {
            return new ProfileOpResult
            {
                NewPoints = profiles[newIndex].Actions.Select(a => a.Clone()).ToList(),
                Status = $"Switched to profile \"{profiles[newIndex].Name}\".",
            };
        }

        return new ProfileOpResult();
    }

    public ProfileOpResult NewProfile(string? rawName, List<SeqAction> currentPoints)
    {
        // The outgoing profile's edits would otherwise vanish the moment the new, empty
        // profile takes over the points list — same reasoning as SwitchTo.
        if (ActiveProfileIndex >= 0) SaveCurrentPointsToProfile(ActiveProfileIndex, currentPoints);

        string unique = ProfileStore.UniqueName(profiles, string.IsNullOrWhiteSpace(rawName) ? "Profile" : rawName.Trim());
        var p = new Profile { Name = unique };
        profiles.Add(p);
        Save();

        ActiveProfileIndex = profiles.Count - 1;

        return new ProfileOpResult
        {
            Status = $"Created profile \"{unique}\".",
            SelectProfileName = unique,
            NewPoints = p.Actions.Select(a => a.Clone()).ToList(),
            RegisterHotkeys = true,
        };
    }

    public ProfileOpResult RenameProfile(string? rawName)
    {
        if (ActiveProfileIndex < 0 || ActiveProfileIndex >= profiles.Count) return new ProfileOpResult();
        Profile p = profiles[ActiveProfileIndex];

        string desired = string.IsNullOrWhiteSpace(rawName) ? p.Name : rawName.Trim();
        string unique = ProfileStore.UniqueName(profiles.Where(x => x != p), desired);
        p.Name = unique;
        Save();

        return new ProfileOpResult
        {
            Status = $"Renamed to \"{unique}\".",
            SelectProfileName = unique,
        };
    }

    public ProfileOpResult DuplicateProfile(List<SeqAction> currentPoints)
    {
        if (ActiveProfileIndex < 0 || ActiveProfileIndex >= profiles.Count) return new ProfileOpResult();

        // Persist any in-progress edits into the source profile first, so the duplicate
        // reflects what's on screen rather than a stale on-disk copy.
        SaveCurrentPointsToProfile(ActiveProfileIndex, currentPoints);

        Profile source = profiles[ActiveProfileIndex];
        string unique = ProfileStore.UniqueName(profiles, source.Name + " (copy)");
        var copy = new Profile { Name = unique, Actions = source.Actions.Select(a => a.Clone()).ToList() };
        profiles.Add(copy);
        Save();

        ActiveProfileIndex = profiles.Count - 1;

        return new ProfileOpResult
        {
            Status = $"Duplicated \"{source.Name}\" as \"{unique}\".",
            SelectProfileName = unique,
            NewPoints = copy.Actions.Select(a => a.Clone()).ToList(),
        };
    }

    public ProfileOpResult DeleteProfile(int index, List<SeqAction> currentPoints)
    {
        if (index < 0 || index >= profiles.Count) return new ProfileOpResult();
        Profile p = profiles[index];

        profiles.RemoveAt(index);
        Save();
        ActiveProfileIndex = -1; // the deleted index no longer refers to anything

        string? nextName = profiles.Count > 0 ? profiles[0].Name : null;
        if (nextName != null)
        {
            ActiveProfileIndex = 0;
            return new ProfileOpResult
            {
                Status = $"Deleted profile \"{p.Name}\".",
                SelectProfileName = nextName,
                NewPoints = profiles[0].Actions.Select(a => a.Clone()).ToList(),
                RegisterHotkeys = true,
            };
        }

        return new ProfileOpResult
        {
            Status = $"Deleted profile \"{p.Name}\".",
            RegisterHotkeys = true,
        };
    }

    public ProfileOpResult SaveCurrentPointsToProfile(int index, List<SeqAction> currentPoints)
    {
        if (index < 0 || index >= profiles.Count) return new ProfileOpResult();
        profiles[index].Actions = currentPoints.Select(a => a.Clone()).ToList();
        Save();
        return new ProfileOpResult();
    }

    /// <summary>
    /// Assigns (or clears, with vk 0) a profile's hotkey, enforcing the collision rules:
    /// the main start/stop hotkey and F8 are rejected outright, and another profile's
    /// hotkey is first-claimed-wins. Returns a status message either way. Profile hotkeys
    /// carry no modifiers, so they only collide with an UNMODIFIED main hotkey of the same
    /// VK — Ctrl+F6 leaves plain F6 free for a profile.
    /// </summary>
    public ProfileOpResult SetHotkey(int profileIndex, uint vk, uint mainVk, uint mainModifiers)
    {
        if (profileIndex < 0 || profileIndex >= profiles.Count) return new ProfileOpResult();

        int sel = vk == 0 ? 0 : (int)(vk - 0x70 + 1);

        // Reject a collision with the main start/stop hotkey or F8 (ends recording): either
        // one would otherwise silently never fire once claimed here, or break recording.
        if (vk != 0 && ((mainModifiers == 0 && vk == mainVk) || vk == VK_F8))
        {
            return new ProfileOpResult
            {
                Status = mainModifiers == 0 && vk == mainVk
                    ? $"F{sel} is already the start/stop hotkey — pick a different key."
                    : $"F{sel} is reserved for ending recording — pick a different key.",
            };
        }

        // Reject a collision with another profile's hotkey too — first-claimed wins, same
        // rule ProfileStore.Load() already applies to a hand-edited profiles.json.
        if (vk != 0)
        {
            for (int i = 0; i < profiles.Count; i++)
            {
                if (i == profileIndex || profiles[i].HotkeyVk != vk) continue;
                return new ProfileOpResult
                {
                    Status = $"F{sel} is already assigned to profile \"{profiles[i].Name}\" — pick a different key.",
                };
            }
        }

        Profile p = profiles[profileIndex];
        p.HotkeyVk = vk;
        p.HotkeyName = vk == 0 ? "" : "F" + sel;
        Save();

        return new ProfileOpResult
        {
            Status = vk == 0 ? $"Removed hotkey from \"{p.Name}\"." : $"\"{p.Name}\" now runs on F{sel}.",
            RegisterHotkeys = true,
        };
    }

    /// <summary>
    /// Applies the "Use profiles" checkbox. Turning on loads the active profile's actions;
    /// turning off saves the outgoing profile's edits and asks the form to reload the ad-hoc
    /// sequence (exactly the pre-profiles behavior).
    /// </summary>
    public ProfileOpResult SetEnabled(bool enabled, List<SeqAction> currentPoints)
    {
        if (enabled)
        {
            if (ActiveProfileIndex >= 0 && ActiveProfileIndex < profiles.Count)
            {
                return new ProfileOpResult
                {
                    Status = $"Using profile \"{profiles[ActiveProfileIndex].Name}\".",
                    NewPoints = profiles[ActiveProfileIndex].Actions.Select(a => a.Clone()).ToList(),
                    RegisterHotkeys = true,
                };
            }

            return new ProfileOpResult
            {
                Status = "No profiles yet — click New to create one.",
                RegisterHotkeys = true,
            };
        }

        // Save whatever is on screen back to the active profile before returning to the
        // single ad-hoc sequence, then fall back to exactly the pre-profiles behavior.
        if (ActiveProfileIndex >= 0 && ActiveProfileIndex < profiles.Count)
            SaveCurrentPointsToProfile(ActiveProfileIndex, currentPoints);

        return new ProfileOpResult
        {
            UnregisterHotkeys = true,
            ReloadAdHocSequence = true,
        };
    }

    // Single gate for every profile write. Refuses to save when the file failed to load,
    // so a parse error can never be promoted into permanent data loss by the next save.
    private void Save()
    {
        if (loadFailed) return;
        ProfileStore.Save(profiles);
    }
}
