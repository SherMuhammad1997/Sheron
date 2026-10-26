using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sheron.Core.Net;
using Sheron.Core.Security;

namespace Sheron.Core.Session;

/// <summary>How often the link retries and pings, and when it gives up (TRD › Connect by IP).</summary>
public sealed record LinkTimings(TimeSpan Retry, TimeSpan Ping, TimeSpan Timeout)
{
    public static readonly LinkTimings Default = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
}

public enum LinkState
{
    /// <summary>The remote PC waits for a viewer.</summary>
    Waiting,
    /// <summary>The viewer is sending Hello and has no answer yet.</summary>
    Connecting,
    Connected,
    /// <summary>The viewer heard nothing for the timeout; the session is over.</summary>
    Lost,
}

public sealed record LinkSnapshot(
    LinkState State,
    IPEndPoint? Peer,
    byte[]? PeerFingerprint,
    long PacketsSent,
    long BytesSent,
    long PacketsReceived,
    long BytesReceived,
    int FramesSent,
    int FramesReceived,
    int LostFrames,
    int RebuiltShards,
    TimeSpan? RoundTrip,
    int Resends,
    int Refused);

/// <summary>
/// One side of a direct link between two PCs over UDP (TRD › Connect by IP): the session handshake with retries,
/// then sealed video packets (remote PC → viewer), reliable control messages both ways, and a ping every second.
/// One worker thread receives and keeps time; <see cref="SendFrame"/> and <see cref="SendControl"/> may be called
/// from any thread. Events are raised on the worker thread.
/// </summary>
public sealed class Link : IDisposable
{
    public const int DefaultPort = 47920;
    public const int SocketBufferBytes = 4 << 20;

    private const int PollMicroseconds = 5000;

    private readonly DeviceKey _device;
    private readonly bool _isHost;
    private readonly LinkTimings _timings;
    private readonly Socket _socket;
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private volatile bool _stopping;

    // Under _gate.
    private LinkState _state;
    private IPEndPoint? _peer;
    private SecureSession? _session;
    private ReliableSender _reliableOut = new();
    private ReliableReceiver _reliableIn = new();
    private VideoPacketizer _packetizer = new();
    private FrameAssembler _assembler = new();
    private TimeSpan _lastHeard;
    private TimeSpan _lastPing;
    private TimeSpan _lastHandshakeSent;
    private long _packetsSent, _bytesSent, _packetsReceived, _bytesReceived;
    private int _framesSent, _framesReceived, _refused;
    // Remote PC: the handshake under way.
    private HostHandshake? _hostHandshake;
    private IPEndPoint? _pendingViewer;
    private byte[]? _pendingHello;
    private byte[]? _pendingWelcome;
    // Viewer: its handshake, and whether the remote PC has started sending (then Finish arrived).
    private ViewerHandshake? _viewerHandshake;
    private byte[]? _hello;
    private byte[]? _finish;
    private bool _heardSealed;

