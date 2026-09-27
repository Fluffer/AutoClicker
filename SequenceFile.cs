using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// Reads and writes <c>.acseq</c> sequence files.
/// </summary>
/// <remarks>
/// Format history — the version field exists because the previous format had none, and
/// an unknown <see cref="ActionKind"/> used to collapse to <see cref="ActionKind.Click"/>
/// via <see cref="Enum.IsDefined"/>, i.e. a file from a future version could silently
/// become a click at its coordinates. Now:
/// <list type="bullet">
/// <item>Legacy files (a bare JSON array of actions) load as format 1 — that is what
/// every version up to 1.1.0 wrote, and they must keep loading forever.</item>
/// <item>Format 1 envelopes (<c>{"FormatVersion":1,"Actions":[...]}</c>) also still load:
/// they never contain control-flow kinds (7-13), which only exist since format 2.</item>
/// <item>Current files are format 3: <c>{"FormatVersion":3,"Actions":[...]}</c>, which may
/// contain the control-flow kinds (7-13) and the visual-targeting kinds (14-15). A format-2/3
/// file opened in an older build is refused (see <see cref="UnsupportedVersionException"/>)
/// before any unknown kind can collapse.</item>
/// <item>A file declaring a <b>higher</b> FormatVersion than this build knows is refused
/// with a clear message instead of being reinterpreted.</item>
/// </list>
/// </remarks>
internal static class SequenceFile
{
    /// <summary>The newest format version this build reads and writes.</summary>
    public const int CurrentVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class Envelope
    {
        public int FormatVersion { get; set; } = CurrentVersion;
        public List<SeqAction> Actions { get; set; } = new();
    }

    /// <summary>Thrown when the file is structurally valid JSON but from a newer app version.</summary>
    public sealed class UnsupportedVersionException : Exception
    {
        public UnsupportedVersionException(int found, int supported)
            : base($"This sequence was saved by a newer version of Auto Clicker " +
                   $"(format v{found}; this build reads up to v{supported}). Update the app to open it.") { }
    }

    public static string Serialize(IReadOnlyList<SeqAction> actions) =>
        JsonSerializer.Serialize(new Envelope { Actions = actions.ToList() }, JsonOptions);

    /// <summary>
    /// Parses sequence JSON (legacy array or current envelope). Normalizes every action
    /// and drops literal null elements, so a corrupt file can never NRE a caller.
    /// </summary>
    /// <exception cref="UnsupportedVersionException">The file targets a newer format.</exception>
    /// <exception cref="JsonException">The file is not valid sequence JSON.</exception>
    public static List<SeqAction> Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        List<SeqAction>? actions;
        if (root.ValueKind == JsonValueKind.Array)
        {
            actions = root.Deserialize<List<SeqAction>>();
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            int version = root.TryGetProperty("FormatVersion", out var ver) && ver.ValueKind == JsonValueKind.Number
                ? ver.GetInt32()
                : 1; // an envelope without the field is an early/v1 file
            if (version > CurrentVersion)
                throw new UnsupportedVersionException(version, CurrentVersion);

            if (!root.TryGetProperty("Actions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new JsonException("Sequence file has no Actions array.");
            actions = arr.Deserialize<List<SeqAction>>();
        }
        else
        {
            throw new JsonException("Sequence file must contain a JSON array or envelope object.");
        }

        var result = actions ?? new List<SeqAction>();
        result.RemoveAll(a => a is null);
        foreach (SeqAction a in result) a.Normalize();
        return result;
    }
}
