using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Sheron.Core.Net;

namespace Sheron.Core.Rendezvous;

/// <summary>How long each stage of finding a path may take (TRD › Server › Direct link, relay and TCP fallback).</summary>
public sealed record ConnectTimings(
    TimeSpan PunchFor,
    TimeSpan PunchEvery,
    TimeSpan CheckEvery,
    int Checks,
    TimeSpan RelayWait,
    TimeSpan AcceptWait,
    TimeSpan HandshakeWait)
{
    public static readonly ConnectTimings Default = new(
        PunchFor: TimeSpan.FromSeconds(1.5),
        PunchEvery: TimeSpan.FromMilliseconds(100),
        CheckEvery: TimeSpan.FromMilliseconds(300),
        Checks: 3,
        RelayWait: TimeSpan.FromSeconds(3),
        AcceptWait: TimeSpan.FromSeconds(15),
        HandshakeWait: TimeSpan.FromSeconds(3));
}

public enum PathKind
{
    /// <summary>Straight to the other PC, over the LAN or through both routers.</summary>
    Direct,
    /// <summary>Through the server's UDP relay.</summary>
    Relay,
    /// <summary>Through the server over this PC's TLS link on TCP 443 (its UDP is blocked).</summary>
    TcpRelay,
}

/// <summary>
/// The path a session runs on: the transport to give the link, and where its packets go (the other PC or the
/// relay). A <see cref="TcpRelayTransport"/> belongs to the caller, who disposes it after the session.
/// </summary>
public sealed record ConnectedPath(PathKind Kind, IPacketTransport Transport, IPEndPoint Remote);

/// <summary>
/// Finds the path for a session once the server has introduced two PCs (Plan step 12): punching straight through
/// both routers first, then the server's UDP relay, then the TCP 443 relay. All UDP goes through this PC's one
/// socket, so its router keeps one mapping. Only one connection is worked out at a time per socket, and not while a
/// session is reading from it.
/// </summary>
public sealed class Connector
{
    private readonly RendezvousClient _server;
    private readonly IPacketTransport _udp;
    private readonly IPEndPoint _serverUdp;
    private readonly ConnectTimings _timings;
    private readonly ConcurrentDictionary<string, bool> _relayAsked = new();

    /// <param name="udp">This PC's UDP socket: the one whose port went to the server as its LAN address.</param>
    /// <param name="serverUdp">The server's UDP address (its IP, port 443 by default).</param>
    public Connector(RendezvousClient server, IPacketTransport udp, IPEndPoint serverUdp, ConnectTimings? timings = null)
    {
        _server = server;
        _udp = udp;
        _serverUdp = serverUdp;
        _timings = timings ?? ConnectTimings.Default;
        _server.RelayAsked += connectId => _relayAsked[Convert.ToHexString(connectId)] = true;
    }

    /// <summary>This PC's UDP address as the server saw it; null until the address check, or when UDP is blocked.</summary>
    public IPEndPoint? PublicUdp { get; private set; }

    public bool UdpWorks => PublicUdp is not null;

