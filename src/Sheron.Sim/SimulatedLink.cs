using Sheron.Core.Net;

namespace Sheron.Sim;

/// <summary>
/// How bad one direction of a link is (TRD › Packets › simulator). Each packet's delay varies evenly by up to
/// ± <paramref name="Jitter"/>, so packets can overtake each other.
/// </summary>
public sealed record LinkSettings(TimeSpan Delay, TimeSpan Jitter = default, double LossRate = 0, bool UdpBlocked = false);

/// <summary>
/// One direction of a network link on a virtual clock: packets go in with <see cref="Send"/> and come out of
/// <see cref="Receive"/> once their arrival time has come. Seeded, so a test gives the same result every run.
/// </summary>
public sealed class SimulatedLink(LinkSettings settings, int seed = 1)
{
    private readonly PriorityQueue<byte[], (TimeSpan At, long Order)> _inFlight = new();
    private readonly Random _random = new(seed);
    private long _order;

    public LinkSettings Settings { get; set; } = settings;
    public int Sent { get; private set; }
    public int Lost { get; private set; }
    public int Delivered { get; private set; }
    public int InFlight => _inFlight.Count;

    /// <summary>Puts a packet on the link at <paramref name="now"/>. Packets over 1,200 bytes are refused, as on the real link.</summary>
    public void Send(ReadOnlySpan<byte> packet, TimeSpan now)
    {
        if (packet.Length > PacketLimits.MaxDatagram)
            throw new ArgumentException($"A packet of {packet.Length} bytes is over the {PacketLimits.MaxDatagram}-byte limit.", nameof(packet));
        Sent++;
        if (Settings.UdpBlocked || _random.NextDouble() < Settings.LossRate)
        {
            Lost++;
            return;
        }
        var jitter = Settings.Jitter * (_random.NextDouble() * 2 - 1);
        var at = now + Settings.Delay + jitter;
        _inFlight.Enqueue(packet.ToArray(), (at < now ? now : at, _order++));
    }

    /// <summary>Adds the packets that have arrived by <paramref name="now"/> to <paramref name="arrived"/>, earliest first.</summary>
    public void Receive(TimeSpan now, List<byte[]> arrived)
    {
        while (_inFlight.TryPeek(out var packet, out var key) && key.At <= now)
        {
            _inFlight.Dequeue();
            arrived.Add(packet);
            Delivered++;
        }
    }
}
