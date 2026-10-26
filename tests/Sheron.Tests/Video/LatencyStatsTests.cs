using Sheron.Core.Video;

namespace Sheron.Tests.Video;

public class LatencyStatsTests
{
    /// <summary>A clock of 1 000 000 ticks a second: 1 000 ticks are 1 ms.</summary>
    private const long TicksPerSecond = 1_000_000;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Each_part_and_the_total_are_averaged_in_ms()
    {
        var stats = new LatencyStats(TicksPerSecond);
        // Encode 8 ms, decode 2 ms, draw 1 ms, wait for the screen 9 ms: 20 ms in all.
        stats.AddShown(Ms(0), new FrameTimes(0, 8_000, 10_000, 11_000, 20_000));
        stats.AddShown(Ms(16), new FrameTimes(100_000, 108_000, 110_000, 111_000, 130_000));

        var snapshot = stats.Snapshot(Ms(20));

        Assert.Equal(25.0, snapshot.AverageMs);
        Assert.Equal(30.0, snapshot.MaxMs);
        Assert.Equal(8.0, snapshot.EncodeMs);
        Assert.Equal(2.0, snapshot.DecodeMs);
        Assert.Equal(1.0, snapshot.DrawMs);
        Assert.Equal(14.0, snapshot.ScreenMs);
    }

    [Fact]
    public void Sixty_frames_a_second_for_five_seconds_read_as_60_fps()
    {
        var stats = new LatencyStats(TicksPerSecond);
        for (int i = 0; i < 300; i++)
            stats.AddShown(Ms(i * 1000.0 / 60), new FrameTimes(0, 1, 2, 3, 4));

        Assert.Equal(60, stats.Snapshot(Ms(4990)).FramesPerSecond);
    }

    [Fact]
    public void Frames_older_than_five_seconds_are_dropped()
    {
        var stats = new LatencyStats(TicksPerSecond);
        stats.AddShown(Ms(0), new FrameTimes(0, 0, 0, 0, 90_000));
        stats.AddUntimed(Ms(0));
        stats.AddShown(Ms(5500), new FrameTimes(0, 0, 0, 0, 10_000));

        var snapshot = stats.Snapshot(Ms(6000));

        Assert.Equal(10.0, snapshot.MaxMs);
        Assert.Equal(0, snapshot.Untimed);
    }

    [Fact]
    public void Untimed_frames_are_counted_even_with_none_shown()
    {
        var stats = new LatencyStats(TicksPerSecond);
        stats.AddUntimed(Ms(0));
        stats.AddUntimed(Ms(10));

        var snapshot = stats.Snapshot(Ms(20));

        Assert.Equal(2, snapshot.Untimed);
        Assert.Equal(0, snapshot.FramesPerSecond);
    }
}
