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
    int Refused,
    double PacketLoss = 0,
    int QueueMs = 0,
    int JitterMs = 0,
    TimeSpan HoldTime = default,
    bool ClockKnown = false,
    LinkReport? LastReport = null,
    int GaveUp = 0);

/// <summary>
/// One side of a direct link between two PCs over UDP (TRD › Connect by IP): the session handshake with retries,
/// then sealed video packets (remote PC → viewer), reliable control messages both ways, and a ping every second.
/// One worker thread receives and keeps time; <see cref="SendFrame"/> and <see cref="SendControl"/> may be called
/// from any thread. Events are raised on the worker thread.
/// </summary>
public sealed class Link : IDisposable
{
    public const int DefaultPort = 47920;

    private const int PollMicroseconds = 5000;

    private readonly DeviceKey _device;
    private readonly bool _isHost;
    private readonly LinkTimings _timings;
    private readonly IPacketTransport _transport;
    private readonly bool _ownsTransport;
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
    private readonly List<AssembledFrame> _released = [];
    // Plan step 10 (TRD › Automatic quality): the viewer measures and reports; the remote PC answers clock probes.
    private DelayTracker _delays = new();
    private ClockSync _clockSync = new();
    private TimeSpan _lastReport, _lastProbe;
    private (ulong Highest, long Opened, int Frames, int Lost, long Bytes) _reportBase;
    private double _packetLoss;
    private readonly Queue<(uint Expected, uint Received)> _lossWindow = new();
    private LinkReport? _lastReportIn;
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

