using System.Net;
using Sheron.Core.Session;

namespace Sheron.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Developer windows instead of the app: --capture-test for Plan steps 3–4, --loop-test for step 5,
        // --host [--port N] and --view <IP>[:port] for step 8. --software-encoder and --software-decoder skip the
        // hardware routes.
        bool hardwareEncoder = !args.Contains("--software-encoder");
        bool hardwareDecoder = !args.Contains("--software-decoder");
        Form? first = args.Contains("--capture-test") ? new CaptureTestForm(hardwareEncoder)
            : args.Contains("--loop-test") ? new LoopTestForm(hardwareEncoder, hardwareDecoder)
            : args.Contains("--host") ? HostForm(args, hardwareEncoder)
            : args.Contains("--view") ? ViewerForm(args, hardwareDecoder)
            : new MainForm();
        if (first is not null) Application.Run(first);
    }

    private static Form? HostForm(string[] args, bool hardwareEncoder)
    {
        int port = Link.DefaultPort;
        string? text = ValueAfter(args, "--port");
        if (text is not null && (!int.TryParse(text, out port) || port is < 1 or > 65535))
            return Refuse($"\"{text}\" is not a port number (1–65535).");
        return new HostTestForm(port, hardwareEncoder);
    }

    private static Form? ViewerForm(string[] args, bool hardwareDecoder)
    {
        string? text = ValueAfter(args, "--view");
        if (text is null || !IPEndPoint.TryParse(text, out var remote))
            return Refuse("Give the remote PC's address after --view, such as --view 192.168.1.20 or --view 192.168.1.20:47920.");
        if (remote.Port == 0) remote.Port = Link.DefaultPort;
        return new ViewerTestForm(remote, hardwareDecoder);
    }

    private static string? ValueAfter(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static Form? Refuse(string message)
    {
        MessageBox.Show(message, "Sheron", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return null;
    }
}
