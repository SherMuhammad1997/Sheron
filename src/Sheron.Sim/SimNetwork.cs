using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Sheron.Core.Net;

namespace Sheron.Sim;

public enum RouterKind
{
    /// <summary>One public port per inside address, whatever the destination: punching passes.</summary>
    EndpointIndependent,
    /// <summary>A new public port for each destination: the port the server saw is useless to a peer, so punching fails when both PCs have one.</summary>
    Symmetric,
}

/// <summary>
/// A bad link between two sites (Plan step 10): each packet is lost with <paramref name="Loss"/> or delivered after
/// <paramref name="Delay"/> ± up to <paramref name="Jitter"/> (evenly spread, so packets can overtake each other).
/// </summary>
public sealed record LinkImpairment(TimeSpan Delay, TimeSpan Jitter, double Loss);

/// <summary>A place with its own network: hosts behind one router with a public IP, or hosts right on the internet.</summary>
public sealed class SimSite
{
    internal SimSite(string name, IPAddress? publicIp, RouterKind router)
    {
        Name = name;
        PublicIp = publicIp;
        Router = router;
    }

    public string Name { get; }

    /// <summary>The router's address on the internet; null for hosts that are on the internet themselves (a server).</summary>
    public IPAddress? PublicIp { get; }

    public RouterKind Router { get; }

    /// <summary>All UDP in and out of this site is dropped (only LAN traffic inside the site still works).</summary>
    public bool UdpBlocked { get; set; }

    internal int NextPort { get; set; } = 40000;
    internal List<Mapping> Mappings { get; } = [];

    internal sealed record Mapping(IPEndPoint Inside, IPEndPoint? Destination, int PublicPort)
    {
        public HashSet<IPAddress> Allowed { get; } = [];
    }

    public override string ToString() => Name;
}

/// <summary>
/// In-memory UDP between simulated hosts, in real time, for the connection tests (TRD › Server › Network simulator).
/// A router maps an inside address to a public port and lets a packet in only from addresses the inside host has
/// sent to (by IP); hosts at one site reach each other by LAN address only (no "hairpin" through their own router,
/// which many routers cannot do). Sites can be cut off from each other, or have UDP blocked. Thread-safe.
/// </summary>
public sealed class SimNetwork
{
    private readonly object _lock = new();
    private readonly List<SimTransport> _transports = [];
    private readonly HashSet<(SimSite, SimSite)> _blocked = [];
    private readonly Dictionary<(SimSite, SimSite), LinkImpairment> _impaired = [];
    private readonly PriorityQueue<(SimTransport Target, byte[] Packet, IPEndPoint From), long> _late = new();
    private readonly Random _random;
    private Thread? _deliverer;

    /// <param name="seed">Seed for the random loss and jitter of <see cref="Impair"/>.</param>
    public SimNetwork(int seed = 10)
    {
        _random = new Random(seed);
    }

    public int Delivered { get; private set; }
    public int Dropped { get; private set; }

    public SimSite AddSite(string name, IPAddress? publicIp, RouterKind router = RouterKind.EndpointIndependent) =>
        new(name, publicIp, router);

    /// <summary>A UDP endpoint on a host at <paramref name="site"/>: its LAN address there, or its internet address.</summary>
    public SimTransport Bind(SimSite site, IPEndPoint address)
    {
        var transport = new SimTransport(this, site, address);
        lock (_lock) _transports.Add(transport);
        return transport;
    }

    /// <summary>Makes UDP between the two sites (both ways; the same site twice for traffic inside it) slow and lossy, in real time.</summary>
    public void Impair(SimSite a, SimSite b, LinkImpairment impairment)
    {
        lock (_lock)
        {
            _impaired[(a, b)] = impairment;
            _impaired[(b, a)] = impairment;
            if (_deliverer is not null) return;
            _deliverer = new Thread(DeliverLate) { IsBackground = true, Name = "Sheron sim delay" };
            _deliverer.Start();
        }
    }

    /// <summary>Drops all UDP from <paramref name="from"/> to <paramref name="to"/>, but not back: a path that works one way only.</summary>
    public void BlockOneWay(SimSite from, SimSite to)
    {
        lock (_lock) _blocked.Add((from, to));
    }

    /// <summary>Drops all UDP between the two sites both ways, as a strict firewall would.</summary>
    public void Block(SimSite a, SimSite b)
    {
        lock (_lock)
        {
            _blocked.Add((a, b));
            _blocked.Add((b, a));
        }
    }

