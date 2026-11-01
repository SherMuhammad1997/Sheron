using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Sheron.Core.Net;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;
using Sheron.Win.Security;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan step 13: connects this PC to the Sheron server, checks the server's pin, registers, and
/// runs the UDP address check; shows this PC's ID and how the server sees it (TRD › Server › Deployment).
/// Open with <c>Sheron.Win.exe --server &lt;IP&gt;[:port] --pin &lt;pin&gt;</c>.
/// </summary>
internal sealed class ServerTestForm : DevForm
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _pin;
    private readonly CancellationTokenSource _closing = new();
    private DeviceKey? _key;
    private UdpTransport? _udp;
    private RendezvousClient? _client;
    private volatile string _status = "Starting";
    private volatile bool _bad;
    private volatile string _udpResult = "–";
    private long _registeredAt;

    public ServerTestForm(string host, int port, byte[] pin)
        : base($"server {host}:{port} (developer)", $"Sheron server {host}:{port} · Plan step 13 · this PC registers and gets its ID",
            new Size(960, 230), headerHeight: 230, rowsPerColumn: 4, withVideo: false)
    {
        _host = host;
        _port = port;
        _pin = pin;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartRefresh();
        _ = Connect();
    }

    private async Task Connect()
    {
        try
        {
            _key = DeviceKey.Import(SecretStore.ThisPc().DevicePrivateKey);
            _udp = new UdpTransport();
            var lan = LocalNetwork.IPv4Addresses()
                .Select(ip => new PeerAddress(AddressKind.Lan, new IPEndPoint(ip, _udp.LocalEndPoint.Port))).ToList();

            _status = $"Connecting to {_host}:{_port} …";
            _client = await RendezvousClient.ConnectAsync(_host, _port, _pin, _key, lan, _closing.Token);
            _client.Disconnected += () =>
            {
                _status = "The server closed the link";
                _bad = true;
            };
            Interlocked.Exchange(ref _registeredAt, Stopwatch.GetTimestamp());
            _status = "Registered";

            // The address check goes to the server's UDP on the same port (TRD › Direct link, relay and TCP fallback).
            var serverUdp = new IPEndPoint(_client.ServerEndPoint.Address.IsIPv4MappedToIPv6
                ? _client.ServerEndPoint.Address.MapToIPv4() : _client.ServerEndPoint.Address, _port);
            _udpResult = "checking …";
            var connector = new Connector(_client, _udp, serverUdp);
            _udpResult = await connector.CheckAddressAsync(_closing.Token)
                ? $"works · the server sees {connector.PublicUdp}"
                : "no answer · UDP blocked here or at the server; sessions would use TCP 443";
        }
        catch (AuthenticationException)
        {
            Fail("The server's pin does not match: this is not our server, or the pin was copied wrongly");
        }
        catch (Exception error) when (error is SocketException or IOException or InvalidDataException)
        {
            Fail($"Cannot reach the server: {error.Message}");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or CryptographicException)
        {
            Fail($"Cannot read this PC's Sheron secrets: {error.Message}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Fail(string message)
    {
        _status = message;
        _bad = true;
    }

    protected override void ReadFigures()
    {
    }

    protected override IEnumerable<(string Label, string Value, Color? Color)> Rows()
    {
        bool registered = _client is { IsConnected: true } && !_bad;
        long since = Interlocked.Read(ref _registeredAt);

        // Left column: the server and this PC.
        yield return ("Status", _status, _bad ? Bad : registered ? Good : null);
        yield return ("Server", $"{_host}:{_port} · pin {Convert.ToBase64String(_pin)[..12]}…", null);
        yield return ("This PC's ID", _client is { Id: not 0 } c ? RendezvousWire.Show(c.Id) : "–", registered ? Accent : null);
        yield return ("This PC's key", _key is null ? "–" : KeyFingerprint.Show(_key.Fingerprint), null);

        // Right column: how it is reached.
        yield return ("UDP", _udpResult, null);
        yield return ("Online for", registered && since != 0 ? $"{Stopwatch.GetElapsedTime(since).TotalSeconds:0} s · ping every 25 s" : "–", null);
        yield return ("Sheron CPU", $"{CpuPercent:0.0} % of this PC (last 3 s)", null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Cancel();
            if (_client is { } client) client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _udp?.Dispose();
            _key?.Dispose();
            _closing.Dispose();
        }
        base.Dispose(disposing);
    }
}
