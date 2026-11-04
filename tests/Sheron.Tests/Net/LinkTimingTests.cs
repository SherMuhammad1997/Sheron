using Sheron.Core.Net;

namespace Sheron.Tests.Net;

/// <summary>Plan step 10: queueing delay, jitter and the other PC's clock (TRD › Automatic quality).</summary>
public class LinkTimingTests
{
    [Fact]
    public void Jitter_alone_is_no_queueing_delay_but_a_link_filling_up_is()
    {
        var tracker = new DelayTracker();
        var random = new Random(4);
        long t = 0;
        for (int i = 0; i < 600; i++, t += 16_667)   // 10 s at 60 fps: 300 ms ± 100 ms
            tracker.Add(t, 300_000 + random.Next(-100_000, 100_000));
        Assert.InRange(tracker.QueueMs(t), 0, 15);
        Assert.InRange(tracker.JitterMs(t), 150, 200);
        Assert.True(tracker.KnowsJitter(t));

        for (int i = 0; i < 60; i++, t += 16_667)    // then everything 150 ms later: a queue building up
            tracker.Add(t, 450_000 + random.Next(-100_000, 100_000));
        Assert.InRange(tracker.QueueMs(t), 120, 170);
    }

    [Fact]
    public void The_reply_with_the_shortest_round_trip_gives_the_clock_offset()
    {
        var sync = new ClockSync();
        Assert.Null(sync.Offset);
        // The other clock is 5 s ahead; one way takes 20 ms, plus 0–60 ms extra on the way back.
        sync.Add(sentMicros: 1_000_000, remoteMicros: 6_020_000, receivedMicros: 1_100_000);   // 100 ms round trip
        sync.Add(sentMicros: 2_000_000, remoteMicros: 7_020_000, receivedMicros: 2_040_000);   // 40 ms: the best
        sync.Add(sentMicros: 3_000_000, remoteMicros: 8_020_000, receivedMicros: 3_070_000);
        Assert.Equal(5_000_000, sync.Offset);
    }
}
