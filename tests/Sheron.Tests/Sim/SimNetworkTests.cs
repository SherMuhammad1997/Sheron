using System.Net;
using Sheron.Sim;

namespace Sheron.Tests.Sim;

/// <summary>The simulated routers behave like real ones, so the connection tests mean something (TRD › Network simulator).</summary>
public class SimNetworkTests
{
    private static IPEndPoint At(string address, int port) => new(IPAddress.Parse(address), port);
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(200);

    private static (byte[] Packet, IPEndPoint From)? Next(SimTransport transport)
    {
        var buffer = new byte[1500];
        return transport.TryReceive(buffer, Wait, out int length, out var from) ? (buffer[..length], from) : null;
    }

    [Fact]
    public void A_router_lets_in_only_addresses_its_host_has_sent_to_so_both_sides_must_punch()
    {
        var net = new SimNetwork();
        var home = net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var office = net.AddSite("office", IPAddress.Parse("198.51.100.7"));
        var a = net.Bind(home, At("192.168.1.20", 5000));
        var b = net.Bind(office, At("10.0.0.8", 6000));

        // A's first packet makes A's mapping (port 40000) but is dropped at B's router: B never sent to A.
        a.Send([1], At("198.51.100.7", 40000));
        Assert.Null(Next(b));
        // B sends to A's public mapping: A's router lets it in (A sent to B's address), and B's mapping is made.
        b.Send([2], At("203.0.113.5", 40000));
        var atA = Next(a);
        Assert.Equal(At("198.51.100.7", 40000), atA!.Value.From);
        // Now A gets through to B.
        a.Send([3], atA.Value.From);
        Assert.Equal(3, Next(b)!.Value.Packet[0]);
    }

    [Fact]
    public void Hosts_on_one_site_reach_each_other_by_LAN_address()
    {
        var net = new SimNetwork();
        var home = net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var a = net.Bind(home, At("192.168.1.20", 5000));
        var b = net.Bind(home, At("192.168.1.21", 5000));

        a.Send([7], At("192.168.1.21", 5000));

        Assert.Equal(At("192.168.1.20", 5000), Next(b)!.Value.From);
    }

    [Fact]
    public void A_symmetric_router_shows_each_destination_a_different_port()
    {
        var net = new SimNetwork();
        var internet = net.AddSite("internet", null);
        var server1 = net.Bind(internet, At("10.10.10.1", 443));
        var server2 = net.Bind(internet, At("10.10.10.2", 443));
        var mobile = net.AddSite("mobile", IPAddress.Parse("100.64.0.9"), RouterKind.Symmetric);
        var phone = net.Bind(mobile, At("172.20.10.2", 5000));

        phone.Send([1], server1.LocalEndPoint);
        phone.Send([1], server2.LocalEndPoint);

        Assert.NotEqual(Next(server1)!.Value.From.Port, Next(server2)!.Value.From.Port);
    }

    [Fact]
    public void Blocked_UDP_and_blocked_sites_drop_everything_between_them()
    {
        var net = new SimNetwork();
        var internet = net.AddSite("internet", null);
        var server = net.Bind(internet, At("10.10.10.1", 443));
        var office = net.AddSite("office", IPAddress.Parse("198.51.100.7")) ;
        var pc = net.Bind(office, At("10.0.0.8", 6000));

        office.UdpBlocked = true;
        pc.Send([1], server.LocalEndPoint);
        Assert.Null(Next(server));

        office.UdpBlocked = false;
        net.Block(office, internet);
        pc.Send([1], server.LocalEndPoint);
        Assert.Null(Next(server));
        Assert.Equal(2, net.Dropped);
    }
}
