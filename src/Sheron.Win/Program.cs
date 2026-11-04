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
        // --host [--port N] for step 8, and --view <ID or IP>, which fills in the start window (the password is typed
        // there, never given on the command line), and --server <IP>[:port] --pin <pin> for step 13 (register with the
        // server). --software-encoder and --software-decoder skip the hardware routes.
        bool hardwareEncoder = !args.Contains("--software-encoder");
        bool hardwareDecoder = !args.Contains("--software-decoder");
        Form? first = args.Contains("--capture-test") ? new CaptureTestForm(hardwareEncoder)
            : args.Contains("--loop-test") ? new LoopTestForm(hardwareEncoder, hardwareDecoder)
            : args.Contains("--host") ? HostForm(args, hardwareEncoder)
            : args.Contains("--view") ? ViewerForm(args, hardwareDecoder)
            : args.Contains("--server") ? ServerForm(args)
            : new MainForm(hardwareDecoder: hardwareDecoder);
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
        if (text is null || (!Core.Rendezvous.RendezvousWire.TryParseId(text, out _) && !IPEndPoint.TryParse(text, out _)))
            return Refuse("Give the remote PC's ID or address after --view, such as --view 4821 or --view 192.168.1.20.");
        return new MainForm(text, hardwareDecoder);
    }

    private static Form? ServerForm(string[] args)
    {
        string? address = ValueAfter(args, "--server");
        string? pinText = ValueAfter(args, "--pin");
        if (address is null || pinText is null)
            return Refuse("Give the server and its pin: --server 203.0.113.10 --pin <the pin the server's log shows>.");
        string host = address;
        int port = 443;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon)
        {
            if (!int.TryParse(address[(colon + 1)..], out port) || port is < 1 or > 65535)
                return Refuse($"\"{address[(colon + 1)..]}\" is not a port number (1–65535).");
            host = address[..colon];
        }
        byte[] pin;
        try
        {
            pin = Convert.FromBase64String(pinText);
        }
        catch (FormatException)
        {
            pin = [];
        }
        if (pin.Length != 32) return Refuse("That pin is not right: copy the whole \"Server pin:\" value from the server's log.");
        return new ServerTestForm(host, port, pin);
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
