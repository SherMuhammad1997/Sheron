using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Sheron.Core.Net;
using Sheron.Core.Security;

namespace Sheron.Core.Rendezvous;

/// <summary>What the server answered to a Find: the other PC, or why there is none.</summary>
public sealed record FindResult(PeerInfo? Peer, NotFoundReason? Reason);

/// <summary>
/// A PC's link to the Sheron server (TRD › Server › Rendezvous protocol): TLS checked by the server's pinned key,
/// registration with the device key, finding other PCs by ID, and being found. Pings every 25 s to stay online.
/// It also carries the TCP 443 relay for a PC whose UDP is blocked (Plan step 12). Events are raised on the client's
/// own reading task.
/// </summary>
public sealed class RendezvousClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultPingEvery = TimeSpan.FromSeconds(25);

    private readonly TcpClient _tcp;
    private readonly SslStream _tls;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FindResult>> _finding = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _joiningTcp = new();
    private readonly TimeSpan _pingEvery;
    private readonly SemaphoreSlim _askingStatus = new(1, 1);
    private volatile StatusQuestion? _status;
    private Task? _reading;
    private Task? _pinging;
    private int _closed;
    private volatile TcpRelayTransport? _tcpRelay;

    private RendezvousClient(TcpClient tcp, SslStream tls, TimeSpan pingEvery)
    {
        _tcp = tcp;
        _tls = tls;
        _pingEvery = pingEvery;
    }

    /// <summary>This PC's 4-character ID, tied to its device key.</summary>
    public uint Id { get; private set; }

    /// <summary>The token this PC's UDP address check carries, from Registered.</summary>
    public byte[] UdpToken { get; private set; } = [];

    /// <summary>The server's address on this TLS link: also the remote end of the TCP relay.</summary>
    public IPEndPoint ServerEndPoint { get; private set; } = new(IPAddress.None, 0);

    /// <summary>Is the link to the server still up?</summary>
    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    /// <summary>Another PC wants to connect to this one.</summary>
    public event Action<PeerInfo>? Incoming;

    /// <summary>The server closed the link, or it broke.</summary>
    public event Action? Disconnected;

    /// <summary>The viewer of the connection with this connect ID found no direct path and asks for the relay.</summary>
    public event Action<byte[]>? RelayAsked;

    /// <summary>
    /// Connects, checks the server's certificate against <paramref name="serverPin"/> (SHA-256 of its public key) and
    /// registers <paramref name="device"/>. Throws <see cref="AuthenticationException"/> when the pin does not match.
    /// </summary>
    /// <param name="addresses">This PC's LAN addresses, with the UDP port of its link socket.</param>
    public static async Task<RendezvousClient> ConnectAsync(string host, int port, byte[] serverPin, DeviceKey device,
        IReadOnlyList<PeerAddress> addresses, CancellationToken cancel = default, TimeSpan? pingEvery = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream? tls = null;
        try
        {
            await tcp.ConnectAsync(host, port, cancel).ConfigureAwait(false);
            tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                // Trusted by its pinned key alone, not by a certificate authority or its name (TRD).
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && CryptographicOperations.FixedTimeEquals(RendezvousWire.Pin(certificate), serverPin),
            }, cancel).ConfigureAwait(false);

            var client = new RendezvousClient(tcp, tls, pingEvery ?? DefaultPingEvery)
            {
                ServerEndPoint = (IPEndPoint)tcp.Client.RemoteEndPoint!,
            };
            await client.RegisterAsync(device, addresses, cancel).ConfigureAwait(false);
            client._reading = client.ReadLoop();
            client._pinging = client.PingLoop();
            return client;
        }
        catch
        {
            tls?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    private async Task RegisterAsync(DeviceKey device, IReadOnlyList<PeerAddress> addresses, CancellationToken cancel)
    {
        var challenge = await RendezvousWire.ReadAsync(_tls, cancel).ConfigureAwait(false);
        if (challenge is not { Type: RendezvousType.Challenge, Body: var body }
            || body.Length != 1 + RendezvousWire.ChallengeSize || body[0] != RendezvousWire.Version)
            throw new InvalidDataException("The server did not send a challenge.");

        byte[] signature = device.Sign(RendezvousWire.RegisterSigned(body.AsSpan(1)));
        byte[] register = [.. device.PublicKey, .. signature, .. RendezvousWire.Addresses(addresses)];
        await SendAsync(RendezvousType.Register, register, cancel).ConfigureAwait(false);

        var registered = await RendezvousWire.ReadAsync(_tls, cancel).ConfigureAwait(false);
        if (registered is not { Type: RendezvousType.Registered, Body: var idBody } || idBody.Length != 4 + RendezvousWire.TokenSize
            || !RendezvousWire.TryReadId(idBody, out uint id) || !RendezvousWire.IsId(id))
            throw new InvalidDataException("The server refused to register this PC.");
        Id = id;
        UdpToken = idBody[4..];
    }

    /// <summary>Asks the server for the PC with <paramref name="id"/>; the server also tells that PC about this one.</summary>
    public async Task<FindResult> FindAsync(uint id, CancellationToken cancel = default)
    {
        var answer = _finding.GetOrAdd(id, _ => new TaskCompletionSource<FindResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        await SendAsync(RendezvousType.Find, RendezvousWire.Id(id), cancel).ConfigureAwait(false);
        using (cancel.Register(() => answer.TrySetCanceled(cancel)))
            return await answer.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the server this PC is in a session (or free again): while busy, nobody is introduced to it and others see
    /// it as in a session (TRD › Connect by ID › One session at a time). A new link starts free.
    /// </summary>
    public Task SetBusyAsync(bool busy, CancellationToken cancel = default) =>
        SendAsync(RendezvousType.Busy, (byte[])[busy ? (byte)1 : (byte)0], cancel);

    /// <summary>Which of these PCs (1–10 IDs) are online, offline or in a session: for the Recent list. One question at a time.</summary>
    public async Task<IReadOnlyList<(uint Id, PcState State)>> StatusAsync(IReadOnlyList<uint> ids, CancellationToken cancel = default)
    {
        var body = RendezvousWire.Status(ids);
        await _askingStatus.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var question = new StatusQuestion([.. ids]);
            _status = question;
            if (!IsConnected) throw new IOException("The link to the server closed.");
            await SendAsync(RendezvousType.Status, body, cancel).ConfigureAwait(false);
            using (cancel.Register(() => question.Answer.TrySetCanceled(cancel)))
                return await question.Answer.Task.ConfigureAwait(false);
        }
        finally
        {
            _status = null;
            _askingStatus.Release();
        }
    }

    /// <summary>Viewer: no direct path to the PC of this connect ID; the server asks that PC to join the relay.</summary>
    public Task RequestRelayAsync(byte[] connectId, CancellationToken cancel = default) =>
        SendAsync(RendezvousType.RelayRequest, connectId, cancel);

    /// <summary>
    /// Joins the relay of <paramref name="connectId"/> over this TLS link, for a PC whose UDP is blocked, and waits
    /// until the other PC has joined too. The returned transport carries the session's packets through the server.
    /// </summary>
    public async Task<TcpRelayTransport> JoinTcpRelayAsync(byte[] connectId, Role role, CancellationToken cancel = default)
    {
        string key = Convert.ToHexString(connectId);
        var ready = _joiningTcp.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var transport = new TcpRelayTransport(this);
        _tcpRelay?.Dispose();
        _tcpRelay = transport;
        try
        {
            await SendAsync(RendezvousType.RelayJoin, (byte[])[.. connectId, (byte)role], cancel).ConfigureAwait(false);
            using (cancel.Register(() => ready.TrySetCanceled(cancel)))
                await ready.Task.ConfigureAwait(false);
            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
        finally
        {
            _joiningTcp.TryRemove(key, out _);
        }
    }

    /// <summary>One packet over the TCP relay, written in order by the sending task.</summary>
    internal Task SendRelayedAsync(byte[] packet, CancellationToken cancel) => SendAsync(RendezvousType.RelaySend, packet, cancel);

    internal void Forget(TcpRelayTransport transport) => Interlocked.CompareExchange(ref _tcpRelay, null, transport);

    private async Task ReadLoop()
    {
        try
        {
            while (await RendezvousWire.ReadAsync(_tls, _closing.Token).ConfigureAwait(false) is { } message)
            {
                switch (message.Type)
                {
                    case RendezvousType.Found when RendezvousWire.TryReadPeer(message.Body, out var peer):
                        if (_finding.TryRemove(peer!.Id, out var found)) found.TrySetResult(new FindResult(peer, null));
                        break;
                    case RendezvousType.NotFound when message.Body.Length == 5 && RendezvousWire.TryReadId(message.Body, out uint id):
                        if (_finding.TryRemove(id, out var missing)) missing.TrySetResult(new FindResult(null, (NotFoundReason)message.Body[4]));
                        break;
                    case RendezvousType.Incoming when RendezvousWire.TryReadPeer(message.Body, out var caller):
                        Incoming?.Invoke(caller!);
                        break;
                    case RendezvousType.Pong:
                        break;
                    case RendezvousType.StatusAnswer when RendezvousWire.TryReadStatusAnswer(message.Body, out var states):
                        // A late answer to a question given up on is not this question's.
                        if (_status is { } question && states.Select(s => s.Id).SequenceEqual(question.Ids)) question.Answer.TrySetResult(states);
                        break;
                    case RendezvousType.RelayAsked when message.Body.Length == RendezvousWire.ConnectIdSize:
                        RelayAsked?.Invoke(message.Body);
                        break;
                    case RendezvousType.RelayReady when message.Body.Length == RendezvousWire.ConnectIdSize:
                        if (_joiningTcp.TryGetValue(Convert.ToHexString(message.Body), out var joined)) joined.TrySetResult();
                        break;
                    case RendezvousType.RelayDelivered when message.Body.Length is > 0 and <= PacketLimits.MaxDatagram:
                        _tcpRelay?.Arrive(message.Body);
                        break;
                    default:
                        return;   // the server breaks its own protocol: stop
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Close();
        }
    }

    private async Task PingLoop()
    {
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                await Task.Delay(_pingEvery, _closing.Token).ConfigureAwait(false);
                await SendAsync(RendezvousType.Ping, ReadOnlyMemory<byte>.Empty, _closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task SendAsync(RendezvousType type, ReadOnlyMemory<byte> body, CancellationToken cancel)
    {
        await _writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await RendezvousWire.WriteAsync(_tls, type, body, cancel).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>Ends the link once: waiting Finds fail, and <see cref="Disconnected"/> is raised.</summary>
    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _closing.Cancel();
        foreach (var waiting in _finding.Values) waiting.TrySetException(new IOException("The link to the server closed."));
        foreach (var waiting in _joiningTcp.Values) waiting.TrySetException(new IOException("The link to the server closed."));
        _status?.Answer.TrySetException(new IOException("The link to the server closed."));
        _tls.Dispose();
        _tcp.Dispose();
        Disconnected?.Invoke();
    }

    private sealed record StatusQuestion(List<uint> Ids)
    {
        public TaskCompletionSource<List<(uint Id, PcState State)>> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        if (_reading is not null) await _reading.ConfigureAwait(false);
        if (_pinging is not null) await _pinging.ConfigureAwait(false);
        _writing.Dispose();
        _askingStatus.Dispose();
        _closing.Dispose();
    }
}
