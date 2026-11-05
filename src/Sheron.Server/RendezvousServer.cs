using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Sheron.Core.Net;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;

namespace Sheron.Server;

/// <summary>Timeouts and limits of the server (TRD › Server › Rendezvous protocol); tests shorten them.</summary>
internal sealed record ServerOptions(
    TimeSpan IdleTimeout,
    TimeSpan RegisterTimeout,
    int FindsPerMinute,
    int ConnectionsPerMinutePerIp,
    TimeSpan RelayIdle,
    int StatusIdsPerMinute = 30,
    int StatusIdsPerMinutePerIp = 60)
{
    public static readonly ServerOptions Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 20, 30, TimeSpan.FromSeconds(60));
}

/// <summary>
/// The rendezvous server (Plan steps 11–12): PCs connect over TLS, register with their device key, get their 9-digit
/// ID, and find each other by ID. On UDP it tells each PC its public address and relays sessions that cannot go
/// direct, bridging to the TCP relay for PCs whose UDP is blocked. It only introduces and forwards; it never sees
/// session keys or screens.
/// </summary>
internal sealed class RendezvousServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly IdRegistry _registry;
    private readonly ServerOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<uint, Connection> _online = new();
    private readonly ConcurrentDictionary<IPAddress, Queue<DateTimeOffset>> _connectionsByIp = new();
    private readonly ConcurrentDictionary<IPAddress, Queue<DateTimeOffset>> _findsByIp = new();
    private readonly ConcurrentDictionary<IPAddress, Queue<DateTimeOffset>> _statusByIp = new();
    private readonly IPacketTransport? _udp;
    private readonly ConcurrentDictionary<string, Connection> _byToken = new();
    private readonly ConcurrentDictionary<string, Relay> _relays = new();
    private readonly ConcurrentDictionary<IPEndPoint, (Relay Relay, Role Role)> _relayByEndpoint = new();
    private Task? _accepting;
    private Thread? _udpThread;

    /// <param name="udp">The UDP side (address check and relay, Plan step 12); null for none.</param>
    public RendezvousServer(IPEndPoint bindTo, X509Certificate2 certificate, IdRegistry registry, ServerOptions? options = null,
        IPacketTransport? udp = null)
    {
        _listener = new TcpListener(bindTo);
        _certificate = certificate;
        _registry = registry;
        _options = options ?? ServerOptions.Default;
        _udp = udp;
    }

    /// <summary>Relays waiting for joins or carrying sessions.</summary>
    public int Relays => _relays.Count;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public int Online => _online.Count;

    /// <summary>Messages for the log (no keys, no addresses beyond the IP); the program writes them out.</summary>
    public event Action<string>? Log;

    public void Start()
    {
        _listener.Start();
        _accepting = AcceptLoop();
        if (_udp is null) return;
        _udpThread = new Thread(UdpLoop) { IsBackground = true, Name = "Sheron server UDP" };
        _udpThread.Start();
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Serve(tcp);
        }
    }

    /// <summary>One PC from TLS to goodbye. Anything malformed or late closes the connection.</summary>
    private async Task Serve(TcpClient tcp)
    {
        var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        var ip = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        if (!AllowConnection(ip))
        {
            tcp.Dispose();
            return;
        }

        Connection? connection = null;
        try
        {
            tcp.NoDelay = true;
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            using var registering = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            registering.CancelAfter(_options.RegisterTimeout);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, registering.Token)
                .ConfigureAwait(false);

            connection = new Connection(tcp, tls, ip);
            if (!await Register(connection, registering.Token).ConfigureAwait(false)) return;
            await Listen(connection).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException
            or ObjectDisposedException or System.Security.Authentication.AuthenticationException or SocketException)
        {
        }
        finally
        {
            if (connection is not null)
            {
                if (connection.Id != 0 && _online.TryRemove(new KeyValuePair<uint, Connection>(connection.Id, connection)))
                    Log?.Invoke($"{RendezvousWire.Show(connection.Id)} offline");
                _byToken.TryRemove(Convert.ToHexString(connection.Token), out _);
                foreach (var relay in _relays.Values.Where(r => r.Viewer == connection || r.Host == connection).ToList()) EndRelay(relay);
                connection.Dispose();
            }
            else
            {
                tcp.Dispose();
            }
        }
    }

    /// <summary>Challenge, signed Register, Registered. The device proves it holds the key its ID is tied to.</summary>
    private async Task<bool> Register(Connection connection, CancellationToken cancel)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(RendezvousWire.ChallengeSize);
        await connection.SendAsync(RendezvousType.Challenge, (byte[])[RendezvousWire.Version, .. challenge], cancel).ConfigureAwait(false);

        var message = await RendezvousWire.ReadAsync(connection.Tls, cancel).ConfigureAwait(false);
        if (message is not { Type: RendezvousType.Register, Body: var body } || !TryReadRegister(body, challenge, out var key, out var lan))
            return false;

        connection.PublicKey = key;
        connection.Id = _registry.IdFor(connection.PublicKey, DateTimeOffset.UtcNow);
        // LAN addresses as the PC gave them; its public UDP address comes from the address check (Plan step 12).
        connection.Lan = [.. lan.Where(a => a.Kind == AddressKind.Lan)];
        _byToken[Convert.ToHexString(connection.Token)] = connection;

        // One connection per device: a new one replaces the old.
        if (_online.TryGetValue(connection.Id, out var earlier)) earlier.Dispose();
        _online[connection.Id] = connection;
        await connection.SendAsync(RendezvousType.Registered, (byte[])[.. RendezvousWire.Id(connection.Id), .. connection.Token], cancel)
            .ConfigureAwait(false);
        Log?.Invoke($"{RendezvousWire.Show(connection.Id)} online from {connection.Ip}");
        return true;
    }

    /// <summary>A Register body: device public key, its signature over the challenge, and the PC's addresses.</summary>
    private static bool TryReadRegister(byte[] body, byte[] challenge, out byte[] key, out List<PeerAddress> addresses)
    {
        key = [];
        addresses = [];
        const int keyAndSignature = RendezvousWire.PublicKeySize + RendezvousWire.SignatureSize;
        if (body.Length < keyAndSignature + 1) return false;
        var publicKey = body.AsSpan(0, RendezvousWire.PublicKeySize);
        var signature = body.AsSpan(RendezvousWire.PublicKeySize, RendezvousWire.SignatureSize);
        if (!DeviceKey.Verify(publicKey, RendezvousWire.RegisterSigned(challenge), signature)) return false;
        if (!RendezvousWire.TryReadAddresses(body.AsSpan(keyAndSignature), out addresses, out int used) || keyAndSignature + used != body.Length)
            return false;
        key = publicKey.ToArray();
        return true;
    }

    /// <summary>Find and Ping until the PC goes, falls silent for the idle timeout, or breaks the protocol.</summary>
    private async Task Listen(Connection connection)
    {
        while (true)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            waiting.CancelAfter(_options.IdleTimeout);
            var message = await RendezvousWire.ReadAsync(connection.Tls, waiting.Token).ConfigureAwait(false);
            switch (message)
            {
                case null:
                    return;
                case { Type: RendezvousType.Ping }:
                    _registry.Touch(connection.Id, DateTimeOffset.UtcNow);
                    await connection.SendAsync(RendezvousType.Pong, ReadOnlyMemory<byte>.Empty, _stopping.Token).ConfigureAwait(false);
                    break;
                case { Type: RendezvousType.Find, Body: var body } when body.Length == 4 && RendezvousWire.TryReadId(body, out uint id):
                    await Find(connection, id).ConfigureAwait(false);
                    break;
                case { Type: RendezvousType.RelayRequest, Body: var body } when body.Length == RendezvousWire.ConnectIdSize:
                    await AskForRelay(connection, body).ConfigureAwait(false);
                    break;
                case { Type: RendezvousType.RelayJoin, Body: var body } when body.Length == RendezvousWire.ConnectIdSize + 1:
                    JoinOverTcp(connection, body);
                    break;
                case { Type: RendezvousType.Busy, Body: [var flag and (0 or 1)] }:
                    connection.Busy = flag == 1;
                    break;
                case { Type: RendezvousType.Status, Body: var body } when RendezvousWire.TryReadStatus(body, out var ids):
                    await Status(connection, ids).ConfigureAwait(false);
                    break;
                case { Type: RendezvousType.RelaySend, Body: var packet }:
                    // After the relay ends (the other PC left) its last packets are dropped, not an error.
                    if (connection.TcpRelay is { } relay) Forward(relay, relay.Viewer == connection ? Role.Viewer : Role.Host, packet);
                    break;
                default:
                    return;
            }
        }
    }

    /// <summary>Introduces two PCs: Found to the asker, Incoming to the target, with one connect ID.</summary>
    private async Task Find(Connection asker, uint id)
    {
        // Per connection and per internet address: 4-character IDs are few, so they must not be tried quickly (TRD › Requests).
        if (!asker.AllowFind(_options.FindsPerMinute, 1) || !WithinLimit(_findsByIp, asker.Ip, _options.FindsPerMinute))
        {
            await NotFound(asker, id, NotFoundReason.TooManyTries).ConfigureAwait(false);
            return;
        }
        if (!_registry.Knows(id) || id == asker.Id)
        {
            await NotFound(asker, id, NotFoundReason.Unknown).ConfigureAwait(false);
            return;
        }
        if (!_online.TryGetValue(id, out var target))
        {
            await NotFound(asker, id, NotFoundReason.Offline).ConfigureAwait(false);
            return;
        }
        // One session at a time (TRD › Connect by ID): a PC in a session is not told about anyone.
        if (target.Busy)
        {
            await NotFound(asker, id, NotFoundReason.Busy).ConfigureAwait(false);
            return;
        }

        byte[] connectId = RandomNumberGenerator.GetBytes(RendezvousWire.ConnectIdSize);
        PruneRelays();
        _relays[Convert.ToHexString(connectId)] = new Relay(connectId, asker, target);
        try
        {
            await target.SendAsync(RendezvousType.Incoming,
                RendezvousWire.Peer(new PeerInfo(connectId, asker.Id, asker.PublicKey, asker.Addresses)), _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            await NotFound(asker, id, NotFoundReason.Offline).ConfigureAwait(false);
            return;
        }
        await asker.SendAsync(RendezvousType.Found,
            RendezvousWire.Peer(new PeerInfo(connectId, target.Id, target.PublicKey, target.Addresses)), _stopping.Token).ConfigureAwait(false);
        Log?.Invoke($"{RendezvousWire.Show(asker.Id)} found {RendezvousWire.Show(target.Id)}");
    }

    /// <summary>
    /// Which of these IDs are online, for the Recent list. Each ID counts against its own limits, per connection and
    /// per internet address; over them, every ID is answered "not checked".
    /// </summary>
    private async Task Status(Connection asker, List<uint> ids)
    {
        bool allowed = asker.AllowStatus(_options.StatusIdsPerMinute, ids.Count)
            && WithinLimit(_statusByIp, asker.Ip, _options.StatusIdsPerMinutePerIp, ids.Count);
        var states = ids.Select(id => (id, !allowed ? PcState.NotChecked
            : id != asker.Id && _online.TryGetValue(id, out var pc) ? (pc.Busy ? PcState.Busy : PcState.Online)
            : PcState.Offline)).ToList();
        await asker.SendAsync(RendezvousType.StatusAnswer, RendezvousWire.StatusAnswer(states), _stopping.Token).ConfigureAwait(false);
    }

    /// <summary>The viewer found no direct path: tell the remote PC to join the relay too.</summary>
    private async Task AskForRelay(Connection viewer, byte[] connectId)
    {
        if (!_relays.TryGetValue(Convert.ToHexString(connectId), out var relay) || relay.Viewer != viewer) return;
        relay.Touch();
        await relay.Host.SendAsync(RendezvousType.RelayAsked, connectId, _stopping.Token).ConfigureAwait(false);
    }

    /// <summary>A PC joins over its TLS connection: only one of the two PCs the connect ID was made for, in its own role.</summary>
    private void JoinOverTcp(Connection connection, byte[] body)
    {
        var role = (Role)body[^1];
        if (!Enum.IsDefined(role) || !_relays.TryGetValue(Convert.ToHexString(body.AsSpan(0, RendezvousWire.ConnectIdSize)), out var relay)) return;
        if (connection != (role == Role.Viewer ? relay.Viewer : relay.Host)) return;
        // A PC that tried UDP first and now joins over TLS gets its packets over TLS from now on.
        if (relay.UdpOf(role) is { } old) _relayByEndpoint.TryRemove(old, out _);
        connection.TcpRelay = relay;
        relay.JoinTcp(role);
        Ready(relay);
    }

    /// <summary>UDP: address checks, relay joins, and packets to forward.</summary>
    private void UdpLoop()
    {
        var buffer = new byte[2048];
        var lastPrune = DateTimeOffset.UtcNow;
        while (!_stopping.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow - lastPrune > TimeSpan.FromSeconds(10))
            {
                PruneRelays();
                lastPrune = DateTimeOffset.UtcNow;
            }
            int length;
            IPEndPoint from;
            try
            {
                if (!_udp!.TryReceive(buffer, TimeSpan.FromMilliseconds(50), out length, out from)) continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            var packet = buffer.AsSpan(0, length);
            if (packet.Length == 0) continue;
            if (RendezvousWire.TryReadUdp(packet, UdpMessage.AddressCheck, false, out var token, out _))
            {
                if (!_byToken.TryGetValue(Convert.ToHexString(token), out var connection)) continue;
                connection.PublicUdp = from;
                _udp.Send(RendezvousWire.AddressSeen(from), from);
            }
            else if (RendezvousWire.TryReadUdp(packet, UdpMessage.RelayJoin, true, out var connectId, out var role))
            {
                if (!_relays.TryGetValue(Convert.ToHexString(connectId), out var relay)) continue;
                if (relay.UdpOf(role) is { } old && !old.Equals(from)) _relayByEndpoint.TryRemove(old, out _);
                relay.JoinUdp(role, from);
                _relayByEndpoint[from] = (relay, role);
                Ready(relay);
            }
            else if (_relayByEndpoint.TryGetValue(from, out var joined))
            {
                Forward(joined.Relay, joined.Role, packet.ToArray());
            }
        }
    }

    /// <summary>Once both PCs have joined: Relay ready to each, on the way each joined (again for every repeated UDP join).</summary>
    private void Ready(Relay relay)
    {
        if (!relay.Joined(Role.Viewer) || !relay.Joined(Role.Host)) return;
        foreach (var role in new[] { Role.Viewer, Role.Host })
        {
            if (relay.UdpOf(role) is { } endpoint)
                _udp?.Send(RendezvousWire.Udp(UdpMessage.RelayReady, relay.ConnectId), endpoint);
            else if (!relay.ReadySentOverTcp(role))
                _ = Send(role == Role.Viewer ? relay.Viewer : relay.Host, RendezvousType.RelayReady, relay.ConnectId);
        }
    }

    /// <summary>One packet from one side to the other, over whatever the other side joined with.</summary>
    private void Forward(Relay relay, Role from, byte[] packet)
    {
        if (!relay.Joined(Role.Viewer) || !relay.Joined(Role.Host) || packet.Length > Core.Net.PacketLimits.MaxDatagram) return;
        relay.Touch();
        var to = from == Role.Viewer ? Role.Host : Role.Viewer;
        if (relay.UdpOf(to) is { } endpoint) _udp?.Send(packet, endpoint);
        else (to == Role.Viewer ? relay.Viewer : relay.Host).Deliver(packet, _stopping.Token);
    }

    private async Task Send(Connection connection, RendezvousType type, byte[] body)
    {
        try
        {
            await connection.SendAsync(type, body, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private void PruneRelays()
    {
        foreach (var relay in _relays.Values.Where(r => r.IdleFor > _options.RelayIdle).ToList()) EndRelay(relay);
    }

    private void EndRelay(Relay relay)
    {
        _relays.TryRemove(Convert.ToHexString(relay.ConnectId), out _);
        foreach (var role in new[] { Role.Viewer, Role.Host })
            if (relay.UdpOf(role) is { } endpoint) _relayByEndpoint.TryRemove(endpoint, out _);
        if (relay.Viewer.TcpRelay == relay) relay.Viewer.TcpRelay = null;
        if (relay.Host.TcpRelay == relay) relay.Host.TcpRelay = null;
    }

    private Task NotFound(Connection asker, uint id, NotFoundReason reason) =>
        asker.SendAsync(RendezvousType.NotFound, (byte[])[.. RendezvousWire.Id(id), (byte)reason], _stopping.Token);

    /// <summary>At most so many new connections a minute from one IP address.</summary>
    private bool AllowConnection(IPAddress ip) => WithinLimit(_connectionsByIp, ip, _options.ConnectionsPerMinutePerIp);

    /// <summary>
    /// Counts <paramref name="count"/> more for <paramref name="ip"/>; false (and not counted) when that would make more
    /// than <paramref name="perMinute"/> in the last minute.
    /// </summary>
    private static bool WithinLimit(ConcurrentDictionary<IPAddress, Queue<DateTimeOffset>> counts, IPAddress ip, int perMinute, int count = 1) =>
        Connection.Allow(counts.GetOrAdd(ip, _ => new Queue<DateTimeOffset>()), perMinute, count);

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        if (_accepting is not null) await _accepting.ConfigureAwait(false);
        _udpThread?.Join(TimeSpan.FromSeconds(1));
        foreach (var connection in _online.Values) connection.Dispose();
        _registry.Save();
        _stopping.Dispose();
    }

    /// <summary>One registered PC: its TLS stream (writes taken one at a time), ID, key and addresses.</summary>
    private sealed class Connection(TcpClient tcp, SslStream tls, IPAddress ip) : IDisposable
    {
        /// <summary>Relayed packets waiting for a PC on the TCP relay; when it cannot keep up, new ones are dropped.</summary>
        public const int RelayQueue = 1024;

        private readonly SemaphoreSlim _writing = new(1, 1);
        private readonly Queue<DateTimeOffset> _finds = new();
        private readonly Queue<DateTimeOffset> _statusIds = new();
        private readonly Channel<byte[]> _relayed = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(RelayQueue) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        private int _delivering;

        public SslStream Tls { get; } = tls;
        public IPAddress Ip { get; } = ip;
        public uint Id { get; set; }
        public byte[] PublicKey { get; set; } = [];
        /// <summary>The token the PC's UDP address check carries.</summary>
        public byte[] Token { get; } = RandomNumberGenerator.GetBytes(RendezvousWire.TokenSize);
        public IReadOnlyList<PeerAddress> Lan { get; set; } = [];
        /// <summary>Where the PC's UDP packets come from on the internet; null until its address check (or if UDP is blocked).</summary>
        public volatile IPEndPoint? PublicUdp;
        /// <summary>The relay this PC joined over TLS, if any.</summary>
        public volatile Relay? TcpRelay;
        /// <summary>In a session: nobody is introduced to it (TRD › Connect by ID › One session at a time).</summary>
        public volatile bool Busy;

        /// <summary>LAN addresses, and the checked public UDP address when there is one.</summary>
        public IReadOnlyList<PeerAddress> Addresses =>
            PublicUdp is { } seen ? [.. Lan, new PeerAddress(AddressKind.Public, seen)] : Lan;

        public bool AllowFind(int perMinute, int count) => Allow(_finds, perMinute, count);

        public bool AllowStatus(int perMinute, int count) => Allow(_statusIds, perMinute, count);

        /// <summary>Counts <paramref name="count"/> more in <paramref name="times"/> unless that makes more than <paramref name="perMinute"/> in a minute.</summary>
        public static bool Allow(Queue<DateTimeOffset> times, int perMinute, int count)
        {
            lock (times)
            {
                var now = DateTimeOffset.UtcNow;
                while (times.Count > 0 && now - times.Peek() > TimeSpan.FromMinutes(1)) times.Dequeue();
                if (times.Count + count > perMinute) return false;
                for (int i = 0; i < count; i++) times.Enqueue(now);
                return true;
            }
        }

        public async Task SendAsync(RendezvousType type, ReadOnlyMemory<byte> body, CancellationToken cancel)
        {
            await _writing.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                await RendezvousWire.WriteAsync(Tls, type, body, cancel).ConfigureAwait(false);
            }
            finally
            {
                _writing.Release();
            }
        }

        /// <summary>Queues one relayed packet; a single task writes them out in order as Relay data.</summary>
        public void Deliver(byte[] packet, CancellationToken cancel)
        {
            if (!_relayed.Writer.TryWrite(packet) || Interlocked.Exchange(ref _delivering, 1) != 0) return;
            _ = DeliverLoop(cancel);
        }

        private async Task DeliverLoop(CancellationToken cancel)
        {
            try
            {
                await foreach (var packet in _relayed.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
                    await SendAsync(RendezvousType.RelayDelivered, packet, cancel).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        public void Dispose()
        {
            _relayed.Writer.TryComplete();
            Tls.Dispose();
            tcp.Dispose();
        }
    }

    /// <summary>One connect ID's relay: the two PCs it was made for, and how each has joined.</summary>
    private sealed class Relay(byte[] connectId, Connection viewer, Connection host)
    {
        private readonly object _lock = new();
        private IPEndPoint? _viewerUdp, _hostUdp;
        private bool _viewerTcp, _hostTcp, _viewerToldTcp, _hostToldTcp;
        private long _lastActive = Environment.TickCount64;

        public byte[] ConnectId { get; } = connectId;
        public Connection Viewer { get; } = viewer;
        public Connection Host { get; } = host;
        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActive));

        public void Touch() => Interlocked.Exchange(ref _lastActive, Environment.TickCount64);

        public IPEndPoint? UdpOf(Role role)
        {
            lock (_lock) return role == Role.Viewer ? _viewerUdp : _hostUdp;
        }

        public bool Joined(Role role)
        {
            lock (_lock) return role == Role.Viewer ? _viewerUdp is not null || _viewerTcp : _hostUdp is not null || _hostTcp;
        }

        public void JoinUdp(Role role, IPEndPoint endpoint)
        {
            lock (_lock)
            {
                if (role == Role.Viewer) _viewerUdp = endpoint;
                else _hostUdp = endpoint;
            }
            Touch();
        }

        public void JoinTcp(Role role)
        {
            lock (_lock)
            {
                if (role == Role.Viewer) (_viewerTcp, _viewerUdp) = (true, null);
                else (_hostTcp, _hostUdp) = (true, null);
            }
            Touch();
        }

        /// <summary>True only the first time: Relay ready goes once over TCP.</summary>
        public bool ReadySentOverTcp(Role role)
        {
            lock (_lock)
            {
                bool sent = role == Role.Viewer ? _viewerToldTcp : _hostToldTcp;
                if (role == Role.Viewer) _viewerToldTcp = true;
                else _hostToldTcp = true;
                return sent;
            }
        }
    }
}
