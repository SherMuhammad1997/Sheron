using System.Net;
using Sheron.Core.Net;

namespace Sheron.Core.Rendezvous;

/// <summary>
/// The remote PC's side of connect by ID (Plan step 14, TRD › Connect by ID): on each Incoming it works out the path
/// with the <see cref="Connector"/> on the rendezvous half of the shared socket, while the session keeps running on
/// the other half (<see cref="PacketMux.Session"/>). A TCP relay path is joined to that same session. A relay the
/// viewer asks for after this PC took a direct path is joined too (TRD › Direct handshake check). It also keeps the
/// server's view of this PC's UDP address fresh. One viewer's path at a time.
/// This PC can also view another on the same socket and registration (<see cref="ConnectAsync"/>; Plan step 15a): while
/// <see cref="Viewing"/> is set, Incoming is ignored (TRD › Connect by ID › One session at a time).
/// </summary>
public sealed class HostRendezvous : IDisposable
{
    public static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(25);

    private readonly RendezvousClient _client;
    private readonly PacketMux _mux;
    private readonly IPEndPoint _serverUdp;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Timer _keepAlive;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PeerInfo> _callers = new();
    private TcpRelayTransport? _tcp;
    private Thread? _tcpPump;
    private volatile string? _accepting;
    private volatile bool _viewing;

    public HostRendezvous(RendezvousClient client, PacketMux mux, IPEndPoint serverUdp, ConnectTimings? timings = null)
    {
        _client = client;
        _mux = mux;
        _serverUdp = serverUdp;
        Connector = new Connector(client, mux.Rendezvous, serverUdp, timings);
        _client.Incoming += OnIncoming;
        _client.RelayAsked += OnRelayAsked;
        _keepAlive = new Timer(_ => KeepAlive(), null, KeepAliveEvery, KeepAliveEvery);
    }

    /// <summary>This PC's public UDP address and whether UDP works, from the address check.</summary>
    public Connector Connector { get; }

    /// <summary>A viewer's path was found: the viewer, how, and where its packets come from.</summary>
    public event Action<PeerInfo, PathKind, IPEndPoint>? PathFound;

    /// <summary>No path to that viewer (it never sent a Hello, or the relay failed).</summary>
    public event Action<PeerInfo, Exception>? PathFailed;

    /// <summary>
    /// This PC is viewing another: Incoming is ignored, so no other path touches the socket. Set by the caller before
    /// <see cref="ConnectAsync"/>; cleared when the session ends, which also sends the socket's packets back to this
    /// PC's own session.
    /// </summary>
    public bool Viewing
    {
        get => _viewing;
        set
        {
            _viewing = value;
            if (!value) _mux.CloseViewer();
        }
    }

    /// <summary>
    /// This PC as viewer, after Found: the path to the other PC on the shared socket (direct, relay or TCP 443). A UDP
    /// path comes with its own queue from <see cref="PacketMux.OpenViewer"/>. Set <see cref="Viewing"/> first.
    /// </summary>
    public Task<ConnectedPath> ConnectAsync(PeerInfo peer, CancellationToken cancel = default) =>
        AsViewer(token => Connector.ConnectAsync(peer, token), cancel);

    /// <summary>This PC as viewer: the relay without punching, after a direct path did not finish its handshake.</summary>
    public Task<ConnectedPath> RelayAsync(PeerInfo peer, CancellationToken cancel = default) =>
        AsViewer(token => Connector.RelayAsync(peer, token), cancel);

