using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Sheron.Core.Net;

namespace Sheron.Core.Rendezvous;

/// <summary>
/// The TCP 443 relay as a packet transport, for a PC whose UDP is blocked (TRD › Server › Direct link, relay and TCP
/// fallback): each packet goes to the server inside one Relay data message on the TLS link, and the server forwards
/// it to the other PC. Every packet comes from, and goes to, <see cref="Peer"/>: the server's IP with port 0, so it
/// is never taken for the server's UDP address (TRD › Connect by ID › TCP relay on the remote PC). Packets wait in a queue of
/// <see cref="QueueLength"/>; when the link cannot keep up, new ones are dropped, as a full network would.
/// </summary>
public sealed class TcpRelayTransport : IPacketTransport, IDisposable
{
    public const int QueueLength = 4096;

    private readonly RendezvousClient _client;
    private readonly BlockingCollection<byte[]> _inbox = new(QueueLength);
    private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(QueueLength) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _sending;
    private int _closed;

    internal TcpRelayTransport(RendezvousClient client)
    {
        _client = client;
        Peer = new IPEndPoint(client.ServerEndPoint.Address, 0);
        _sending = SendLoop();
    }

    /// <summary>The far end of every packet: the server, standing in for the other PC.</summary>
    public IPEndPoint Peer { get; }

    public IPEndPoint LocalEndPoint => Peer;

    internal void Arrive(byte[] packet) => _inbox.TryAdd(packet);

    public bool TryReceive(byte[] buffer, TimeSpan timeout, out int length, out IPEndPoint from)
    {
        length = 0;
        from = Peer;
        if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(TcpRelayTransport));
        // A broken server link just goes quiet, like a lost network: the session's own timeout notices.
        if (!_inbox.TryTake(out var packet, timeout)) return false;
        packet.CopyTo(buffer, 0);
        length = packet.Length;
        return true;
    }

    /// <summary>Queues one packet for the other PC; <paramref name="to"/> is ignored, as there is only the one.</summary>
    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (packet.Length > PacketLimits.MaxDatagram)
            throw new ArgumentException($"A packet of {packet.Length} bytes is over the {PacketLimits.MaxDatagram}-byte limit.", nameof(packet));
        if (Volatile.Read(ref _closed) != 0) return;
        _outbox.Writer.TryWrite(packet.ToArray());
    }

    private async Task SendLoop()
    {
        try
        {
            await foreach (var packet in _outbox.Reader.ReadAllAsync(_closing.Token).ConfigureAwait(false))
                await _client.SendRelayedAsync(packet, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The server link closed: nothing more can go; the session times out.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _client.Forget(this);
        _outbox.Writer.TryComplete();
        _closing.Cancel();
        _sending.Wait(TimeSpan.FromSeconds(1));
        _closing.Dispose();
    }
}
