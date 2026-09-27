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
            Application.Run(new Form1());
            return 0;
        }

        // Headless CLI runs deliberately skip the mutex: several scheduled sequences can
        // run concurrently (they only share read-mostly files, saved PID-suffixed).
        return CliRunner.Run(args);
    }
}