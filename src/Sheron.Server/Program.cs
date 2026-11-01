using System.Net;
using System.Runtime.InteropServices;
using Sheron.Core;
using Sheron.Core.Net;
using Sheron.Core.Rendezvous;
using Sheron.Server;

// Sheron's server (TRD › Server): IDs and finding PCs on TCP (Plan step 11), address check and relay on UDP with
// the same port number (Plan step 12).
//   Sheron.Server [--port 443] [--data <folder>]
// The data folder keeps the TLS certificate and the ID registry. Logs go to standard output (journald under systemd).
int port = 443;
string data = Path.Combine(AppContext.BaseDirectory, "data");
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out int chosen) && chosen is > 0 and < 65536) port = chosen;
    if (args[i] == "--data") data = args[i + 1];
}
Directory.CreateDirectory(data);

void Write(string line) => Console.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {line}");

using var certificate = ServerCertificate.LoadOrCreate(data);
var registry = new IdRegistry(Path.Combine(data, "registry.txt"));
using var udp = new UdpTransport(port);
await using var server = new RendezvousServer(new IPEndPoint(IPAddress.Any, port), certificate, registry, udp: udp);
server.Log += Write;
server.Start();
Write($"{AppInfo.Name} server {AppInfo.Version} on TCP and UDP {port}; {registry.Count} devices known");
// The app is built with this pin: SHA-256 of the certificate's public key.
Write($"Server pin: {Convert.ToBase64String(RendezvousWire.Pin(certificate))}");

// Last-seen times go to disk every 5 minutes; new devices are saved at once.
using var saving = new Timer(_ => registry.Save(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

var stop = new TaskCompletionSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.TrySetResult();
});
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
Write("Stopping");
