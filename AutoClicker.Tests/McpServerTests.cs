using System.Text.Json;
using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// MCP protocol behavior at the message level — no pipes. Every test drives
/// <see cref="McpJsonRpc.HandleLine"/> directly with a <see cref="McpContext"/>.
/// </summary>
public class McpServerTests
{
    private static McpContext NewContext(McpToolRegistry? registry = null) =>
        new() { Tools = registry ?? McpToolRegistry.BuildDefault(new McpRunHost(), Path.GetTempPath()) };

    private static JsonDocument Parse(string? line)
    {
        Assert.NotNull(line);
        return JsonDocument.Parse(line!);
    }

    private static JsonElement Call(string request, out string? response, McpContext? ctx = null)
    {
        response = McpJsonRpc.HandleLine(request, ctx ?? NewContext());
        return Parse(response).RootElement;
    }

    [Fact]
    public void Initialize_reports_protocol_capabilities_and_server_info()
    {
        var result = Call("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", out _)
            .GetProperty("result");

        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
        Assert.Equal("autoclicker", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("serverInfo").GetProperty("version").GetString()));
    }

    [Fact]
    public void Initialize_echoes_a_supported_client_protocol_version()
    {
        var result = Call(
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""",
            out _).GetProperty("result");

        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void Initialized_notification_is_ignored()
    {
        var ctx = NewContext();
        string? response = McpJsonRpc.HandleLine(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""", ctx);

        Assert.Null(response);
    }

    [Fact]
    public void Tools_list_reports_all_seven_tools_with_schemas()
    {
        var result = Call("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", out _)
            .GetProperty("result");

        var tools = result.GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.Equal(7, tools.GetArrayLength());

        var names = new HashSet<string>();
        foreach (JsonElement t in tools.EnumerateArray())
        {
            names.Add(t.GetProperty("name").GetString()!);
            Assert.False(string.IsNullOrEmpty(t.GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Object, t.GetProperty("inputSchema").ValueKind);
        }

        Assert.Contains("list_profiles", names);
        Assert.Contains("list_sequences", names);
        Assert.Contains("read_sequence", names);
        Assert.Contains("create_sequence", names);
        Assert.Contains("run_sequence", names);
        Assert.Contains("stop_run", names);
        Assert.Contains("get_state", names);
    }

    [Fact]
    public void Tools_call_routes_to_a_registered_tool()
    {
        var registry = new McpToolRegistry();
        registry.AddTool("echo", "Echoes its arguments.", """{"type":"object","properties":{}}""",
            args => McpToolRegistry.ToolResult.Ok(args.GetRawText()));

        var result = Call(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","arguments":{"value":"hi"}}}""",
            out _, NewContext(registry)).GetProperty("result");

        var content = result.GetProperty("content");
        Assert.Single(content.EnumerateArray());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("""{"value":"hi"}""", content[0].GetProperty("text").GetString());
        Assert.False(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void Tool_failure_is_reported_as_isError_content()
    {
        // read_sequence with no path is a genuine tool error, not a protocol error.
        var result = Call(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_sequence","arguments":{}}}""",
            out _).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.False(string.IsNullOrEmpty(result.GetProperty("content")[0].GetProperty("text").GetString()));
    }

    [Fact]
    public void Unknown_tool_is_reported_as_isError_content()
    {
        var result = Call(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"does_not_exist","arguments":{}}}""",
            out _).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void Ping_returns_an_empty_result()
    {
        var result = Call("""{"jsonrpc":"2.0","id":7,"method":"ping"}""", out _).GetProperty("result");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    [Fact]
    public void Unknown_method_returns_method_not_found()
    {
        var root = Call("""{"jsonrpc":"2.0","id":8,"method":"nope"}""", out _);

        Assert.Equal(-32601, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Parse_error_returns_minus_32700_with_null_id()
    {
        var root = Call("not json", out _);

        Assert.Equal(-32700, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public void Batch_array_is_rejected_with_minus_32600()
    {
        var root = Call("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", out _);

        Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Notification_requests_produce_no_response()
    {
        var ctx = NewContext();
        Assert.Null(McpJsonRpc.HandleLine("""{"jsonrpc":"2.0","method":"ping"}""", ctx));
    }

    [Fact]
    public void Stdio_session_runs_initialize_and_tools_list_over_a_string_transport()
    {
        // RunSession is the transport-agnostic loop behind both the named-pipe server and
        // --stdio. Driving it with StringReader/StringWriter exercises the exact stdio path
        // without spawning a process.
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""" + "\n" +
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\n");
        var output = new StringWriter();

        McpServer.RunSession(input, output, new McpRunHost());

        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        using (var doc = JsonDocument.Parse(lines[0]))
            Assert.Equal("2025-06-18",
                doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        using (var doc = JsonDocument.Parse(lines[1]))
            Assert.Equal(7,
                doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }
}