    private Link(DeviceKey device, bool isHost, IPacketTransport transport, bool ownsTransport, IPEndPoint? remote, LinkTimings timings)
    {
        _device = device;
        _isHost = isHost;
        _timings = timings;
        _transport = transport;
        _ownsTransport = ownsTransport;
        LocalEndPoint = transport.LocalEndPoint;
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

    /// <summary>The remote PC's side: waits for a viewer on its own UDP <paramref name="port"/> (0: any free port).</summary>
    public static Link Host(DeviceKey device, int port = DefaultPort, LinkTimings? timings = null) =>
        new(device, true, new UdpTransport(port), true, null, timings ?? LinkTimings.Default);

    /// <summary>The viewer's side: connects to the remote PC at <paramref name="remote"/> from its own UDP socket.</summary>
    public static Link Viewer(DeviceKey device, IPEndPoint remote, LinkTimings? timings = null) =>
        new(device, false, new UdpTransport(0), true, remote, timings ?? LinkTimings.Default);

    /// <summary>
    /// The remote PC's side on a path the connector found (direct, relay or TCP relay): the transport stays open
    /// after the link, for the next session.
    /// </summary>
    public static Link Host(DeviceKey device, IPacketTransport transport, LinkTimings? timings = null) =>
        new(device, true, transport, false, null, timings ?? LinkTimings.Default);

    /// <summary>The viewer's side on a path the connector found: <paramref name="remote"/> is the remote PC or the relay.</summary>
    public static Link Viewer(DeviceKey device, IPacketTransport transport, IPEndPoint remote, LinkTimings? timings = null) =>
        new(device, false, transport, false, remote, timings ?? LinkTimings.Default);

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>A session started: the remote PC starts capturing (Plan step 8).</summary>
    public event Action? Connected;

    /// <summary>The session ended: timed out, or replaced by a new viewer.</summary>
    public event Action? Disconnected;

    /// <summary>Viewer: a frame came in whole.</summary>
    public event Action<AssembledFrame>? FrameReceived;

    /// <summary>A control message other than Ping came in, in order. Clock and report messages stay inside the link.</summary>
    public event Action<byte[]>? ControlReceived;

    /// <summary>Remote PC: the viewer's Link report, every 250 ms (TRD › Automatic quality).</summary>
    public event Action<LinkReport>? ReportReceived;

    public LinkSnapshot Snapshot()
    {
        lock (_gate)
        {
            long now = Micros.Now();
            return new LinkSnapshot(_state, _peer, _session?.RemoteFingerprint, _packetsSent, _bytesSent, _packetsReceived,
                _bytesReceived, _framesSent, _framesReceived, _assembler.LostFrames, _assembler.RebuiltShards,
                _reliableOut.RoundTrip, _reliableOut.Resends, _refused + (_session?.Refused ?? 0),
                _packetLoss, _isHost ? _lastReportIn?.QueueMs ?? 0 : _delays.QueueMs(now),
                _isHost ? _lastReportIn?.JitterMs ?? 0 : _delays.JitterMs(now), _assembler.HoldTime,
                _clockSync.Offset is not null, _lastReportIn, _assembler.GaveUp);
        }
    }

    /// <summary>
    /// Remote PC: sends one encoded frame as sealed video packets, after 8 bytes of its capture time
    /// (<paramref name="capturedAt"/>, a Stopwatch timestamp; 0: now). Dropped when no viewer is connected.
    /// </summary>
    public void SendFrame(ReadOnlySpan<byte> frame, bool keyFrame, long capturedAt = 0)
    {
        lock (_gate)
        {
            if (_state != LinkState.Connected || _session is null || _peer is null) return;
            var payload = new byte[CaptureTimeSize + frame.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload,
                Micros.From(capturedAt != 0 ? capturedAt : Stopwatch.GetTimestamp()));
            frame.CopyTo(payload.AsSpan(CaptureTimeSize));
            foreach (var packet in _packetizer.Packetize(payload, keyFrame)) Send(_session.Seal(packet), _peer);
            _framesSent++;
        }
    }

    /// <summary>Bytes of capture time in front of each frame (TRD › Automatic quality › Delay figure).</summary>
    public const int CaptureTimeSize = 8;

    /// <summary>
    /// How long a viewer waits for the keyframe it asked for before asking again: one and a half round trips, at
    /// least 300 ms (TRD › Connect by IP › After a lost frame).
    /// </summary>
    public static TimeSpan AskKeyFrameAgainAfter(TimeSpan? roundTrip)
    {
        var floor = TimeSpan.FromMilliseconds(300);
        return roundTrip is { } rtt && rtt * 1.5 > floor ? rtt * 1.5 : floor;
    }

    /// <summary>The jitter buffer holds a frame at most this long for an earlier missing one.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromMilliseconds(250);

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
        var wait = TimeSpan.FromMicroseconds(PollMicroseconds);
        while (!_stopping)
        {
            try
            {
                if (_transport.TryReceive(buffer, wait, out int length, out var from))
                {
                    do Handle(buffer.AsSpan(0, length), from);
                    while (!_stopping && _transport.TryReceive(buffer, TimeSpan.Zero, out length, out from));
                }
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
                _assembler.Add(plain, Stopwatch.GetTimestamp(), _released);
                Deliver(raise);
                break;
            case PacketKind.Reliable:
                var messages = new List<byte[]>();
                if (!_reliableIn.OnPacket(plain, messages)) break;
                Send(_session.Seal(_reliableIn.Ack()), from);
                foreach (var message in messages)
                    if (!HandleInside(message, now, raise)) raise.Add(() => ControlReceived?.Invoke(message));
                break;
            case PacketKind.Ack:
                _reliableOut.OnAck(plain, now);
                FlushReliable(now);
                break;
        }
    }

    /// <summary>
    /// Viewer: hands on the frames the jitter buffer let go, without their capture time, and measures their delay
    /// (TRD › Automatic quality). Under <see cref="_gate"/>.
    /// </summary>
    private void Deliver(List<Action> raise)
    {
        foreach (var released in _released)
        {
            if (released.Data.Length < CaptureTimeSize) continue;
            long captured = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(released.Data);
            long completed = Micros.From(released.CompletedAt);
            _delays.Add(completed, completed - captured);
            long? local = _clockSync.Offset is { } offset ? Micros.ToTimestamp(captured - offset) : null;
            var frame = released with { Data = released.Data[CaptureTimeSize..], CapturedAt = local };
            _framesReceived++;
            raise.Add(() => FrameReceived?.Invoke(frame));
        }
        _released.Clear();
    }

    /// <summary>Clock probes and replies and Link reports are the link's own (TRD › Automatic quality). Under <see cref="_gate"/>.</summary>
    private bool HandleInside(byte[] message, TimeSpan now, List<Action> raise)
    {
        switch (ControlMessage.TypeOf(message))
        {
            case ControlType.Ping:
                return true;
            case ControlType.ClockProbe when _isHost && ControlMessage.TryReadClockProbe(message, out long viewerMicros):
                _reliableOut.Send(ControlMessage.ClockReply(viewerMicros, Micros.Now()));
                FlushReliable(now);
                return true;
            case ControlType.ClockReply when !_isHost && ControlMessage.TryReadClockReply(message, out long sent, out long host):
                _clockSync.Add(sent, host, Micros.Now());
                return true;
            case ControlType.LinkReport when _isHost && ControlMessage.TryReadReport(message, out var report):
                // Repair data follows the loss over the last 2 s of reports, never below 1 % (TRD › Packets). Summed,
                // not averaged: packets that overtake each other move between reports but are not lost.
                _lossWindow.Enqueue((report.PacketsExpected, report.PacketsReceived));
                while (_lossWindow.Count > 8) _lossWindow.Dequeue();
                long expected = _lossWindow.Sum(r => (long)r.Expected), received = _lossWindow.Sum(r => (long)r.Received);
                if (expected >= 20)
                {
                    _packetLoss = Math.Clamp(1 - (double)received / expected, 0, 1);
                    // Planned for the top of what the count can tell: the loss plus twice its uncertainty, so a count
                    // that comes out a little low does not leave blocks short of repair shards.
                    double margin = 2 * Math.Sqrt(_packetLoss * (1 - _packetLoss) / expected);
                    _packetizer.LossRate = Math.Max(FecPolicy.MinLossRate, _packetLoss + margin);
                }
                _packetizer.TargetBlockLoss = FecPolicy.TargetFor(_reliableOut.RoundTrip);
                _lastReportIn = report;
                raise.Add(() => ReportReceived?.Invoke(report));
                return true;
            case ControlType.ClockProbe or ControlType.ClockReply or ControlType.LinkReport:
                return true;   // the wrong way round, or malformed: not for the window either
            default:
                return false;
        }
    }

    /// <summary>Viewer, every 250 ms: the Link report, and the hold time it implies; every second, a clock probe.</summary>
    private void Report(TimeSpan now)
    {
        if (now - _lastProbe >= TimeSpan.FromSeconds(1))
        {
            _reliableOut.Send(ControlMessage.ClockProbe(Micros.Now()));
            _lastProbe = now;
        }
        if (now - _lastReport < TimeSpan.FromMilliseconds(250) || _session is null) return;
        _lastReport = now;
        long micros = Micros.Now();
        var b = _reportBase;
        var report = new LinkReport(
            (uint)Math.Min(uint.MaxValue, _session.HighestOpened - b.Highest),
            (uint)Math.Clamp(_session.Opened - b.Opened, 0, uint.MaxValue),
            _framesReceived - b.Frames, _assembler.LostFrames - b.Lost,
            (uint)Math.Clamp(_bytesReceived - b.Bytes, 0, uint.MaxValue),
            _delays.QueueMs(micros), _delays.JitterMs(micros));
        _reportBase = (_session.HighestOpened, _session.Opened, _framesReceived, _assembler.LostFrames, _bytesReceived);
        if (report.PacketLoss is { } loss) _packetLoss = loss;
        _lastReportIn = report;
        // Out-of-order frames wait at most three times the jitter, never over the cap (TRD › Jitter buffer); until enough
        // frames have come to measure it, the hold stays as it is (the longest, at the start).
        if (_delays.KnowsJitter(micros))
        {
            var hold = TimeSpan.FromMilliseconds(3.0 * report.JitterMs);
            _assembler.HoldTime = hold < MaxHold ? hold : MaxHold;
        }
        _reliableOut.Send(ControlMessage.Report(report));
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
                if (!_isHost)
                {
                    _assembler.Expire(Stopwatch.GetTimestamp(), _released);
                    Deliver(raise);
                    if (_heardSealed) Report(now);
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
        // Until the first report has measured the jitter, out-of-order frames get the longest hold.
        _assembler = new FrameAssembler { HoldTime = MaxHold };
        _released.Clear();
        _delays = new DelayTracker();
        _clockSync = new ClockSync();
        _packetLoss = 0;
        _lossWindow.Clear();
        _lastReportIn = null;
        _lastReport = _lastProbe = _clock.Elapsed - TimeSpan.FromSeconds(1);
        _reportBase = (0, 0, _framesReceived, 0, _bytesReceived);
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
            _transport.Send(packet, to);
            _packetsSent++;
            _bytesSent += packet.Length;
        }
        catch (ObjectDisposedException) when (_stopping)
        {
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (_ownsTransport && _transport is IDisposable owned) owned.Dispose();
        _thread.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            _session?.Dispose();
            _hostHandshake?.Dispose();
            _viewerHandshake?.Dispose();
        }
    }
}