    /// <summary>
    /// Address check: sends the server this PC's token over UDP, up to <see cref="ConnectTimings.Checks"/> times, and
    /// learns its public address. False when no answer came: UDP is blocked, and sessions will use the TCP relay.
    /// </summary>
    public Task<bool> CheckAddressAsync(CancellationToken cancel = default) => Task.Run(() =>
    {
        var check = RendezvousWire.Udp(UdpMessage.AddressCheck, _server.UdpToken);
        var buffer = new byte[PacketLimits.MaxDatagram + 1];
        for (int i = 0; i < _timings.Checks; i++)
        {
            _udp.Send(check, _serverUdp);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < _timings.CheckEvery)
            {
                cancel.ThrowIfCancellationRequested();
                if (!_udp.TryReceive(buffer, _timings.CheckEvery - clock.Elapsed, out int length, out var from)) continue;
                if (from.Equals(_serverUdp) && RendezvousWire.TryReadAddressSeen(buffer.AsSpan(0, length), out var seen))
                {
                    PublicUdp = seen;
                    return true;
                }
            }
        }
        PublicUdp = null;
        return false;
    }, cancel);

    /// <summary>
    /// Viewer, after Found: the path to the other PC. Throws <see cref="TimeoutException"/> when none works (the other
    /// PC never answered or joined the relay).
    /// </summary>
    public async Task<ConnectedPath> ConnectAsync(PeerInfo peer, CancellationToken cancel = default)
    {
        if (UdpWorks && await Task.Run(() => Punch(peer, cancel), cancel).ConfigureAwait(false) is { } direct)
            return new ConnectedPath(PathKind.Direct, _udp, direct);
        return await RelayAsync(peer, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Viewer: the relay without punching, for when a direct path did not finish its session handshake within
    /// <see cref="ConnectTimings.HandshakeWait"/> (TRD › Direct handshake check). UDP relay, else TCP 443.
    /// </summary>
    public async Task<ConnectedPath> RelayAsync(PeerInfo peer, CancellationToken cancel = default)
    {
        await _server.RequestRelayAsync(peer.ConnectId, cancel).ConfigureAwait(false);
        if (UdpWorks && await Task.Run(() => JoinUdpRelay(peer.ConnectId, Role.Viewer, cancel), cancel).ConfigureAwait(false))
            return new ConnectedPath(PathKind.Relay, _udp, _serverUdp);

        var tcp = await JoinTcpRelay(peer.ConnectId, Role.Viewer, _timings.RelayWait, cancel).ConfigureAwait(false);
        return new ConnectedPath(PathKind.TcpRelay, tcp, tcp.Peer);
    }

    /// <summary>
    /// Remote PC: joins the relay of <paramref name="connectId"/> when the viewer asks for it after this PC already
    /// took a direct path (TRD › Direct handshake check). UDP relay, else TCP 443.
    /// </summary>
    public async Task<ConnectedPath> JoinRelayAsHostAsync(byte[] connectId, CancellationToken cancel = default)
    {
        if (UdpWorks && await Task.Run(() => JoinUdpRelay(connectId, Role.Host, cancel), cancel).ConfigureAwait(false))
            return new ConnectedPath(PathKind.Relay, _udp, _serverUdp);
        var tcp = await JoinTcpRelay(connectId, Role.Host, _timings.RelayWait, cancel).ConfigureAwait(false);
        return new ConnectedPath(PathKind.TcpRelay, tcp, tcp.Peer);
    }

    /// <summary>Takes a relay request for <paramref name="connectId"/> off the list; true when there was one.</summary>
    internal bool TakeRelayRequest(byte[] connectId) => _relayAsked.TryRemove(Convert.ToHexString(connectId), out _);

    internal void AddRelayRequest(byte[] connectId) => _relayAsked[Convert.ToHexString(connectId)] = true;

    /// <summary>
    /// Remote PC, on Incoming: punches back, joins the relay if the viewer asks, and returns the path the viewer's
    /// Hello came in on (the Hello is kept for the session). Throws <see cref="TimeoutException"/> when no Hello came.
    /// </summary>
    public async Task<ConnectedPath> AcceptAsync(PeerInfo caller, CancellationToken cancel = default)
    {
        if (UdpWorks) return await Task.Run(() => Accept(caller, cancel), cancel).ConfigureAwait(false);

        // UDP blocked: nothing can come straight; wait for the viewer to ask for the relay, then join over TLS.
        var clock = Stopwatch.StartNew();
        string key = Convert.ToHexString(caller.ConnectId);
        while (!_relayAsked.TryRemove(key, out _))
        {
            if (clock.Elapsed > _timings.AcceptWait) throw new TimeoutException("The viewer never asked for the relay.");
            await Task.Delay(20, cancel).ConfigureAwait(false);
        }
        var tcp = await JoinTcpRelay(caller.ConnectId, Role.Host, _timings.AcceptWait - clock.Elapsed, cancel).ConfigureAwait(false);
        return new ConnectedPath(PathKind.TcpRelay, tcp, tcp.Peer);
    }

    /// <summary>
    /// Viewer: Punch to every address of the other PC; the first address that answers with Punch ack is the path, as
    /// that proves packets get through both ways. A Punch from the other PC is answered, and its address punched too.
    /// </summary>
    private IPEndPoint? Punch(PeerInfo peer, CancellationToken cancel)
    {
        var punch = RendezvousWire.Udp(UdpMessage.Punch, peer.ConnectId, Role.Viewer);
        var targets = Targets(peer);
        var buffer = new byte[PacketLimits.MaxDatagram + 1];
        var clock = Stopwatch.StartNew();
        var nextSend = TimeSpan.Zero;
        while (clock.Elapsed < _timings.PunchFor)
        {
            cancel.ThrowIfCancellationRequested();
            if (clock.Elapsed >= nextSend)
            {
                foreach (var target in targets) _udp.Send(punch, target);
                nextSend += _timings.PunchEvery;
            }
            if (!_udp.TryReceive(buffer, Until(clock, nextSend), out int length, out var from)) continue;
            var packet = buffer.AsSpan(0, length);
            if (RendezvousWire.TryReadUdp(packet, UdpMessage.Punch, true, out var id, out var role) && role == Role.Host
                && id.AsSpan().SequenceEqual(peer.ConnectId))
            {
                // Only proves the way back; keep punching, now also to where this came from.
                _udp.Send(RendezvousWire.Udp(UdpMessage.PunchAck, peer.ConnectId), from);
                if (!targets.Contains(from))
                {
                    targets.Add(from);
                    _udp.Send(punch, from);
                }
                continue;
            }
            if (RendezvousWire.TryReadUdp(packet, UdpMessage.PunchAck, false, out id, out _) && id.AsSpan().SequenceEqual(peer.ConnectId))
                return from;
        }
        return null;
    }

    /// <summary>Relay join to the server until it answers Relay ready (both PCs joined).</summary>
    private bool JoinUdpRelay(byte[] connectId, Role role, CancellationToken cancel)
    {
        var join = RendezvousWire.Udp(UdpMessage.RelayJoin, connectId, role);
        var buffer = new byte[PacketLimits.MaxDatagram + 1];
        var clock = Stopwatch.StartNew();
        var nextSend = TimeSpan.Zero;
        while (clock.Elapsed < _timings.RelayWait)
        {
            cancel.ThrowIfCancellationRequested();
            if (clock.Elapsed >= nextSend)
            {
                _udp.Send(join, _serverUdp);
                nextSend += _timings.PunchEvery;
            }
            if (_udp.TryReceive(buffer, Until(clock, nextSend), out int length, out var from) && from.Equals(_serverUdp)
                && RendezvousWire.TryReadUdp(buffer.AsSpan(0, length), UdpMessage.RelayReady, false, out var id, out _)
                && id.AsSpan().SequenceEqual(connectId))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Remote PC with working UDP: punches back for <see cref="ConnectTimings.PunchFor"/>, answers each Punch, joins
    /// the UDP relay when the viewer asks, and stops at the viewer's Hello: from the viewer itself, or from the relay.
    /// </summary>
    private ConnectedPath Accept(PeerInfo caller, CancellationToken cancel)
    {
        var punch = RendezvousWire.Udp(UdpMessage.Punch, caller.ConnectId, Role.Host);
        var join = RendezvousWire.Udp(UdpMessage.RelayJoin, caller.ConnectId, Role.Host);
        var targets = Targets(caller);
        string key = Convert.ToHexString(caller.ConnectId);
        var buffer = new byte[PacketLimits.MaxDatagram + 1];
        bool joining = false, joined = false;
        var clock = Stopwatch.StartNew();
        var nextSend = TimeSpan.Zero;
        while (clock.Elapsed < _timings.AcceptWait)
        {
            cancel.ThrowIfCancellationRequested();
            if (!joining && _relayAsked.TryRemove(key, out _))
            {
                joining = true;
                nextSend = clock.Elapsed;
            }
            if (clock.Elapsed >= nextSend)
            {
                if (clock.Elapsed < _timings.PunchFor)
                    foreach (var target in targets) _udp.Send(punch, target);
                if (joining && !joined) _udp.Send(join, _serverUdp);
                nextSend += _timings.PunchEvery;
            }
            if (!_udp.TryReceive(buffer, Until(clock, nextSend), out int length, out var from) || length == 0) continue;
            var packet = buffer.AsSpan(0, length);
            if (RendezvousWire.TryReadUdp(packet, UdpMessage.Punch, true, out var id, out var role))
            {
                if (role == Role.Viewer && id.AsSpan().SequenceEqual(caller.ConnectId))
                    _udp.Send(RendezvousWire.Udp(UdpMessage.PunchAck, caller.ConnectId), from);
            }
            else if (RendezvousWire.TryReadUdp(packet, UdpMessage.RelayReady, false, out id, out _))
            {
                joined |= from.Equals(_serverUdp) && id.AsSpan().SequenceEqual(caller.ConnectId);
            }
            else if (packet[0] == (byte)PacketKind.Hello)
            {
                var kind = from.Equals(_serverUdp) ? PathKind.Relay : PathKind.Direct;
                return new ConnectedPath(kind, new HeldPacketTransport(_udp, packet.ToArray(), from), from);
            }
        }
        throw new TimeoutException("No Hello came from the viewer.");
    }

    private async Task<TcpRelayTransport> JoinTcpRelay(byte[] connectId, Role role, TimeSpan wait, CancellationToken cancel)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        waiting.CancelAfter(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        try
        {
            return await _server.JoinTcpRelayAsync(connectId, role, waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException("The other PC never joined the relay.");
        }
    }

    /// <summary>The other PC's addresses this socket can reach (IPv4), each once.</summary>
    private static List<IPEndPoint> Targets(PeerInfo peer) =>
        [.. peer.Addresses.Select(a => a.EndPoint).Where(e => e.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Distinct()];

    private static TimeSpan Until(Stopwatch clock, TimeSpan next)
    {
        var wait = next - clock.Elapsed;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }
}
