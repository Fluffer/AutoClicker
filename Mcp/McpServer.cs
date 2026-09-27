using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AutoClicker;

/// <summary>
/// The in-process MCP server, exposed only in CLI mode (<c>AutoClicker.exe --mcp</c>). It lets
/// an AI assistant drive AutoClicker through the standard MCP tools over a local named pipe.
/// </summary>
/// <remarks>
/// <para>Why a named pipe and not stdio: the CLI already borrows the launching terminal's
/// console (see <see cref="CliRunner.Run"/>), so stdout is not a clean channel to hand an MCP
/// client. A pipe is also the natural way for a GUI-style WinExe to serve a long-lived
/// connection that a client can open on demand.</para>
/// <para>Why the GUI never starts this server: it exists to give an external assistant control
/// over real mouse/keyboard input. The GUI runs by double-click and has no business exposing
/// that surface; a server should only exist when the user explicitly launches
/// <c>--mcp</c> from a terminal. This decision is enforced structurally — <see cref="Program.Main"/>
/// only calls this class when the first argument is <c>--mcp</c>.</para>
/// <para>Security: the pipe's DACL grants FullControl to the current Windows user only (not
/// Everyone, not Administrators-as-a-class), so no other account can connect. The pipe name
/// also embeds the sanitized username, making it per-user and predictable for a client that
/// wants to discover it.</para>
/// </remarks>
internal static class McpServer
{
    private const int ExitSuccess = 0;
    private const int ExitRuntimeFailure = 1;
    private const int ExitUsage = 2;

    private static readonly bool TraceEnabled =
        Environment.GetEnvironmentVariable("AUTOCLICKER_MCP_TRACE") == "1";

    /// <summary>
    /// Parses the MCP-only arguments (<c>--pipe-name &lt;name&gt;</c>, <c>--stdio</c>) and runs
    /// the server until the process is killed or stdin closes. <paramref name="args"/> is
    /// everything after <c>--mcp</c>.
    /// </summary>
    public static int Run(string[] args)
    {
        string? pipeName = null;
        bool stdio = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe-name":
                    if (i + 1 >= args.Length) return Usage("--pipe-name requires a value.");
                    pipeName = args[++i];
                    break;
                case "--stdio":
                    stdio = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return ExitSuccess;
                default:
                    return Usage($"Unknown MCP option '{args[i]}'.");
            }
        }

        if (stdio)
        {
            if (pipeName is not null) return Usage("--stdio and --pipe-name are mutually exclusive.");
            return RunStdio();
        }

        return RunServer(pipeName ?? DefaultPipeName());
    }

    /// <summary>One MCP server per user, so a client can discover the pipe deterministically.</summary>
    internal static string DefaultPipeName()
    {
        var sb = new StringBuilder();
        foreach (char c in Environment.UserName)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return $"AutoClicker.mcp.{sb}";
    }

    /// <summary>Builds a pipe DACL granting FullControl to the current user and nobody else.</summary>
    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        // GetCurrent() always carries a user SID; the nullability annotation just doesn't
        // know that, so assert it once here rather than thread a nullable through the rules.
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("Could not determine the current Windows user.");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Creates the server pipe with the current-user-only DACL. Exposed so the integration
    /// tests exercise the exact same protected-pipe creation path as the real server.
    /// </summary>
    internal static NamedPipeServerStream CreatePipe(string pipeName, int maxInstances = 1) =>
        // Asynchronous + current-user-only DACL. maxNumberOfServerInstances = 1 keeps a
        // second server process from silently shadowing the first on the same name.
        NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, maxInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, BuildPipeSecurity());

    private static int RunServer(string pipeName)
    {
        NamedPipeServerStream server;
        try
        {
            server = CreatePipe(pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not create MCP pipe '{pipeName}': {ex.Message}");
            return ExitRuntimeFailure;
        }

        using (server)
        {
            // The run host outlives individual connections: a run keeps going (and get_state
            // still reports it) after a client disconnects and reconnects.
            var runHost = new McpRunHost();

            Trace($"MCP server listening on pipe '{pipeName}'.");

            while (true)
            {
                try
                {
                    server.WaitForConnection();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    break; // pipe torn down; process is exiting
                }

                Trace("client connected");

                using var reader = new StreamReader(server, new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false),
                    bufferSize: 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

                RunSession(reader, writer, runHost);

                server.Disconnect();
                Trace("client disconnected");
            }
        }

        return ExitSuccess;
    }

    /// <summary>
    /// stdio transport: speak MCP JSON-RPC on the process's own stdin/stdout and run until
    /// stdin closes. This is the transport mainstream MCP clients (Claude Desktop, VS Code
    /// Copilot) expect — they spawn <c>AutoClicker.exe --mcp --stdio</c> with redirected
    /// standard handles, so no console is needed and no pipe name has to be discovered.
    /// </summary>
    private static int RunStdio()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        RunSession(stdin, stdout, new McpRunHost());
        return ExitSuccess;
    }

    /// <summary>
    /// The request/response loop, transport-agnostic: read a newline-delimited JSON request
    /// from <paramref name="input"/>, dispatch it through <see cref="McpJsonRpc.HandleLine"/>,
    /// write the response line to <paramref name="output"/>. Runs until the input ends (or
    /// the reader throws, e.g. a client disconnecting mid-line). Shared by the pipe loop and
    /// the stdio loop; exposed so tests can drive a session over <see cref="StringReader"/>.
    /// </summary>
    internal static void RunSession(TextReader input, TextWriter output, McpRunHost runHost)
    {
        var registry = McpToolRegistry.BuildDefault(runHost, UserDataPaths.RootDir);
        var context = new McpContext { Tools = registry };

        while (true)
        {
            string? line;
            try
            {
                line = input.ReadLine();
            }
            catch (IOException)
            {
                line = null; // client vanished mid-line
            }
            if (line is null) break;

            Trace("< " + line);
            string? response = McpJsonRpc.HandleLine(line, context);
            if (response is not null)
            {
                Trace("> " + response);
                output.WriteLine(response);
            }
        }
    }

    private static void Trace(string message)
    {
        // Protocol trace goes to stderr (and only when opted in) — never stdout, which on a
        // console-attached process the user may be watching, and never the pipe, which is the
        // protocol channel itself.
        if (TraceEnabled) Console.Error.WriteLine("[mcp] " + message);
    }

    private static int Usage(string message)
    {
        if (message.Length > 0) Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'AutoClicker.exe --mcp --help' for MCP usage.");
        return ExitUsage;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Auto Clicker - MCP server mode (lets an AI assistant drive AutoClicker)

            Usage:
              AutoClicker.exe --mcp [--pipe-name <name> | --stdio]

            Options:
              --pipe-name <name>   Listen on a specific named pipe
                                   (default: AutoClicker.mcp.<username>)
              --stdio              Speak JSON-RPC on stdin/stdout instead of a pipe
                                   (the transport Claude Desktop / VS Code Copilot expect)

            The server speaks MCP (JSON-RPC 2.0, newline-delimited) and runs until the
            process is killed (pipe) or stdin closes (stdio). Protocol trace lines go to
            stderr when the AUTOCLICKER_MCP_TRACE environment variable is set to 1.
            """);
    }
}
