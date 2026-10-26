using Sheron.Sim;

namespace Sheron.Tests.Sim;

public class SimulatedLinkTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void About_the_set_share_of_packets_is_lost()
    {
        var link = new SimulatedLink(new LinkSettings(Ms(10), LossRate: 0.05), seed: 1);
        for (int i = 0; i < 20_000; i++) link.Send(new byte[100], Ms(i));

        Assert.InRange(link.Lost / 20_000.0, 0.045, 0.055);
    }

    [Fact]
    public void Packets_arrive_after_the_delay_give_or_take_the_jitter()
    {
        var link = new SimulatedLink(new LinkSettings(Ms(300), Jitter: Ms(100)), seed: 2);
        var arrived = new List<byte[]>();
        link.Send(new byte[] { 1 }, Ms(0));

        link.Receive(Ms(199), arrived);
        Assert.Empty(arrived);
        link.Receive(Ms(400), arrived);
        Assert.Single(arrived);
    }

    [Fact]
    public void Jitter_lets_packets_overtake_each_other()
    {
        var link = new SimulatedLink(new LinkSettings(Ms(50), Jitter: Ms(20)), seed: 3);
        for (int i = 0; i < 100; i++) link.Send(new[] { (byte)i }, Ms(i));
        var arrived = new List<byte[]>();
        link.Receive(Ms(1000), arrived);

        Assert.Equal(100, arrived.Count);
        Assert.NotEqual(Enumerable.Range(0, 100).Select(i => (byte)i), arrived.Select(p => p[0]));
    }

    [Fact]
    public void Blocked_UDP_drops_everything()
    {
        var link = new SimulatedLink(new LinkSettings(Ms(10), UdpBlocked: true));
        link.Send(new byte[10], Ms(0));
        var arrived = new List<byte[]>();
        link.Receive(Ms(1000), arrived);

        Assert.Empty(arrived);
        Assert.Equal(1, link.Lost);
    }

    [Fact]
    public void Packets_over_1200_bytes_are_refused()
    {
        var link = new SimulatedLink(new LinkSettings(Ms(10)));
        link.Send(new byte[1200], Ms(0));
        Assert.Throws<ArgumentException>(() => link.Send(new byte[1201], Ms(0)));
    }

    [Fact]
    public void The_same_seed_gives_the_same_run()
    {
        static int LostWith(int seed)
        {
            var link = new SimulatedLink(new LinkSettings(Ms(10), LossRate: 0.3), seed);
            for (int i = 0; i < 1000; i++) link.Send(new byte[1], Ms(i));
            return link.Lost;
        }
        Assert.Equal(LostWith(9), LostWith(9));
    }
}
