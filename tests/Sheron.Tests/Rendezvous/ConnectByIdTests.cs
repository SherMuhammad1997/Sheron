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
/// Plan step 14 through the network simulator (TRD › Connect by ID): the remote PC's session runs on one half of its
/// shared socket before anyone calls; a viewer finds it by ID, the path is worked out on the other half, and the
/// viewer is let in with the password and gets video, on every path. The server is the real one.
/// </summary>
[Collection(Timing.Name)]
public class ConnectByIdTests : IAsyncLifetime
{
    private static readonly IPEndPoint ServerUdp = new(IPAddress.Parse("10.10.10.1"), 443);
    private static readonly LinkTimings Quick = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(3));
    private const string Unattended = "unattended 1";

    private readonly string _folder = Directory.CreateTempSubdirectory("sheron-byid-").FullName;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = ServerCertificate.Create();
    private readonly SimNetwork _net = new();
    private readonly SimTransport _serverUdp;
    private readonly RendezvousServer _server;
    private readonly List<IDisposable> _owned = [];
    private readonly List<IAsyncDisposable> _clients = [];

    public ConnectByIdTests()
    {
        _serverUdp = _net.Bind(_net.AddSite("internet", null), ServerUdp);
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
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        foreach (var client in _clients) await client.DisposeAsync();
        await _server.DisposeAsync();
        _serverUdp.Dispose();
    }

    private T Own<T>(T thing) where T : IDisposable
    {
        _owned.Add(thing);
        return thing;
    }

    private Task<RendezvousClient> Register(DeviceKey key, IPEndPoint lan) =>
        RendezvousClient.ConnectAsync("127.0.0.1", _server.LocalEndPoint.Port, RendezvousWire.Pin(_certificate), key,
            [new PeerAddress(AddressKind.Lan, lan)]);

    /// <summary>The remote PC as the sharing window sets it up: socket, splitter, session, then the server.</summary>
    private sealed record Host(DeviceKey Key, RendezvousClient Client, HostRendezvous Rendezvous, Link Link, HostAccess Access);

    private async Task<Host> StartHost(SimSite site, string lan)
    {
        var key = Own(DeviceKey.Create());
        var udp = Own(_net.Bind(site, new IPEndPoint(IPAddress.Parse(lan), 47920)));
        var mux = Own(new PacketMux(udp));
        var link = Own(Link.Host(key, mux.Session, Quick));
        var access = Own(new HostAccess(link, new AccessGate(PasswordHash.Create(Unattended))));
        var client = await Register(key, udp.LocalEndPoint);
        _clients.Add(client);
        var rendezvous = Own(new HostRendezvous(client, mux, ServerUdp));
        await rendezvous.CheckAddressAsync();
        return new Host(key, client, rendezvous, link, access);
    }

    /// <summary>
    /// A viewer at <paramref name="site"/> connects to <paramref name="host"/> by ID with the fixed password; once let in,
    /// the remote PC sends frames. Returns the viewer's path, and the remote PC's.
    /// </summary>
    private async Task<(PathKind Viewer, PathKind Host)> ViewById(Host host, SimSite site, string lan)
    {
        var hostPath = new TaskCompletionSource<PathKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Rendezvous.PathFound += (_, kind, _) => hostPath.TrySetResult(kind);
        host.Rendezvous.PathFailed += (_, error) => hostPath.TrySetException(error);

        var key = Own(DeviceKey.Create());
        var udp = Own(_net.Bind(site, new IPEndPoint(IPAddress.Parse(lan), 50000)));
        var client = await Register(key, udp.LocalEndPoint);
        _clients.Add(client);
        var connector = new Connector(client, udp, ServerUdp);
        await connector.CheckAddressAsync();

        var found = await client.FindAsync(host.Client.Id);
        var peer = Assert.IsType<PeerInfo>(found.Peer);
        var path = await connector.ConnectAsync(peer);
        if (path.Transport is TcpRelayTransport tcp) Own(tcp);
        var link = Own(Link.Viewer(key, path.Transport, path.Remote, Quick));
        var access = Own(new ViewerAccess(link, Unattended, peer.PublicKey));
        var frames = new ConcurrentQueue<AssembledFrame>();
        link.FrameReceived += frames.Enqueue;

        Assert.True(Within(() => access.IsLetIn && host.Access.IsLetIn), $"not let in: {access.Answer}");
        var random = new Random(14);
        var sent = new List<byte[]>();
        for (int i = 0; i < 20; i++)
        {
            var frame = new byte[i == 0 ? 80_000 : random.Next(2_000, 15_000)];
            random.NextBytes(frame);
            sent.Add(frame);
            host.Link.SendFrame(frame, keyFrame: i == 0);
            Thread.Sleep(5);
        }
        Assert.True(Within(() => frames.Count == sent.Count), $"{frames.Count} of {sent.Count} frames");
        Assert.Equal(sent, frames.Select(f => f.Data));
        return (path.Kind, await hostPath.Task.WaitAsync(TimeSpan.FromSeconds(10)));
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

    private (SimSite Home, SimSite Office) Sites() =>
        (_net.AddSite("home", IPAddress.Parse("203.0.113.5")), _net.AddSite("office", IPAddress.Parse("198.51.100.7")));

    [Fact]
    public async Task By_ID_through_both_routers_the_path_is_direct()
    {
        var (home, office) = Sites();
        var host = await StartHost(office, "10.0.0.8");

        Assert.Equal((PathKind.Direct, PathKind.Direct), await ViewById(host, home, "192.168.1.20"));
    }

    [Fact]
    public async Task By_ID_with_the_direct_path_blocked_both_use_the_relay()
    {
        var (home, office) = Sites();
        _net.Block(home, office);
        var host = await StartHost(office, "10.0.0.8");

        Assert.Equal((PathKind.Relay, PathKind.Relay), await ViewById(host, home, "192.168.1.20"));
    }

    [Fact]
    public async Task By_ID_a_remote_PC_with_UDP_blocked_gets_its_viewer_over_the_TCP_relay()
    {
        var (home, office) = Sites();
        office.UdpBlocked = true;
        var host = await StartHost(office, "10.0.0.8");
        Assert.False(host.Rendezvous.Connector.UdpWorks);

        Assert.Equal((PathKind.Relay, PathKind.TcpRelay), await ViewById(host, home, "192.168.1.20"));
    }

    [Fact]
    public async Task By_ID_with_UDP_blocked_on_both_sides_everything_goes_over_TCP_443()
    {
        var (home, office) = Sites();
        home.UdpBlocked = office.UdpBlocked = true;
        var host = await StartHost(office, "10.0.0.8");

        Assert.Equal((PathKind.TcpRelay, PathKind.TcpRelay), await ViewById(host, home, "192.168.1.20"));
    }

    [Fact]
    public async Task A_second_viewer_after_the_first_also_gets_through()
    {
        var (home, office) = Sites();
        _net.Block(home, office);
        var host = await StartHost(office, "10.0.0.8");

        Assert.Equal(PathKind.Relay, (await ViewById(host, home, "192.168.1.20")).Viewer);
        // A new viewer replaces the first session on the same remote PC.
        Assert.Equal(PathKind.Relay, (await ViewById(host, home, "192.168.1.21")).Viewer);
    }

    [Fact]
    public void A_viewer_sends_no_password_to_a_PC_whose_key_is_not_the_one_the_server_gave()
    {
        using var hostKey = DeviceKey.Create();
        using var viewerKey = DeviceKey.Create();
        using var someoneElse = DeviceKey.Create();
        using var host = Link.Host(hostKey, port: 0, Quick);
        var passwords = new ConcurrentQueue<byte[]>();
        host.ControlReceived += passwords.Enqueue;
        using var viewer = Link.Viewer(viewerKey, new IPEndPoint(IPAddress.Loopback, host.LocalEndPoint.Port), Quick);
        using var access = new ViewerAccess(viewer, Unattended, someoneElse.PublicKey);

        Assert.True(Within(() => access.WrongHostKey), "the wrong key was not noticed");
        Thread.Sleep(200);
        Assert.DoesNotContain(passwords, m => ControlMessage.TypeOf(m) == ControlType.AccessRequest);
    }
}