    private async Task<ConnectedPath> AsViewer(Func<CancellationToken, Task<ConnectedPath>> find, CancellationToken cancel)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _closing.Token);
        await _oneAtATime.WaitAsync(both.Token).ConfigureAwait(false);
        try
        {
            _mux.Rendezvous.Clear();
            var path = await find(both.Token).ConfigureAwait(false);
            return path.Transport == _mux.Rendezvous ? path with { Transport = _mux.OpenViewer(path.Remote) } : path;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public async Task<bool> CheckAddressAsync(CancellationToken cancel = default)
    {
        await _oneAtATime.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            _mux.Rendezvous.Clear();
            return await Connector.CheckAddressAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private void OnIncoming(PeerInfo caller)
    {
        if (_viewing) return;
        _callers[Convert.ToHexString(caller.ConnectId)] = caller;
        _ = AcceptAsync(caller);
    }

    /// <summary>The viewer asks for the relay. While its path is being worked out, the connector takes it; after, join here.</summary>
    private void OnRelayAsked(byte[] connectId)
    {
        string key = Convert.ToHexString(connectId);
        if (_viewing) return;
        if (_accepting != key && _callers.TryGetValue(key, out var caller)) _ = JoinLateAsync(caller);
    }

    private async Task JoinLateAsync(PeerInfo caller)
    {
        try
        {
            await _oneAtATime.WaitAsync(_closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            // The connector may have taken the request meanwhile, while working out a new path for this viewer.
            if (!Connector.TakeRelayRequest(caller.ConnectId)) return;
            _mux.Rendezvous.Clear();
            var path = await Connector.JoinRelayAsHostAsync(caller.ConnectId, _closing.Token).ConfigureAwait(false);
            if (path.Transport is TcpRelayTransport tcp) UseTcpRelay(tcp);
            PathFound?.Invoke(caller, path.Kind, path.Remote);
        }
        catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_closing.IsCancellationRequested) PathFailed?.Invoke(caller, error);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task AcceptAsync(PeerInfo caller)
    {
        try
        {
            await _oneAtATime.WaitAsync(_closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            // Old answers and Hellos say nothing about this viewer.
            _mux.Rendezvous.Clear();
            _accepting = Convert.ToHexString(caller.ConnectId);
            _mux.CopyHelloToRendezvous = true;
            var path = await Connector.AcceptAsync(caller, _closing.Token).ConfigureAwait(false);
            if (path.Transport is TcpRelayTransport tcp) UseTcpRelay(tcp);
            PathFound?.Invoke(caller, path.Kind, path.Remote);
        }
        catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_closing.IsCancellationRequested) PathFailed?.Invoke(caller, error);
        }
        finally
        {
            _mux.CopyHelloToRendezvous = false;
            _accepting = null;
            _oneAtATime.Release();
            // A relay asked for while the path was worked out but not taken by it is joined now.
            if (Connector.TakeRelayRequest(caller.ConnectId)) OnRelayAskedAgain(caller);
        }
    }

    private void OnRelayAskedAgain(PeerInfo caller)
    {
        // Put the request back for JoinLateAsync, which takes it under the one-at-a-time lock.
        Connector.AddRelayRequest(caller.ConnectId);
        _ = JoinLateAsync(caller);
    }

    /// <summary>The TCP relay joins the session: its packets come in beside the UDP ones; packets to it go over TLS.</summary>
    private void UseTcpRelay(TcpRelayTransport tcp)
    {
        var socket = _mux.Socket;
        var session = _mux.Session;
        Interlocked.Exchange(ref _tcp, tcp);
        session.SendVia((packet, to) =>
        {
            if (to.Equals(tcp.Peer)) tcp.Send(packet, to);
            else socket.Send(packet, to);
        });
        _tcpPump = new Thread(() =>
        {
            var buffer = new byte[PacketLimits.MaxDatagram + 1];
            try
            {
                while (!_closing.IsCancellationRequested && Volatile.Read(ref _tcp) == tcp)
                    if (tcp.TryReceive(buffer, TimeSpan.FromMilliseconds(100), out int length, out var from))
                        session.Add(buffer[..length], from);
            }
            catch (ObjectDisposedException)
            {
                // A newer relay replaced this one, or the window closed.
            }
        })
        { IsBackground = true, Name = "Sheron TCP relay pump" };
        _tcpPump.Start();
    }

    /// <summary>An address check now and then keeps this PC's router mapping and the server's view of it fresh.</summary>
    private void KeepAlive()
    {
        if (!Connector.UdpWorks || !_client.IsConnected || _closing.IsCancellationRequested) return;
        _mux.Rendezvous.Send(RendezvousWire.Udp(UdpMessage.AddressCheck, _client.UdpToken), _serverUdp);
    }

    public void Dispose()
    {
        _closing.Cancel();
        _client.Incoming -= OnIncoming;
        _client.RelayAsked -= OnRelayAsked;
        _keepAlive.Dispose();
        Interlocked.Exchange(ref _tcp, null)?.Dispose();
        _tcpPump?.Join(TimeSpan.FromSeconds(1));
    }
}
