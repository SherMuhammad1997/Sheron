namespace Sheron.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Developer windows instead of the app: --capture-test for Plan steps 3–4, --loop-test for step 5.
        // --software-encoder and --software-decoder skip the hardware routes.
        bool hardwareEncoder = !args.Contains("--software-encoder");
        Form first = args.Contains("--capture-test") ? new CaptureTestForm(hardwareEncoder)
            : args.Contains("--loop-test") ? new LoopTestForm(hardwareEncoder, !args.Contains("--software-decoder"))
            : new MainForm();
        Application.Run(first);
    }
}
