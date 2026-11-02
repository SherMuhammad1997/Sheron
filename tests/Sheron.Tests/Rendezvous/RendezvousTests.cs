using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;
using Sheron.Server;

namespace Sheron.Tests.Rendezvous;

/// <summary>
/// Plan step 11 "Done when": two clients on this machine register with a local server, get IDs and find each other
/// (TRD › Server › Rendezvous protocol). The server uses a fresh self-signed certificate; the clients pin it.
/// </summary>
public class RendezvousTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sheron-server-").FullName;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = ServerCertificate.Create();
    private byte[] Pin => RendezvousWire.Pin(_certificate);

    private RendezvousServer Server(ServerOptions? options = null)
    {
        var server = new RendezvousServer(new IPEndPoint(IPAddress.Loopback, 0), _certificate,
            new IdRegistry(Path.Combine(_folder, "registry.txt")), options);
        server.Start();
        return server;
    }

    private Task<RendezvousClient> Connect(RendezvousServer server, DeviceKey key, int udpPort = 47920, TimeSpan? pingEvery = null) =>
        RendezvousClient.ConnectAsync("127.0.0.1", server.LocalEndPoint.Port, Pin, key,
            [new PeerAddress(AddressKind.Lan, new IPEndPoint(IPAddress.Parse("192.168.1.20"), udpPort))], pingEvery: pingEvery);

    private static bool Within(Func<bool> condition, int ms = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    [Fact]
    public async Task Two_PCs_register_get_different_IDs_and_find_each_other()
    {
        await using var server = Server();
        using var keyA = DeviceKey.Create();
        using var keyB = DeviceKey.Create();
        await using var a = await Connect(server, keyA, 50001);
        await using var b = await Connect(server, keyB, 50002);
        var incoming = new ConcurrentQueue<PeerInfo>();
        b.Incoming += incoming.Enqueue;

        Assert.True(RendezvousWire.IsId(a.Id) && RendezvousWire.IsId(b.Id));
        Assert.NotEqual(a.Id, b.Id);

        var found = await a.FindAsync(b.Id);

        var peer = Assert.IsType<PeerInfo>(found.Peer);
        Assert.Equal(b.Id, peer.Id);
        Assert.Equal(keyB.PublicKey, peer.PublicKey);
        Assert.Contains(peer.Addresses, x => x.Kind == AddressKind.Lan && x.EndPoint.Equals(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50002)));
        // The public UDP address comes only from the address check (Plan step 12; ConnectionTests).
        Assert.DoesNotContain(peer.Addresses, x => x.Kind == AddressKind.Public);

        // The other PC hears about the asker, with the same connect ID.
        Assert.True(Within(() => !incoming.IsEmpty));
        incoming.TryDequeue(out var caller);
        Assert.Equal(a.Id, caller!.Id);
        Assert.Equal(keyA.PublicKey, caller.PublicKey);
        Assert.Equal(peer.ConnectId, caller.ConnectId);
    }

    [Fact]
    public async Task The_same_key_keeps_its_ID_after_the_server_restarts()
    {
        using var key = DeviceKey.Create();
        uint first;
        await using (var server = Server())
        await using (var client = await Connect(server, key))
            first = client.Id;

        await using var restarted = Server();
        await using var again = await Connect(restarted, key);
        Assert.Equal(first, again.Id);
    }

    [Fact]
    public async Task Finding_an_unknown_or_offline_ID_says_which()
    {
        await using var server = Server();
        await using var asker = await Connect(server, DeviceKey.Create());
        uint gone;
        await using (var leaving = await Connect(server, DeviceKey.Create()))
            gone = leaving.Id;
        Assert.True(Within(() => server.Online == 1));

        Assert.Equal(NotFoundReason.Unknown, (await asker.FindAsync(123_456_789)).Reason);
        Assert.Equal(NotFoundReason.Offline, (await asker.FindAsync(gone)).Reason);
    }

    [Fact]
    public async Task A_server_without_the_pinned_key_is_refused()
    {
        await using var server = Server();
        var wrongPin = new byte[32];

        await Assert.ThrowsAsync<AuthenticationException>(() => RendezvousClient.ConnectAsync("127.0.0.1",
            server.LocalEndPoint.Port, wrongPin, DeviceKey.Create(), []));
    }

    /// <summary>Someone who has a PC's public key but not its private key cannot take its ID.</summary>
    [Fact]
    public async Task A_register_signed_with_another_key_is_refused()
    {
        await using var server = Server();
        using var victim = DeviceKey.Create();
        using var thief = DeviceKey.Create();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint.Port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync("localhost");

        var challenge = await RendezvousWire.ReadAsync(tls, default);
        byte[] signature = thief.Sign(RendezvousWire.RegisterSigned(challenge!.Value.Body.AsSpan(1)));
        await RendezvousWire.WriteAsync(tls, RendezvousType.Register, (byte[])[.. victim.PublicKey, .. signature, 0], default);

        Assert.Null(await RendezvousWire.ReadAsync(tls, default));   // closed, no ID given
        Assert.Equal(0, server.Online);
    }

    [Fact]
    public async Task Too_many_finds_in_a_minute_are_refused()
    {
        await using var server = Server(ServerOptions.Default with { FindsPerMinute = 3 });
        await using var asker = await Connect(server, DeviceKey.Create());

        for (int i = 0; i < 3; i++) Assert.Equal(NotFoundReason.Unknown, (await asker.FindAsync(123_456_789)).Reason);
        Assert.Equal(NotFoundReason.TooManyTries, (await asker.FindAsync(123_456_789)).Reason);
    }

    [Fact]
    public async Task A_silent_PC_is_dropped_and_a_pinging_one_stays()
    {
        await using var server = Server(ServerOptions.Default with { IdleTimeout = TimeSpan.FromMilliseconds(400) });
        await using var silent = await Connect(server, DeviceKey.Create(), pingEvery: TimeSpan.FromHours(1));
        await using var pinging = await Connect(server, DeviceKey.Create(), pingEvery: TimeSpan.FromMilliseconds(100));

        // The client's state, not its Disconnected event: on a busy machine the drop can come before a handler is added.
        Assert.True(Within(() => !silent.IsConnected), "the silent PC was not dropped");
        Assert.True(pinging.IsConnected);
        Thread.Sleep(600);
        Assert.Equal(1, server.Online);
    }

    [Fact]
    public void IDs_show_in_three_groups_of_three()
    {
        Assert.Equal("482 917 365", RendezvousWire.Show(482_917_365));
    }

    [Theory]
    [InlineData("713 152 388", 713_152_388u)]
    [InlineData("713152388", 713_152_388u)]
    [InlineData(" 713-152-388 ", 713_152_388u)]
    [InlineData("71315238", null)]       // 8 digits
    [InlineData("099 999 999", null)]    // below the first ID
    [InlineData("192.168.1.20", null)]   // an address, not an ID
    [InlineData("71315238x", null)]
    public void An_ID_is_typed_with_or_without_its_spaces(string typed, uint? id)
    {
        Assert.Equal(id is not null, RendezvousWire.TryParseId(typed, out uint parsed));
        if (id is { } expected) Assert.Equal(expected, parsed);
    }

    public void Dispose()
    {
        _certificate.Dispose();
        Directory.Delete(_folder, recursive: true);
    }
}
