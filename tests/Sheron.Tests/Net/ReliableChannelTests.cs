using System.Text;
using Sheron.Core.Net;

namespace Sheron.Tests.Net;

public class ReliableChannelTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Messages_arrive_once_and_in_order_even_when_packets_come_backwards()
    {
        var sender = new ReliableSender();
        var receiver = new ReliableReceiver();
        var packets = new List<byte[]>();
        for (int i = 0; i < 300; i++)
        {
            sender.Send(Text($"key {i}"));
            packets.AddRange(sender.Poll(Ms(i * 0.5)));   // one message per packet; all within the first resend time
        }

        var delivered = new List<byte[]>();
        foreach (var packet in packets.AsEnumerable().Reverse().Concat(packets)) Assert.True(receiver.OnPacket(packet, delivered));

        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"key {i}"), delivered.Select(Encoding.UTF8.GetString));
        Assert.Equal(300, receiver.Duplicates);
    }

    [Fact]
    public void A_long_message_is_cut_into_fragments_and_put_back_together()
    {
        var message = new byte[5_000];
        new Random(1).NextBytes(message);
        var sender = new ReliableSender();
        var receiver = new ReliableReceiver();

        sender.Send(message);
        var packets = sender.Poll(Ms(0));
        var delivered = new List<byte[]>();
        foreach (var packet in packets) receiver.OnPacket(packet, delivered);

        Assert.True(packets.Count >= 5);
        Assert.All(packets, p => Assert.True(p.Length <= PacketLimits.MaxPlain));
        Assert.Equal(message, Assert.Single(delivered));
    }

    [Fact]
    public void A_lost_packet_is_sent_again_after_the_resend_time_and_the_ack_clears_it()
    {
        var sender = new ReliableSender();
        var receiver = new ReliableReceiver();
        var delivered = new List<byte[]>();

        sender.Send(Text("lost"));
        sender.Poll(Ms(0));   // dropped on the way
        Assert.Empty(sender.Poll(Ms(100)));
        var again = sender.Poll(Ms(250));   // 250 ms: no round trip measured yet

        Assert.Single(again);
        receiver.OnPacket(again[0], delivered);
        sender.OnAck(receiver.Ack(), Ms(260));
        Assert.Equal("lost", Encoding.UTF8.GetString(Assert.Single(delivered)));
        Assert.Equal(0, sender.Unacked);
        Assert.Equal(1, sender.Resends);
    }

    [Fact]
    public void A_gap_the_ack_shows_is_sent_again_once_its_ack_is_overdue()
    {
        var sender = new ReliableSender();
        var receiver = new ReliableReceiver();
        var delivered = new List<byte[]>();

        // Measure a 40 ms round trip.
        sender.Send(Text("a"));
        receiver.OnPacket(sender.Poll(Ms(0))[0], delivered);
        sender.OnAck(receiver.Ack(), Ms(40));
        Assert.Equal(Ms(40), sender.RoundTrip);

        // "b" is lost, "c" arrives: the ack shows the gap.
        sender.Send(Text("b"));
        sender.Poll(Ms(100));
        sender.Send(Text("c"));
        receiver.OnPacket(sender.Poll(Ms(101))[0], delivered);
        sender.OnAck(receiver.Ack(), Ms(140));

        // Not yet 1¼ round trips (about 50 ms) after "b" went: it may only be late. After that: sent again, well
        // before the full resend time (about 100 ms).
        Assert.Empty(sender.Poll(Ms(141)));
        Assert.Single(sender.Poll(Ms(151)));
    }

    [Fact]
    public void The_ack_lists_what_arrived_ahead_of_a_gap()
    {
        var sender = new ReliableSender();
        var packets = new List<byte[]>();
        for (int i = 0; i < 4; i++)
        {
            sender.Send(Text($"m{i}"));
            packets.AddRange(sender.Poll(Ms(i)));
        }
        var receiver = new ReliableReceiver();
        var delivered = new List<byte[]>();

        receiver.OnPacket(packets[0], delivered);
        receiver.OnPacket(packets[2], delivered);
        receiver.OnPacket(packets[3], delivered);
        sender.OnAck(receiver.Ack(), Ms(10));

        Assert.Single(delivered);
        Assert.Equal(1, sender.Unacked);   // only m1 is still waiting
    }

    [Fact]
    public void Broken_packets_change_nothing()
    {
        var sender = new ReliableSender();
        sender.Send(Text("hello"));
        var packet = sender.Poll(Ms(0))[0];
        var receiver = new ReliableReceiver();
        var delivered = new List<byte[]>();

        Assert.False(receiver.OnPacket(packet.AsSpan(0, packet.Length - 1), delivered));
        Assert.False(receiver.OnPacket(new byte[] { 2, 0 }, delivered));
        Assert.False(receiver.OnPacket(new byte[] { 1, 1, 0 }, delivered));
        Assert.Empty(delivered);
        Assert.True(receiver.OnPacket(packet, delivered));
        Assert.Single(delivered);
    }
}
