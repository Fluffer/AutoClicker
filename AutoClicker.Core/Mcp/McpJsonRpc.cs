using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AutoClicker;

/// <summary>
/// Per-connection state shared across <see cref="McpJsonRpc.HandleLine"/> calls. Each pipe
/// connection performs its own <c>initialize</c> handshake, so this is fresh per connection.
/// </summary>
internal sealed class McpContext
{
    /// <summary>The MCP protocol version negotiated at <c>initialize</c>.</summary>
    public string ProtocolVersion { get; set; } = McpJsonRpc.DefaultProtocolVersion;

    /// <summary>The tool registry this connection dispatches <c>tools/call</c> against.</summary>
    public required McpToolRegistry Tools { get; init; }
}

/// <summary>
/// The MCP-over-JSON-RPC 2.0 message handler, as a pure function: a request line goes in, a
/// response line (or null for notifications) comes out. No pipe, no IO — the
/// <see cref="McpServer"/> owns the transport, and this class owns the protocol, which is what
/// makes the message-level behavior unit-testable without any real pipes.
/// </summary>
/// <remarks>
/// Framing is one UTF-8 JSON object per line (newline-delimited), exactly the framing MCP uses
/// over stdio. Batch arrays are not part of MCP and are rejected with -32600.
/// </remarks>
internal static class McpJsonRpc
{
    internal const string DefaultProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedProtocolVersions = { "2025-06-18", "2024-11-05" };

    private const int ErrorParse = -32700;
    private const int ErrorInvalidRequest = -32600;
    private const int ErrorMethodNotFound = -32601;
    private const int ErrorInvalidParams = -32602;

    /// <summary>
    /// Handles one request line. Returns the JSON response line to write, or null when the
    /// message was a notification (no <c>id</c>) and produced no response.
    /// </summary>
    public static string? HandleLine(string requestLine, McpContext ctx)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(requestLine);
        }
        catch (JsonException)
        {
            return Error(null, ErrorParse, "Parse error");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return Error(null, ErrorInvalidRequest, "Batch arrays are not supported.");

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("method", out JsonElement methodProp)
                || methodProp.ValueKind != JsonValueKind.String)
            {
                return Error(null, ErrorInvalidRequest, "Invalid Request");
            }

            string method = methodProp.GetString()!;
            bool isNotification = !root.TryGetProperty("id", out JsonElement id);
            JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;

            switch (method)
            {
                case "initialize":
                    return HandleInitialize(id, isNotification, parameters, ctx);

                case "notifications/initialized":
                    return null; // acknowledgment; never answered

                case "ping":
                    return isNotification ? null : Result(id, static _ => { });

                case "tools/list":
                    return isNotification ? null : HandleToolsList(id, ctx);

                case "tools/call":
                    return isNotification ? null : HandleToolsCall(id, parameters, ctx);

                default:
                    return isNotification
                        ? null
                        : Error(id, ErrorMethodNotFound, $"Method not found: {method}");
            }
        }
    }

    private static string? HandleInitialize(JsonElement id, bool isNotification, JsonElement parameters, McpContext ctx)
    {
        string? requested = parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("protocolVersion", out JsonElement pv)
            && pv.ValueKind == JsonValueKind.String
                ? pv.GetString()
                : null;

        // Echo the client's version when it is one we speak; otherwise pick our own.
        ctx.ProtocolVersion = requested is not null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : DefaultProtocolVersion;

        if (isNotification) return null;
        return Result(id, w =>
        {
            w.WriteString("protocolVersion", ctx.ProtocolVersion);
            w.WriteStartObject("capabilities");
            w.WriteStartObject("tools");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("serverInfo");
            w.WriteString("name", "autoclicker");
            w.WriteString("version", VersionString);
            w.WriteEndObject();
        });
    }

    private static string HandleToolsList(JsonElement id, McpContext ctx) => Result(id, w =>
    {
        w.WriteStartArray("tools");
        foreach (McpToolRegistry.Tool tool in ctx.Tools.Tools)
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("description", tool.Description);
            w.WritePropertyName("inputSchema");
            using (JsonDocument schema = JsonDocument.Parse(tool.InputSchemaJson))
                schema.RootElement.WriteTo(w);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private static string HandleToolsCall(JsonElement id, JsonElement parameters, McpContext ctx)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out JsonElement nameProp)
            || nameProp.ValueKind != JsonValueKind.String)
        {
            return Error(id, ErrorInvalidParams, "Invalid params: tools/call requires a 'name' string.");
        }

        string toolName = nameProp.GetString()!;
        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement argsProp)
            && argsProp.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? argsProp
                : JsonSerializer.SerializeToElement(new { });

        McpToolRegistry.ToolResult result;
        if (!ctx.Tools.TryInvoke(toolName, arguments, out result))
            result = McpToolRegistry.ToolResult.Fail($"Unknown tool: {toolName}");

        return Result(id, w =>
        {
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", result.Text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("isError", result.IsError);
        });
    }

    /// <summary>Builds a JSON-RPC 2.0 success response with an object result.</summary>
    private static string Result(JsonElement id, Action<Utf8JsonWriter> writeResult)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WritePropertyName("result");
            w.WriteStartObject();
            writeResult(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Builds a JSON-RPC 2.0 error response (id may be null for parse errors).</summary>
    private static string Error(JsonElement? id, int code, string message)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is { } idValue) idValue.WriteTo(w);
            else w.WriteNullValue();
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string VersionString
    {
        get
        {
            Assembly asm = typeof(McpJsonRpc).Assembly;
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "unknown";
        }
    }
}
