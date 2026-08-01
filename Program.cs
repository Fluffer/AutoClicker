namespace AutoClicker;

static class Program
{
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
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
            return 0;
        }

        return CliRunner.Run(args);
    }
}