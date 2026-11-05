using System.Collections.Concurrent;
using System.Net;

namespace Sheron.Core.Net;

/// <summary>Sends one packet somewhere: the way a <see cref="PacketQueue"/> sends.</summary>
public delegate void PacketSender(ReadOnlySpan<byte> packet, IPEndPoint to);

/// <summary>
/// A packet transport fed by someone else (a <see cref="PacketMux"/> or a relay pump) and sending through a sender
/// that can be changed while it runs. Holds at most <see cref="Capacity"/> packets; more are dropped, as a full
/// network would.
/// </summary>
public sealed class PacketQueue : IPacketTransport
{
    public const int Capacity = 8192;

    private readonly BlockingCollection<(byte[] Packet, IPEndPoint From)> _inbox = new(Capacity);
    private volatile PacketSender _send;
    private volatile bool _closed;

    public PacketQueue(IPEndPoint localEndPoint, PacketSender send)
    {
        LocalEndPoint = localEndPoint;
        _send = send;
    }

    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Where sent packets go from now on.</summary>
    public void SendVia(PacketSender send) => _send = send;

    public void Add(byte[] packet, IPEndPoint from)
    {
        if (!_closed) _inbox.TryAdd((packet, from));
    }

    public bool TryReceive(byte[] buffer, TimeSpan timeout, out int length, out IPEndPoint from)
    {
        length = 0;
        from = LocalEndPoint;
        if (_closed) throw new ObjectDisposedException(nameof(PacketQueue));
        if (!_inbox.TryTake(out var item, timeout)) return false;
        item.Packet.CopyTo(buffer, 0);
        length = item.Packet.Length;
        from = item.From;
        return true;
    }

    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to) => _send(packet, to);

    /// <summary>Throws away what is waiting.</summary>
    public void Clear()
    {
        while (_inbox.TryTake(out _)) { }
    }

    internal void Close() => _closed = true;
}

/// <summary>
/// One UDP socket shared by the session and the connector (TRD › Connect by ID › One socket, two readers): a thread
/// reads the socket and hands rendezvous messages (0x30–0x35) to <see cref="Rendezvous"/> and everything else to
/// <see cref="Session"/>; while <see cref="CopyHelloToRendezvous"/> is set, each Hello also goes to the connector.
/// While this PC views another (<see cref="OpenViewer"/>), packets from that PC's address go to the viewer's queue
/// instead (TRD › Connect by ID › Viewer on the shared socket). All queues send straight out of the socket. Disposing
/// stops the thread; the socket stays its owner's.
/// </summary>
public sealed class PacketMux : IDisposable
{
    private readonly IPacketTransport _socket;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile ViewerRoute? _viewer;

    public PacketMux(IPacketTransport socket)
    {
        _socket = socket;
        Session = new PacketQueue(socket.LocalEndPoint, socket.Send);
        Rendezvous = new PacketQueue(socket.LocalEndPoint, socket.Send);
        _thread = new Thread(Run) { IsBackground = true, Name = "Sheron packet mux" };
        _thread.Start();
    }

    /// <summary>The socket underneath: where both queues send by default.</summary>
    public IPacketTransport Socket => _socket;

    public PacketQueue Session { get; }

    public PacketQueue Rendezvous { get; }

    public bool CopyHelloToRendezvous { get; set; }

    /// <summary>
    /// A new queue for this PC's own viewer session: from now on, non-rendezvous packets from <paramref name="remote"/>
    /// (the other PC, or the relay) go there and not to <see cref="Session"/>. Replaces an earlier viewer queue, which
    /// then gets nothing more; a queue is never closed under its link.
    /// </summary>
    public PacketQueue OpenViewer(IPEndPoint remote)
    {
        var queue = new PacketQueue(_socket.LocalEndPoint, _socket.Send);
        _viewer = new ViewerRoute(remote, queue);
        return queue;
    }

    /// <summary>The viewer session ended: everything goes to <see cref="Session"/> again.</summary>
    public void CloseViewer() => _viewer = null;

    /// <summary>Where the viewer's packets come from; null when this PC views nobody.</summary>
    public IPEndPoint? ViewerRemote => _viewer?.Remote;

    /// <summary>A rendezvous message: address check, address seen, relay join and ready, punch and punch ack.</summary>
    public static bool IsRendezvous(byte first) => first is >= 0x30 and <= 0x35;

    private void Run()
    {
        var buffer = new byte[PacketLimits.MaxDatagram + 1];
        while (!_stopping)
        {
            int length;
            IPEndPoint from;
            try
            {
                if (!_socket.TryReceive(buffer, TimeSpan.FromMilliseconds(100), out length, out from) || length == 0) continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            var packet = buffer[..length];
            if (IsRendezvous(packet[0]))
            {
                Rendezvous.Add(packet, from);
                continue;
            }
            if (_viewer is { } viewer && from.Equals(viewer.Remote))
            {
                viewer.Queue.Add(packet, from);
                continue;
            }
            Session.Add(packet, from);
            if (packet[0] == (byte)PacketKind.Hello && CopyHelloToRendezvous) Rendezvous.Add(packet, from);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(1));
        Session.Close();
        Rendezvous.Close();
    }

    private sealed record ViewerRoute(IPEndPoint Remote, PacketQueue Queue);
}