    internal void Send(SimTransport source, ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        SimTransport? target;
        IPEndPoint from;
        lock (_lock)
        {
            var site = source.Site;
            // On the same site: straight across the LAN.
            target = _transports.FirstOrDefault(t => t.Site == site && t.LocalEndPoint.Equals(to) && t != source);
            from = source.LocalEndPoint;
            if (target is null)
            {
                var (destination, inside) = Find(to);
                if (destination is null || destination == site || site.UdpBlocked || destination.UdpBlocked)
                {
                    Dropped++;
                    return;
                }
                // The packet leaves through its own router (which then lets answers from there in) before a block on
                // the way, or a firewall at the far end, drops it.
                from = Outbound(site, source.LocalEndPoint, to);
                if (_blocked.Contains((site, destination)))
                {
                    Dropped++;
                    return;
                }
                target = inside is null ? null : _transports.FirstOrDefault(t => t.Site == destination && t.LocalEndPoint.Equals(inside));
                if (target is null || (destination.PublicIp is not null && !Allowed(destination, to.Port, from.Address)))
                {
                    Dropped++;
                    return;
                }
            }
            if (target.Closed)
            {
                Dropped++;
                return;
            }
            if (_impaired.TryGetValue((site, target.Site), out var bad))
            {
                if (_random.NextDouble() < bad.Loss)
                {
                    Dropped++;
                    return;
                }
                double ms = bad.Delay.TotalMilliseconds + (2 * _random.NextDouble() - 1) * bad.Jitter.TotalMilliseconds;
                long due = Stopwatch.GetTimestamp() + (long)(Math.Max(0, ms) * Stopwatch.Frequency / 1000);
                _late.Enqueue((target, packet.ToArray(), from), due);
                Delivered++;
                Monitor.PulseAll(_lock);
                return;
            }
            Delivered++;
        }
        target.Arrive(packet.ToArray(), from);
    }

    /// <summary>Hands each delayed packet to its host when its time comes.</summary>
    private void DeliverLate()
    {
        while (true)
        {
            (SimTransport Target, byte[] Packet, IPEndPoint From) next;
            lock (_lock)
            {
                while (true)
                {
                    if (_late.TryPeek(out _, out long due))
                    {
                        long wait = due - Stopwatch.GetTimestamp();
                        if (wait <= 0) break;
                        Monitor.Wait(_lock, TimeSpan.FromSeconds(Math.Min(wait / (double)Stopwatch.Frequency, 0.05)));
                    }
                    else
                    {
                        Monitor.Wait(_lock, TimeSpan.FromMilliseconds(50));
                    }
                }
                next = _late.Dequeue();
            }
            if (!next.Target.Closed) next.Target.Arrive(next.Packet, next.From);
        }
    }

    /// <summary>The site an internet address belongs to, and the inside address it reaches (through the router's mapping).</summary>
    private (SimSite? Site, IPEndPoint? Inside) Find(IPEndPoint to)
    {
        foreach (var transport in _transports)
        {
            var site = transport.Site;
            if (site.PublicIp is null && transport.LocalEndPoint.Equals(to)) return (site, to);
            if (site.PublicIp is not null && site.PublicIp.Equals(to.Address))
            {
                var mapping = site.Mappings.FirstOrDefault(m => m.PublicPort == to.Port);
                return (site, mapping?.Inside);
            }
        }
        return (null, null);
    }

    /// <summary>The public address a packet leaves the site with; the router now lets the destination's address in.</summary>
    private static IPEndPoint Outbound(SimSite site, IPEndPoint inside, IPEndPoint to)
    {
        if (site.PublicIp is null) return inside;
        var mapping = site.Router == RouterKind.EndpointIndependent
            ? site.Mappings.FirstOrDefault(m => m.Inside.Equals(inside))
            : site.Mappings.FirstOrDefault(m => m.Inside.Equals(inside) && to.Equals(m.Destination));
        if (mapping is null)
        {
            mapping = new SimSite.Mapping(inside, site.Router == RouterKind.Symmetric ? to : null, site.NextPort++);
            site.Mappings.Add(mapping);
        }
        mapping.Allowed.Add(to.Address);
        return new IPEndPoint(site.PublicIp, mapping.PublicPort);
    }

    private static bool Allowed(SimSite site, int publicPort, IPAddress from) =>
        site.Mappings.FirstOrDefault(m => m.PublicPort == publicPort)?.Allowed.Contains(from) == true;
}

/// <summary>One simulated UDP endpoint.</summary>
public sealed class SimTransport : IPacketTransport, IDisposable
{
    private readonly SimNetwork _network;
    private readonly BlockingCollection<(byte[] Packet, IPEndPoint From)> _inbox = new();

    internal SimTransport(SimNetwork network, SimSite site, IPEndPoint address)
    {
        _network = network;
        Site = site;
        LocalEndPoint = address;
    }

    public SimSite Site { get; }
    public IPEndPoint LocalEndPoint { get; }
    internal bool Closed { get; private set; }

    internal void Arrive(byte[] packet, IPEndPoint from)
    {
        try
        {
            _inbox.Add((packet, from));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public bool TryReceive(byte[] buffer, TimeSpan timeout, out int length, out IPEndPoint from)
    {
        length = 0;
        from = LocalEndPoint;
        if (Closed) throw new ObjectDisposedException(nameof(SimTransport));
        if (!_inbox.TryTake(out var item, timeout)) return false;
        item.Packet.CopyTo(buffer, 0);
        length = item.Packet.Length;
        from = item.From;
        return true;
    }

    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (packet.Length > PacketLimits.MaxDatagram)
            throw new ArgumentException($"A packet of {packet.Length} bytes is over the {PacketLimits.MaxDatagram}-byte limit.", nameof(packet));
        _network.Send(this, packet, to);
    }

    public void Dispose()
    {
        Closed = true;
    }
}
