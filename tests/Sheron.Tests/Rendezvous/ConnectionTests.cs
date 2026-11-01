using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Sheron.Core.Net;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;
using Sheron.Core.Session;
using Sheron.Server;
using Sheron.Sim;

namespace Sheron.Tests.Rendezvous;

/// <summary>
/// Plan step 12 "Done when": through the network simulator, two PCs connect directly; with the direct path blocked
/// they use the UDP relay; with UDP blocked they use the TCP 443 relay; and a session runs on each path (TRD ›
/// Server › Direct link, relay and TCP fallback). The server is the real one: TLS on this machine, UDP on the simulator.
/// </summary>
[Collection(Timing.Name)]
public class ConnectionTests : IAsyncLifetime
{
    private static readonly IPEndPoint ServerUdp = At("10.10.10.1", 443);
    private static readonly LinkTimings Quick = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(3));

    private readonly string _folder = Directory.CreateTempSubdirectory("sheron-connect-").FullName;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = ServerCertificate.Create();
    private readonly SimNetwork _net = new();
    private readonly SimSite _internet;
    private readonly SimTransport _serverUdp;
    private readonly RendezvousServer _server;
    private readonly List<IAsyncDisposable> _clients = [];

    public ConnectionTests()
    {
        _internet = _net.AddSite("internet", null);
        _serverUdp = _net.Bind(_internet, ServerUdp);
        _server = new RendezvousServer(new IPEndPoint(IPAddress.Loopback, 0), _certificate,
            new IdRegistry(Path.Combine(_folder, "registry.txt")), udp: _serverUdp);
    }

    public Task InitializeAsync()
    {
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        await _server.DisposeAsync();
        _serverUdp.Dispose();
    }

    private static IPEndPoint At(string address, int port) => new(IPAddress.Parse(address), port);

    /// <summary>One PC: its UDP socket at a site, its TLS link to the server, and its connector after the address check.</summary>
    private async Task<(Connector Connector, RendezvousClient Client)> Pc(SimSite site, string lan, DeviceKey key)
    {
        var udp = _net.Bind(site, At(lan, 47920));
        var client = await RendezvousClient.ConnectAsync("127.0.0.1", _server.LocalEndPoint.Port, RendezvousWire.Pin(_certificate), key,
            [new PeerAddress(AddressKind.Lan, udp.LocalEndPoint)]);
        _clients.Add(client);
        var connector = new Connector(client, udp, ServerUdp);
        await connector.CheckAddressAsync();
        return (connector, client);
    }

    /// <summary>
    /// The viewer finds the remote PC by ID and both work out the path at once, as the app does on Found and Incoming.
    /// Each starts its session as soon as it has its path (the remote PC knows its path when the viewer's Hello
    /// comes), then a session runs on the two paths.
    /// </summary>
    private static async Task<(ConnectedPath Viewer, ConnectedPath Host)> ConnectAndRunSession(
        (Connector Connector, RendezvousClient Client) viewer, DeviceKey viewerKey,
        (Connector Connector, RendezvousClient Client) host, DeviceKey hostKey)
    {
        var accepting = new TaskCompletionSource<Task<ConnectedPath>>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Client.Incoming += caller => accepting.TrySetResult(host.Connector.AcceptAsync(caller));

        var found = await viewer.Client.FindAsync(host.Client.Id);
        var viewerPath = await viewer.Connector.ConnectAsync(Assert.IsType<PeerInfo>(found.Peer));
        ConnectedPath? hostPath = null;
        try
        {
            using var viewerLink = Link.Viewer(viewerKey, viewerPath.Transport, viewerPath.Remote, Quick);
            hostPath = await await accepting.Task;
            using var hostLink = Link.Host(hostKey, hostPath.Transport, Quick);
            RunSession(viewerLink, viewerKey, hostLink, hostKey);
        }
        finally
        {
            Close(viewerPath);
            if (hostPath is not null) Close(hostPath);
        }
        return (viewerPath, hostPath);
    }

    /// <summary>The handshake, a keyframe and more frames, and control messages both ways.</summary>
    private static void RunSession(Link viewer, DeviceKey viewerKey, Link host, DeviceKey hostKey)
    {
        var frames = new ConcurrentQueue<AssembledFrame>();
        var toViewer = new ConcurrentQueue<byte[]>();
        var toHost = new ConcurrentQueue<byte[]>();
        viewer.FrameReceived += frames.Enqueue;
        viewer.ControlReceived += toViewer.Enqueue;
        host.ControlReceived += toHost.Enqueue;

        Assert.True(Within(() => host.Snapshot().State == LinkState.Connected && viewer.Snapshot().State == LinkState.Connected),
            "no session");
        // The viewer reached the PC the server named, whatever path it took.
        Assert.Equal(hostKey.Fingerprint, viewer.Snapshot().PeerFingerprint);
        Assert.Equal(viewerKey.Fingerprint, host.Snapshot().PeerFingerprint);

        host.SendControl(ControlMessage.VideoFormat(1920, 1080));
        viewer.SendControl(ControlMessage.KeyFrameRequest());
        var random = new Random(12);
        var sent = new List<byte[]>();
        for (int i = 0; i < 30; i++)
        {
            var frame = new byte[i == 0 ? 120_000 : random.Next(3_000, 20_000)];
            random.NextBytes(frame);
            sent.Add(frame);
            host.SendFrame(frame, keyFrame: i == 0);
            Thread.Sleep(5);
        }

        Assert.True(Within(() => frames.Count == sent.Count && toViewer.Count == 1 && toHost.Count == 1),
            $"{frames.Count} frames, {toViewer.Count} + {toHost.Count} control messages");
        var received = frames.ToList();
        for (int i = 0; i < sent.Count; i++) Assert.Equal(sent[i], received[i].Data);
        Assert.True(ControlMessage.TryReadVideoFormat(toViewer.Single(), out int width, out int height));
        Assert.Equal((1920, 1080), (width, height));
    }

    private static bool Within(Func<bool> condition, int ms = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    private static void Close(ConnectedPath path)
    {
        if (path.Transport is TcpRelayTransport tcp) tcp.Dispose();
    }

    [Fact]
    public async Task The_address_check_finds_the_routers_public_address_and_the_other_PC_hears_it()
    {
        var home = _net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var office = _net.AddSite("office", IPAddress.Parse("198.51.100.7"));
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var viewer = await Pc(home, "192.168.1.20", viewerKey);
        var host = await Pc(office, "10.0.0.8", hostKey);

        Assert.Equal(IPAddress.Parse("203.0.113.5"), viewer.Connector.PublicUdp!.Address);
        var found = await viewer.Client.FindAsync(host.Client.Id);
        Assert.Contains(found.Peer!.Addresses, a => a.Kind == AddressKind.Public && a.EndPoint.Equals(host.Connector.PublicUdp));
        Assert.Contains(found.Peer!.Addresses, a => a.Kind == AddressKind.Lan && a.EndPoint.Equals(At("10.0.0.8", 47920)));
    }

    [Fact]
    public async Task PCs_at_two_sites_punch_through_both_routers_and_connect_directly()
    {
        var home = _net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var office = _net.AddSite("office", IPAddress.Parse("198.51.100.7"));
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var viewer = await Pc(home, "192.168.1.20", viewerKey);
        var host = await Pc(office, "10.0.0.8", hostKey);

        var (viewerPath, hostPath) = await ConnectAndRunSession(viewer, viewerKey, host, hostKey);

        Assert.Equal(PathKind.Direct, viewerPath.Kind);
        Assert.Equal(PathKind.Direct, hostPath.Kind);
        Assert.Equal(host.Connector.PublicUdp, viewerPath.Remote);
        Assert.Equal(viewer.Connector.PublicUdp, hostPath.Remote);
    }

    [Fact]
    public async Task PCs_on_one_network_connect_by_LAN_address()
    {
        var home = _net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var viewer = await Pc(home, "192.168.1.20", viewerKey);
        var host = await Pc(home, "192.168.1.21", hostKey);

        var (viewerPath, hostPath) = await ConnectAndRunSession(viewer, viewerKey, host, hostKey);

        Assert.Equal(PathKind.Direct, viewerPath.Kind);
        Assert.Equal(At("192.168.1.21", 47920), viewerPath.Remote);
        Assert.Equal(At("192.168.1.20", 47920), hostPath.Remote);
    }

    [Fact]
    public async Task With_the_direct_path_blocked_both_PCs_use_the_UDP_relay()
    {
        var home = _net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var office = _net.AddSite("office", IPAddress.Parse("198.51.100.7"));
        _net.Block(home, office);
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var viewer = await Pc(home, "192.168.1.20", viewerKey);
        var host = await Pc(office, "10.0.0.8", hostKey);

        var (viewerPath, hostPath) = await ConnectAndRunSession(viewer, viewerKey, host, hostKey);

        Assert.Equal(PathKind.Relay, viewerPath.Kind);
        Assert.Equal(PathKind.Relay, hostPath.Kind);
        Assert.Equal(ServerUdp, viewerPath.Remote);
        Assert.Equal(ServerUdp, hostPath.Remote);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_PC_with_UDP_blocked_uses_the_TCP_443_relay(bool viewerBlocked, bool hostBlocked)
    {
        var home = _net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var office = _net.AddSite("office", IPAddress.Parse("198.51.100.7"));
        home.UdpBlocked = viewerBlocked;
        office.UdpBlocked = hostBlocked;
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var viewer = await Pc(home, "192.168.1.20", viewerKey);
        var host = await Pc(office, "10.0.0.8", hostKey);
        Assert.Equal(!viewerBlocked, viewer.Connector.UdpWorks);
        Assert.Equal(!hostBlocked, host.Connector.UdpWorks);

        var (viewerPath, hostPath) = await ConnectAndRunSession(viewer, viewerKey, host, hostKey);

        Assert.Equal(viewerBlocked ? PathKind.TcpRelay : PathKind.Relay, viewerPath.Kind);
        Assert.Equal(hostBlocked ? PathKind.TcpRelay : PathKind.Relay, hostPath.Kind);
    }

    [Fact]
    public async Task When_the_other_PC_leaves_the_TCP_relay_this_PC_stays_online()
    {
        var home = _net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var office = _net.AddSite("office", IPAddress.Parse("198.51.100.7"));
        home.UdpBlocked = office.UdpBlocked = true;
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var viewer = await Pc(home, "192.168.1.20", viewerKey);
        var host = await Pc(office, "10.0.0.8", hostKey);
        var accepting = new TaskCompletionSource<Task<ConnectedPath>>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Client.Incoming += caller => accepting.TrySetResult(host.Connector.AcceptAsync(caller));
        var found = await viewer.Client.FindAsync(host.Client.Id);
        var viewerPath = await viewer.Connector.ConnectAsync(found.Peer!);
        using var relay = Assert.IsType<TcpRelayTransport>(viewerPath.Transport);
        await await accepting.Task;

        // The remote PC goes; the viewer's session still sends for a moment before it times out.
        await host.Client.DisposeAsync();
        Assert.True(Within(() => _server.Online == 1));
        for (int i = 0; i < 20; i++) relay.Send(new byte[100], relay.Peer);

        await Task.Delay(300);
        Assert.True(viewer.Client.IsConnected);
        Assert.Equal(NotFoundReason.Offline, (await viewer.Client.FindAsync(host.Client.Id)).Reason);
    }
}
