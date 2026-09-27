using System.Text;
using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// The MCP tools the server exposes, plus the handler that runs each one. Kept separate from
/// the JSON-RPC plumbing so the tool set is easy to enumerate in tests and the server stays a
/// thin transport. Tool handlers run synchronously on the pipe loop thread — they are all
/// quick local operations except <c>run_sequence</c>, which hands off to
/// <see cref="McpRunHost"/> and returns immediately.
/// </summary>
internal sealed class McpToolRegistry
{
    /// <summary>One tool's list/metadata entry. <see cref="InputSchemaJson"/> is a JSON Schema object as a JSON string.</summary>
    public sealed record Tool(string Name, string Description, string InputSchemaJson);

    /// <summary>A tool's result: the text to return, and whether it represents a failure.</summary>
    public sealed record ToolResult(string Text, bool IsError)
    {
        public static ToolResult Ok(string text) => new(text, false);
        public static ToolResult Fail(string text) => new(text, true);
    }

    private readonly List<Tool> tools = new();
    private readonly Dictionary<string, Func<JsonElement, ToolResult>> handlers = new(StringComparer.Ordinal);

    /// <summary>The registered tools, in registration order.</summary>
    public IReadOnlyList<Tool> Tools => tools;

    /// <summary>Registers a tool. Tests use this to inject a fake tool (e.g. an echo).</summary>
    public void AddTool(string name, string description, string inputSchemaJson,
        Func<JsonElement, ToolResult> handler)
    {
        tools.Add(new Tool(name, description, inputSchemaJson));
        handlers[name] = handler;
    }

    /// <summary>Dispatches a <c>tools/call</c>. Returns false when the tool name is unknown.</summary>
    public bool TryInvoke(string name, JsonElement arguments, out ToolResult result)
    {
        if (!handlers.TryGetValue(name, out Func<JsonElement, ToolResult>? handler))
        {
            result = ToolResult.Fail($"Unknown tool: {name}");
            return false;
        }
        result = handler(arguments);
        return true;
    }

    /// <summary>The seven production tools, wired to the real stores and run host.</summary>
    public static McpToolRegistry BuildDefault(McpRunHost runHost, string defaultSequencesDir)
    {
        var registry = new McpToolRegistry();

        registry.AddTool(
            "list_profiles",
            "List saved profiles: names, action counts and hotkeys.",
            """{"type":"object","properties":{}}""",
            _ =>
            {
                List<Profile> profiles = ProfileStore.Load();
                if (profiles.Count == 0) return ToolResult.Ok("(no saved profiles)");
                var sb = new StringBuilder();
                foreach (Profile p in profiles)
                {
                    sb.Append(p.Name).Append(" — ").Append(p.Actions.Count).Append(" action(s)");
                    if (!string.IsNullOrEmpty(p.HotkeyName)) sb.Append(" (hotkey ").Append(p.HotkeyName).Append(')');
                    sb.Append('\n');
                }
                return ToolResult.Ok(sb.ToString());
            });

        registry.AddTool(
            "list_sequences",
            "List sequence files (.acseq/.json) in a folder, with their action counts.",
            """{"type":"object","properties":{"folder":{"type":"string","description":"Folder to scan. Defaults to Documents\\AutoClicker."}}}""",
            args =>
            {
                string folder = GetString(args, "folder") ?? defaultSequencesDir;
                if (!Directory.Exists(folder)) return ToolResult.Fail($"Folder not found: {folder}");
                string[] files;
                try
                {
                    files = Directory.GetFiles(folder, "*.acseq")
                        .Concat(Directory.GetFiles(folder, "*.json"))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return ToolResult.Fail($"Could not list {folder}: {ex.Message}");
                }
                if (files.Length == 0) return ToolResult.Ok($"No .acseq/.json sequence files in {folder}");
                var sb = new StringBuilder();
                foreach (string f in files)
                {
                    int count = -1;
                    try { count = SequenceFile.Deserialize(File.ReadAllText(f)).Count; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                  or JsonException or SequenceFile.UnsupportedVersionException)
                    {
                        // Not a readable sequence — report it as such rather than fail the whole listing.
                    }
                    sb.Append(count >= 0
                            ? $"{Path.GetFileName(f)} — {count} action(s)"
                            : $"{Path.GetFileName(f)} — not a readable sequence")
                      .Append('\n');
                }
                return ToolResult.Ok(sb.ToString());
            });

        registry.AddTool(
            "read_sequence",
            "Read a sequence file and return its actions as JSON summary lines (one per action).",
            """{"type":"object","properties":{"path":{"type":"string","description":"Path to the .acseq/.json file."}},"required":["path"]}""",
            args =>
            {
                string path = GetString(args, "path") ?? "";
                if (path.Length == 0) return ToolResult.Fail("The 'path' argument is required.");
                if (!File.Exists(path)) return ToolResult.Fail($"Sequence file not found: {path}");
                List<SeqAction> actions;
                try { actions = SequenceFile.Deserialize(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or JsonException or SequenceFile.UnsupportedVersionException)
                {
                    return ToolResult.Fail($"Could not read {path}: {ex.Message}");
                }
                var sb = new StringBuilder();
                for (int i = 0; i < actions.Count; i++)
                {
                    SeqAction a = actions[i];
                    sb.Append(JsonSerializer.Serialize(new
                    {
                        index = i,
                        kind = a.Kind.ToString(),
                        description = a.Describe(),
                        target = a.DescribeTarget(),
                        x = a.X,
                        y = a.Y,
                        button = a.Button,
                        delayMs = a.DelayMs,
                        keyCombo = a.KeyCombo,
                        text = a.Text,
                    }));
                    sb.Append('\n');
                }
                return ToolResult.Ok(sb.ToString());
            });

        registry.AddTool(
            "create_sequence",
            "Create (or overwrite) a sequence file from an array of action objects.",
            """{"type":"object","properties":{"path":{"type":"string"},"actions":{"type":"array","items":{"type":"object"}}},"required":["path","actions"]}""",
            args =>
            {
                string path = GetString(args, "path") ?? "";
                if (path.Length == 0) return ToolResult.Fail("The 'path' argument is required.");
                if (!args.TryGetProperty("actions", out JsonElement actionsArr) || actionsArr.ValueKind != JsonValueKind.Array)
                    return ToolResult.Fail("The 'actions' argument must be an array of action objects.");

                List<SeqAction>? actions;
                try { actions = actionsArr.Deserialize<List<SeqAction>>(); }
                catch (JsonException ex)
                {
                    return ToolResult.Fail("The 'actions' array is not valid action JSON: " + ex.Message);
                }

                actions ??= new List<SeqAction>();
                string? problem = ValidateActions(actions);
                if (problem is not null) return ToolResult.Fail(problem);

                try
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, SequenceFile.Serialize(actions));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException
                                              or System.Security.SecurityException)
                {
                    return ToolResult.Fail($"Could not write {path}: {ex.Message}");
                }
                return ToolResult.Ok($"Created {path} with {actions.Count} action(s).");
            });

