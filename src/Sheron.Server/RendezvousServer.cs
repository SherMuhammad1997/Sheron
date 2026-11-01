using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;

namespace Sheron.Server;

/// <summary>Timeouts and limits of the server (TRD › Server › Rendezvous protocol); tests shorten them.</summary>
internal sealed record ServerOptions(
    TimeSpan IdleTimeout,
    TimeSpan RegisterTimeout,
    int FindsPerMinute,
    int ConnectionsPerMinutePerIp)
{
    public static readonly ServerOptions Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 20, 30);
}

/// <summary>
/// The rendezvous server (Plan step 11): PCs connect over TLS, register with their device key, get their 9-digit ID,
/// and find each other by ID. It only introduces PCs; it never sees session keys or screens.
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
    private Task? _accepting;

    public RendezvousServer(IPEndPoint bindTo, X509Certificate2 certificate, IdRegistry registry, ServerOptions? options = null)
    {
        _listener = new TcpListener(bindTo);
        _certificate = certificate;
        _registry = registry;
        _options = options ?? ServerOptions.Default;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public int Online => _online.Count;

    /// <summary>Messages for the log (no keys, no addresses beyond the IP); the program writes them out.</summary>
    public event Action<string>? Log;

    public void Start()
    {
        _listener.Start();
        _accepting = AcceptLoop();
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
        // Addresses as the PC gave them, plus the one the server sees, with the PC's UDP port (Plan step 12 checks it).
        int udpPort = lan.Select(a => a.EndPoint.Port).FirstOrDefault();
        connection.Addresses = [.. lan.Where(a => a.Kind == AddressKind.Lan), new PeerAddress(AddressKind.Public, new IPEndPoint(connection.Ip, udpPort))];

        // One connection per device: a new one replaces the old.
        if (_online.TryGetValue(connection.Id, out var earlier)) earlier.Dispose();
        _online[connection.Id] = connection;
        await connection.SendAsync(RendezvousType.Registered, RendezvousWire.Id(connection.Id), cancel).ConfigureAwait(false);
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
                default:
                    return;
            }
        }
    }

    /// <summary>Introduces two PCs: Found to the asker, Incoming to the target, with one connect ID.</summary>
    private async Task Find(Connection asker, uint id)
    {
        if (!asker.AllowFind(_options.FindsPerMinute))
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

        byte[] connectId = RandomNumberGenerator.GetBytes(RendezvousWire.ConnectIdSize);
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

    private Task NotFound(Connection asker, uint id, NotFoundReason reason) =>
        asker.SendAsync(RendezvousType.NotFound, (byte[])[.. RendezvousWire.Id(id), (byte)reason], _stopping.Token);

    /// <summary>At most so many new connections a minute from one IP address.</summary>
    private bool AllowConnection(IPAddress ip)
    {
        var times = _connectionsByIp.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
        lock (times)
        {
            var now = DateTimeOffset.UtcNow;
            while (times.Count > 0 && now - times.Peek() > TimeSpan.FromMinutes(1)) times.Dequeue();
            if (times.Count >= _options.ConnectionsPerMinutePerIp) return false;
            times.Enqueue(now);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        if (_accepting is not null) await _accepting.ConfigureAwait(false);
        foreach (var connection in _online.Values) connection.Dispose();
        _registry.Save();
        _stopping.Dispose();
    }

    /// <summary>One registered PC: its TLS stream (writes taken one at a time), ID, key and addresses.</summary>
    private sealed class Connection(TcpClient tcp, SslStream tls, IPAddress ip) : IDisposable
    {
        private readonly SemaphoreSlim _writing = new(1, 1);
        private readonly Queue<DateTimeOffset> _finds = new();

        public SslStream Tls { get; } = tls;
        public IPAddress Ip { get; } = ip;
        public uint Id { get; set; }
        public byte[] PublicKey { get; set; } = [];
        public IReadOnlyList<PeerAddress> Addresses { get; set; } = [];

        public bool AllowFind(int perMinute)
        {
            lock (_finds)
            {
                var now = DateTimeOffset.UtcNow;
                while (_finds.Count > 0 && now - _finds.Peek() > TimeSpan.FromMinutes(1)) _finds.Dequeue();
                if (_finds.Count >= perMinute) return false;
                _finds.Enqueue(now);
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

        public void Dispose()
        {
            Tls.Dispose();
            tcp.Dispose();
        }
    }
}
