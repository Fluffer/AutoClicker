namespace AutoClicker;

static class Program
{
    // Named mutex: two GUI instances both register the start/stop hotkey, and the loser
    // shows a misleading "already claimed by another app" message; they would also race
    // the shared settings/profile files. Local\ scope keeps it per-session (fast-user
    // switching gets one instance each, which is correct — hotkeys are session-wide).
    private const string SingleInstanceMutexName = @"Local\AutoClicker.SingleInstance";

    /// <summary>
    ///  The main entry point for the application. With no arguments this launches the GUI,
    ///  unchanged from before CLI mode existed. Any arguments instead hand off to
    ///  <see cref="CliRunner"/>, which returns the process exit code.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        // Move legacy %AppData% data to Documents before any store is read (GUI or CLI),
        // so MSIX uninstalls stop deleting the user's profiles and settings.
        UserDataPaths.EnsureMigrated();

        // MCP server mode: an explicit, opt-in CLI surface for AI assistants to drive
        // AutoClicker. It is deliberately NOT available from the GUI (no arguments) — a
        // double-clicked app has no reason to expose control of real mouse/keyboard input to
        // any process that can reach a local pipe, so the server only exists when the user
        // asks for it from a terminal. Dispatched before normal CLI parsing.
        if (args.Length > 0 && args[0] == "--mcp")
        {
            return McpServer.Run(args[1..]);
        }

        if (args.Length == 0)
        {
            using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                // Not an error: the user double-clicked an already-running app. Exit 0 so
                // shells/scripts don't treat a second launch as a failure.
                MessageBox.Show("Auto Clicker is already running.", "Auto Clicker",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // Dark mode (SystemColorMode): applied before any window is created, after
            // Initialize(). 0 = light (classic), 1 = dark, 2 = follow the system. A settings
            // file saved before this feature existed has no ColorMode, so it defaults to 0
            // (classic) and keeps the original look. Load() never throws, so this is safe.
            AppSettings startupSettings = AppSettings.Load();
            Application.SetColorMode(startupSettings.ColorMode switch
            {
                1 => SystemColorMode.Dark,
                2 => SystemColorMode.System,
                _ => SystemColorMode.Classic,
            });

            Application.Run(new Form1());
            return 0;
        }

        // Headless CLI runs deliberately skip the mutex: several scheduled sequences can
        // run concurrently (they only share read-mostly files, saved PID-suffixed).
        return CliRunner.Run(args);
    }
}