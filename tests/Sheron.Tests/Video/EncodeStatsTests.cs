using Sheron.Core.Video;

namespace Sheron.Tests.Video;

public class EncodeStatsTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Sixty_frames_of_16_kB_in_one_second_are_7680_kbps()
    {
        var stats = new EncodeStats();
        for (int i = 0; i < 60; i++)
            stats.AddFrame(Ms(i * 1000.0 / 60), encodeMs: 2.0, bytes: 16_000, keyFrame: i == 0);

        var snapshot = stats.Snapshot(Ms(1000));

        Assert.Equal(60, snapshot.FramesPerSecond);
        Assert.Equal(60 * 16_000 * 8 / 1000, snapshot.KilobitsPerSecond);
        Assert.Equal(1, snapshot.KeyFrames);
    }

    [Fact]
    public void Average_and_max_encode_time_come_from_the_last_second()
    {
        var stats = new EncodeStats();
        stats.AddFrame(Ms(0), 9.0, 100, false);
        stats.AddFrame(Ms(1500), 2.0, 100, false);
        stats.AddFrame(Ms(1600), 4.0, 100, false);

        var snapshot = stats.Snapshot(Ms(1700));

        Assert.Equal(3.0, snapshot.AverageEncodeMs);
        Assert.Equal(4.0, snapshot.MaxEncodeMs);
    }

    [Fact]
    public void No_frames_reads_as_zero()
    {
        Assert.Equal(default, new EncodeStats().Snapshot(Ms(0)));
    }
}
