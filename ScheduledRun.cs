using System.Globalization;

namespace AutoClicker;

/// <summary>
/// One scheduled run: at a time of day, on selected days of the week, launch a sequence file
/// (or a saved profile) once. Schedules fire only while the app is open — a closed app fires
/// nothing, and Windows Task Scheduler is the future path for unattended runs.
/// </summary>
public sealed class ScheduledRun
{
    public string Name { get; set; } = "";

    /// <summary>Time of day as <c>HH:mm</c>, 24-hour.</summary>
    public string Time { get; set; } = "09:00";

    /// <summary>
    /// Which days of the week the schedule is active, indexed by <see cref="DayOfWeek"/>
    /// (Sunday = 0 … Saturday = 6). An empty array means "no days selected" (never fires).
    /// </summary>
    public bool[] DaysOfWeek { get; set; } = new bool[7];

    /// <summary>A sequence file (<c>.acseq</c>/<c>.json</c>) to run — used when <see cref="ProfileName"/> is empty.</summary>
    public string SequencePath { get; set; } = "";

    /// <summary>A saved profile to run — wins over <see cref="SequencePath"/> when non-empty.</summary>
    public string ProfileName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    private const int MaxNameLength = 60;

    /// <summary>Clamps anything a hand-edited or corrupt file might carry into a sane range.</summary>
    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        if (Name.Length > MaxNameLength) Name = Name[..MaxNameLength];

        Time = (Time ?? "").Trim();
        // A time that isn't HH:mm simply never matches, so IsDue() stays false for it.

        DaysOfWeek ??= new bool[7];
        if (DaysOfWeek.Length != 7)
        {
            var days = new bool[7];
            Array.Copy(DaysOfWeek, days, Math.Min(DaysOfWeek.Length, 7));
            DaysOfWeek = days;
        }

        SequencePath = (SequencePath ?? "").Trim();
        ProfileName = (ProfileName ?? "").Trim();
    }

    /// <summary>True when the schedule should fire at <paramref name="now"/>.</summary>
    public bool IsDue(DateTime now)
    {
        if (!Enabled || !TimeSpan.TryParse(Time, CultureInfo.InvariantCulture, out TimeSpan t)) return false;
        if (now.Hour != t.Hours || now.Minute != t.Minutes) return false;
        return DaysOfWeek.Length > (int)now.DayOfWeek && DaysOfWeek[(int)now.DayOfWeek];
    }

    /// <summary>Stable identity for the watcher's "already fired this minute" bookkeeping.</summary>
    internal string IdentityKey =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}|{Time}|{SequencePath}|{ProfileName}|{string.Join(',', DaysOfWeek)}");

    /// <summary>Human-readable target for the list UI.</summary>
    public string Target => !string.IsNullOrWhiteSpace(ProfileName) ? $"profile \"{ProfileName}\"" : SequencePath;
}
