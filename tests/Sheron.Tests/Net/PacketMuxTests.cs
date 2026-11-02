using System.Net;
using Sheron.Core.Net;
using Sheron.Sim;

namespace Sheron.Tests.Net;

/// <summary>Plan step 14: one socket shared by the session and the connector (TRD › Connect by ID › One socket, two readers).</summary>
public class PacketMuxTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(500);

    /// <summary>Byte 0 of the next packet, or -1 when none came.</summary>
    private static int Next(PacketQueue queue)
    {
        var buffer = new byte[1500];
        return queue.TryReceive(buffer, Wait, out int length, out _) && length > 0 ? buffer[0] : -1;
    }

    [Fact]
    public void Rendezvous_messages_go_to_the_connector_and_the_rest_to_the_session()
    {
        var net = new SimNetwork();
        var lan = net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var a = net.Bind(lan, new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5000));
        var b = net.Bind(lan, new IPEndPoint(IPAddress.Parse("192.168.1.21"), 5000));
        using var mux = new PacketMux(b);

        foreach (byte kind in new byte[] { 0x10, 0x34, 0x20, 0x33, 0x22 }) a.Send([kind, 1, 2], b.LocalEndPoint);

        Assert.Equal(0x10, Next(mux.Session));
        Assert.Equal(0x20, Next(mux.Session));
        Assert.Equal(0x22, Next(mux.Session));
        Assert.Equal(0x34, Next(mux.Rendezvous));
        Assert.Equal(0x33, Next(mux.Rendezvous));
        Assert.Equal(-1, Next(mux.Rendezvous));

        // While a path is worked out, the connector also sees each Hello.
        mux.CopyHelloToRendezvous = true;
        a.Send([0x20, 9], b.LocalEndPoint);
        Assert.Equal(0x20, Next(mux.Session));
        Assert.Equal(0x20, Next(mux.Rendezvous));
    }

    [Fact]
    public void Both_halves_send_out_of_the_same_socket_and_the_session_can_be_routed_elsewhere()
    {
        var net = new SimNetwork();
        var lan = net.AddSite("home", IPAddress.Parse("203.0.113.5"));
        var a = net.Bind(lan, new IPEndPoint(IPAddress.Parse("192.168.1.20"), 5000));
        var b = net.Bind(lan, new IPEndPoint(IPAddress.Parse("192.168.1.21"), 5000));
        using var mux = new PacketMux(b);
        var buffer = new byte[1500];

        mux.Session.Send([1], a.LocalEndPoint);
        mux.Rendezvous.Send([0x34], a.LocalEndPoint);
        Assert.True(a.TryReceive(buffer, Wait, out _, out var from));
        Assert.Equal(b.LocalEndPoint, from);
        Assert.True(a.TryReceive(buffer, Wait, out _, out _));

        var elsewhere = new List<byte>();
        mux.Session.SendVia((packet, _) => elsewhere.Add(packet[0]));
        mux.Session.Send([7], a.LocalEndPoint);
        Assert.Equal([7], elsewhere);
        Assert.False(a.TryReceive(buffer, TimeSpan.FromMilliseconds(100), out _, out _));
    }
}