    private Link(DeviceKey device, bool isHost, IPEndPoint bindTo, IPEndPoint? remote, LinkTimings timings)
    {
        _device = device;
        _isHost = isHost;
        _timings = timings;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = SocketBufferBytes,
            SendBufferSize = SocketBufferBytes,
        };
        _socket.Bind(bindTo);
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        if (isHost)
        {
            _state = LinkState.Waiting;
        }
        else
        {
            _state = LinkState.Connecting;
            _peer = remote;
            _viewerHandshake = new ViewerHandshake(device);
            _hello = _viewerHandshake.Hello();
            _lastHandshakeSent = TimeSpan.MinValue;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = isHost ? "Sheron link (remote PC)" : "Sheron link (viewer)" };
        _thread.Start();
    }

    /// <summary>The remote PC's side: waits for a viewer on <paramref name="port"/> (0: any free port).</summary>
    public static Link Host(DeviceKey device, int port = DefaultPort, LinkTimings? timings = null) =>
        new(device, true, new IPEndPoint(IPAddress.Any, port), null, timings ?? LinkTimings.Default);

    /// <summary>The viewer's side: connects to the remote PC at <paramref name="remote"/>.</summary>
    public static Link Viewer(DeviceKey device, IPEndPoint remote, LinkTimings? timings = null) =>
        new(device, false, new IPEndPoint(IPAddress.Any, 0), remote, timings ?? LinkTimings.Default);

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>A session started: the remote PC starts capturing (Plan step 8).</summary>
    public event Action? Connected;

    /// <summary>The session ended: timed out, or replaced by a new viewer.</summary>
    public event Action? Disconnected;

    /// <summary>Viewer: a frame came in whole.</summary>
    public event Action<AssembledFrame>? FrameReceived;

    /// <summary>A control message other than Ping came in, in order.</summary>
    public event Action<byte[]>? ControlReceived;

    public LinkSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new LinkSnapshot(_state, _peer, _session?.RemoteFingerprint, _packetsSent, _bytesSent, _packetsReceived,
                _bytesReceived, _framesSent, _framesReceived, _assembler.LostFrames, _assembler.RebuiltShards,
                _reliableOut.RoundTrip, _reliableOut.Resends, _refused + (_session?.Refused ?? 0));
        }
    }

    /// <summary>Remote PC: sends one encoded frame as sealed video packets. Dropped when no viewer is connected.</summary>
    public void SendFrame(ReadOnlySpan<byte> frame, bool keyFrame)
    {
        lock (_gate)
        {
            if (_state != LinkState.Connected || _session is null || _peer is null) return;
            foreach (var packet in _packetizer.Packetize(frame, keyFrame)) Send(_session.Seal(packet), _peer);
            _framesSent++;
        }
    }

    /// <summary>Sends a control message over the reliable channel. Dropped when not connected.</summary>
    public void SendControl(ReadOnlySpan<byte> message)
    {
        lock (_gate)
        {
            if (_state != LinkState.Connected) return;
            _reliableOut.Send(message);
            FlushReliable(_clock.Elapsed);
        }
    }

    private void Run()
    {
        var buffer = new byte[PacketLimits.MaxDatagram + 1];
        while (!_stopping)
        {
            try
            {
                if (_socket.Poll(PollMicroseconds, SelectMode.SelectRead))
                {
                    do
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int length = _socket.ReceiveFrom(buffer, ref from);
                        Handle(buffer.AsSpan(0, length), (IPEndPoint)from);
                    }
                    while (_socket.Available > 0);
                }
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Windows reports an ICMP "port unreachable" for an earlier packet as a reset; oversized packets are not
                // Sheron's. Neither stops the link.
            }
            catch (Exception error) when (_stopping && error is ObjectDisposedException or SocketException)
            {
                return;
            }
            if (_stopping) return;
            Tick();
        }
    }

    private void Handle(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        if (packet.Length == 0) return;
        var raise = new List<Action>();
        lock (_gate)
        {
            _packetsReceived++;
            _bytesReceived += packet.Length;
            switch ((PacketKind)packet[0])
            {
                case PacketKind.Hello when _isHost:
                    OnHello(packet, from);
                    break;
                case PacketKind.Finish when _isHost:
                    OnFinish(packet, from, raise);
                    break;
                case PacketKind.Welcome when !_isHost:
                    OnWelcome(packet, from, raise);
                    break;
                case PacketKind.Sealed:
                    OnSealed(packet, from, raise);
                    break;
                default:
                    _refused++;
                    break;
            }
        }
        foreach (var action in raise) action();
    }

    /// <summary>Remote PC: answers a Hello. The same Hello again gets the same Welcome (the first may have been lost).</summary>
    private void OnHello(ReadOnlySpan<byte> hello, IPEndPoint from)
    {
        if (_pendingWelcome is not null && from.Equals(_pendingViewer) && hello.SequenceEqual(_pendingHello!))
        {
            Send(_pendingWelcome, from);
            return;
        }
        var handshake = new HostHandshake(_device);
        if (!handshake.TryWelcome(hello, out var welcome))
        {
            handshake.Dispose();
            _refused++;
            return;
        }
        _hostHandshake?.Dispose();
        _hostHandshake = handshake;
        _pendingViewer = from;
        _pendingHello = hello.ToArray();
        _pendingWelcome = welcome;
        Send(welcome, from);
    }

    /// <summary>Remote PC: a good Finish starts the session; it replaces any session with an earlier viewer.</summary>
    private void OnFinish(ReadOnlySpan<byte> finish, IPEndPoint from, List<Action> raise)
    {
        if (_hostHandshake is null || !from.Equals(_pendingViewer) || !_hostHandshake.TryAccept(finish, out var session))
        {
            _refused++;
            return;
        }
        if (_state == LinkState.Connected) raise.Add(() => Disconnected?.Invoke());
        _hostHandshake.Dispose();
        _hostHandshake = null;
        _pendingHello = _pendingWelcome = null;
        _pendingViewer = null;
        StartSession(session, from);
        raise.Add(() => Connected?.Invoke());
    }

    /// <summary>Viewer: a good Welcome gives the session; Finish goes back until the remote PC starts sending.</summary>
    private void OnWelcome(ReadOnlySpan<byte> welcome, IPEndPoint from, List<Action> raise)
    {
        if (_state != LinkState.Connecting || _viewerHandshake is null || !from.Equals(_peer)
            || !_viewerHandshake.TryFinish(welcome, out var finish, out var session))
        {
            _refused++;
            return;
        }
        _viewerHandshake.Dispose();
        _viewerHandshake = null;
        _finish = finish;
        StartSession(session, from);
        Send(finish, from);
        _lastHandshakeSent = _clock.Elapsed;
        raise.Add(() => Connected?.Invoke());
    }

    private void OnSealed(ReadOnlySpan<byte> packet, IPEndPoint from, List<Action> raise)
    {
        if (_session is null || !from.Equals(_peer) || !_session.TryOpen(packet, out var plain) || plain.Length == 0) return;
        var now = _clock.Elapsed;
        _lastHeard = now;
        _heardSealed = true;
        switch ((PacketKind)plain[0])
        {
            case PacketKind.Video when !_isHost:
                if (_assembler.Add(plain) is { } frame)
                {
                    _framesReceived++;
                    raise.Add(() => FrameReceived?.Invoke(frame));
                }
                break;
            case PacketKind.Reliable:
                var messages = new List<byte[]>();
                if (!_reliableIn.OnPacket(plain, messages)) break;
                Send(_session.Seal(_reliableIn.Ack()), from);
                foreach (var message in messages)
                    if (ControlMessage.TypeOf(message) != ControlType.Ping) raise.Add(() => ControlReceived?.Invoke(message));
                break;
            case PacketKind.Ack:
                _reliableOut.OnAck(plain, now);
                FlushReliable(now);
                break;
        }
    }

    /// <summary>Retries the handshake, pings, resends, and ends a session that has gone quiet.</summary>
    private void Tick()
    {
        var raise = new List<Action>();
        lock (_gate)
        {
            var now = _clock.Elapsed;
            bool retry = _lastHandshakeSent == TimeSpan.MinValue || now - _lastHandshakeSent >= _timings.Retry;
            if (!_isHost && _state == LinkState.Connecting && retry && _peer is not null)
            {
                Send(_hello!, _peer);
                _lastHandshakeSent = now;
            }
            else if (!_isHost && _state == LinkState.Connected && !_heardSealed && retry && _peer is not null)
            {
                Send(_finish!, _peer);
                _lastHandshakeSent = now;
            }

            if (_state == LinkState.Connected)
            {
                if (now - _lastPing >= _timings.Ping)
                {
                    _reliableOut.Send(ControlMessage.Ping());
                    _lastPing = now;
                }
                FlushReliable(now);
                if (now - _lastHeard >= _timings.Timeout)
                {
                    EndSession(_isHost ? LinkState.Waiting : LinkState.Lost);
                    raise.Add(() => Disconnected?.Invoke());
                }
            }
        }
        foreach (var action in raise) action();
    }

    private void StartSession(SecureSession session, IPEndPoint peer)
    {
        _session?.Dispose();
        _session = session;
        _peer = peer;
        _reliableOut = new ReliableSender();
        _reliableIn = new ReliableReceiver();
        _packetizer = new VideoPacketizer();
        _assembler = new FrameAssembler();
        _heardSealed = false;
        _lastHeard = _clock.Elapsed;
        _lastPing = _clock.Elapsed - _timings.Ping;   // ping at once: it measures the round trip
        _state = LinkState.Connected;
    }

    private void EndSession(LinkState next)
    {
        _session?.Dispose();
        _session = null;
        if (_isHost) _peer = null;
        _state = next;
    }

    private void FlushReliable(TimeSpan now)
    {
        if (_session is null || _peer is null) return;
        foreach (var packet in _reliableOut.Poll(now)) Send(_session.Seal(packet), _peer);
    }

    private void Send(byte[] packet, IPEndPoint to)
    {
        try
        {
            _socket.SendTo(packet, to);
            _packetsSent++;
            _bytesSent += packet.Length;
        }
        catch (Exception error) when (error is SocketException || (error is ObjectDisposedException && _stopping))
        {
            // Network gone for a moment: the packet is lost like any other; resends and the timeout handle it.
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _socket.Dispose();
        _thread.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            _session?.Dispose();
            _hostHandshake?.Dispose();
            _viewerHandshake?.Dispose();
        }
    }
}
