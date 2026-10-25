namespace Sheron.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // --capture-test opens the developer window for Plan step 3 instead of the app.
        Form first = args.Contains("--capture-test") ? new CaptureTestForm() : new MainForm();
        Application.Run(first);
    }
}
