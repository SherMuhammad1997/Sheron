namespace Sheron.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // --capture-test opens the developer window for Plan steps 3–4 instead of the app;
        // add --software-encoder to skip the hardware encoder.
        Form first = args.Contains("--capture-test")
            ? new CaptureTestForm(allowHardwareEncoder: !args.Contains("--software-encoder"))
            : new MainForm();
        Application.Run(first);
    }
}
