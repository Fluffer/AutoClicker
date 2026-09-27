using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// End-to-end named-pipe tests: a real server pipe (created through
/// <see cref="McpServer.CreatePipe"/>, so the current-user-only DACL is exercised) and a real
/// client pipe, running the initialize handshake and a tools/list round-trip. Local pipes are
/// reliable on Windows; unique names and generous timeouts keep these from flaking.
/// </summary>
public class McpPipeIntegrationTests
{
    /// <summary>
    /// A connected server/client pipe pair with newline-delimited JSON readers/writers on both
    /// ends, matching the framing the real <see cref="McpServer"/> loop uses.
    /// </summary>
    private sealed class PipePair : IDisposable
    {
        private readonly CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        private readonly StreamReader serverReader;
        private readonly StreamWriter serverWriter;
        private readonly StreamReader clientReader;
        private readonly StreamWriter clientWriter;

        public PipePair()
        {
            string name = "AutoClicker.Tests." + Guid.NewGuid().ToString("N");
            NamedPipeServerStream server = McpServer.CreatePipe(name);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

            var serverWait = Task.Run(() => server.WaitForConnection(), cts.Token);
            client.Connect(TimeSpan.FromSeconds(15));
            serverWait.GetAwaiter().GetResult();

            serverReader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            serverWriter = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
                { AutoFlush = true, NewLine = "\n" };
            clientReader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            clientWriter = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
                { AutoFlush = true, NewLine = "\n" };
        }

        /// <summary>Client sends a request; server processes it through <paramref name="ctx"/> and replies.</summary>
        public async Task<string?> RoundTripAsync(string request, McpContext ctx)
        {
            await clientWriter.WriteLineAsync(request).WaitAsync(cts.Token);
            string? requestLine = await serverReader.ReadLineAsync(cts.Token);
            string? response = McpJsonRpc.HandleLine(requestLine!, ctx);
            if (response is not null)
            {
                await serverWriter.WriteLineAsync(response).WaitAsync(cts.Token);
                return await clientReader.ReadLineAsync(cts.Token);
            }
            return null;
        }

        public void Dispose()
        {
            cts.Cancel();
            serverWriter.Dispose();
            serverReader.Dispose();
            clientWriter.Dispose();
            clientReader.Dispose();
            cts.Dispose();
        }
    }

    [Fact]
    public async Task Initialize_handshake_round_trips_over_a_named_pipe()
    {
        using var pair = new PipePair();
        var ctx = new McpContext { Tools = McpToolRegistry.BuildDefault(new McpRunHost(), Path.GetTempPath()) };

        string? line = await pair.RoundTripAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            ctx);

        using var doc = JsonDocument.Parse(line!);
        JsonElement result = doc.RootElement.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("autoclicker", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Tools_list_round_trips_over_a_named_pipe()
    {
        using var pair = new PipePair();
        var ctx = new McpContext { Tools = McpToolRegistry.BuildDefault(new McpRunHost(), Path.GetTempPath()) };

        string? line = await pair.RoundTripAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", ctx);

        using var doc = JsonDocument.Parse(line!);
        Assert.Equal(7, doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());
    }
}
