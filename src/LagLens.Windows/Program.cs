namespace LagLens.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test") || args.Contains("--ui-smoke") || args.Contains("--live-test") || args.Contains("--licenses"))
            return SmokeTests.Run(args);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFailure(e.Exception);
        try { Application.Run(new MainForm()); return 0; }
        catch (Exception ex) { ShowFailure(ex); return 1; }
    }

    private static void ShowFailure(Exception ex)
    {
        MessageBox.Show("LagLens could not complete the operation. Your existing session files remain in your Sessions folder.\n\n" + ex.Message,
            "LagLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