        registry.AddTool(
            "run_sequence",
            "Run a sequence file in the background. Returns immediately with a run_id; poll get_state for progress and stop_run to cancel.",
            """{"type":"object","properties":{"path":{"type":"string"},"repeat":{"type":"integer","description":"Number of passes (default 1)."},"until_stopped":{"type":"boolean","description":"Repeat until stop_run (default false)."},"background":{"type":"boolean","description":"PostMessage background mode (default false)."},"jitter":{"type":"integer","description":"Position jitter in pixels (default 0)."}},"required":["path"]}""",
            args =>
            {
                string path = GetString(args, "path") ?? "";
                if (path.Length == 0) return ToolResult.Fail("The 'path' argument is required.");
                if (!File.Exists(path)) return ToolResult.Fail($"Sequence file not found: {path}");

                List<SeqAction> actions;
                try { actions = SequenceFile.Deserialize(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              or JsonException or SequenceFile.UnsupportedVersionException)
                {
                    return ToolResult.Fail($"Could not read {path}: {ex.Message}");
                }
                foreach (SeqAction a in actions) a.Normalize();
                if (actions.Count == 0) return ToolResult.Fail($"{path} contains no actions.");

                int repeat = GetInt(args, "repeat") ?? 1;
                bool untilStopped = GetBool(args, "until_stopped") ?? false;
                bool background = GetBool(args, "background") ?? false;
                int jitter = GetInt(args, "jitter") ?? 0;

                if (repeat < 1) return ToolResult.Fail("'repeat' must be at least 1.");
                if (jitter is < 0 or > 500) return ToolResult.Fail("'jitter' must be between 0 and 500.");
                if (untilStopped && repeat != 1)
                    return ToolResult.Fail("'repeat' and 'until_stopped' are mutually exclusive.");

                var options = new RunOptions
                {
                    Background = background,
                    Limited = !untilStopped,
                    Limit = repeat,
                    JitterPixels = jitter,
                    JitterPercent = 0,
                    CornerFailSafe = false,
                };

                string? runId = runHost.StartRun(actions, options, Path.GetFileName(path));
                if (runId is null)
                    return ToolResult.Fail("A run is already in progress. Call stop_run first.");

                return ToolResult.Ok(JsonSerializer.Serialize(new { started = true, run_id = runId }));
            });

        registry.AddTool(
            "stop_run",
            "Cancel the run currently in progress.",
            """{"type":"object","properties":{}}""",
            _ => ToolResult.Ok(JsonSerializer.Serialize(new { stopped = runHost.StopRun() })));

        registry.AddTool(
            "get_state",
            "Current run state: idle/running, run_id, current target, elapsed time and steps done.",
            """{"type":"object","properties":{}}""",
            _ => ToolResult.Ok(runHost.GetStateJson()));

        return registry;
    }

    /// <summary>Kinds <c>create_sequence</c> accepts: the input kinds a simple JSON array can express safely.</summary>
    private static bool IsCreatableKind(ActionKind kind) => kind is
        ActionKind.Click or ActionKind.Drag or ActionKind.Scroll
        or ActionKind.Key or ActionKind.Text or ActionKind.Wait or ActionKind.WaitPixel;

    /// <summary>Normalizes each action and returns an error message, or null when all are valid.</summary>
    private static string? ValidateActions(List<SeqAction> actions)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            SeqAction a = actions[i];
            a.Normalize();
            if (!IsCreatableKind(a.Kind))
                return $"Action {i}: kind \"{a.Kind}\" is not allowed through create_sequence " +
                       "(allowed: Click, Drag, Scroll, Key, Text, Wait, WaitPixel).";
            switch (a.Kind)
            {
                case ActionKind.Key when string.IsNullOrWhiteSpace(a.KeyCombo):
                    return $"Action {i}: Key actions need a KeyCombo (e.g. \"Ctrl+C\").";
                case ActionKind.Text when a.Text.Length == 0:
                    return $"Action {i}: Text actions need non-empty Text.";
                case ActionKind.WaitPixel when a.Condition == PixelCondition.None:
                    return $"Action {i}: WaitPixel actions need a Condition (IfMatch or IfNoMatch).";
                default:
                    break;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out JsonElement p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int? GetInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out JsonElement p)
        && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt32(out int v)
            ? v
            : null;

    private static bool? GetBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out JsonElement p)
        && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean()
            : null;
}
