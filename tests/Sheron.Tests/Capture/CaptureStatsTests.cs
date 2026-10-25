using Sheron.Core.Capture;

namespace Sheron.Tests.Capture;

public class CaptureStatsTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Sixty_frames_in_one_second_read_as_60_fps()
    {
        var stats = new CaptureStats();
        for (int i = 0; i < 60; i++)
            stats.AddFrame(Ms(i * 1000.0 / 60), frameMs: 1.0, changedPercent: 10);

        var snapshot = stats.Snapshot(Ms(1000));

        Assert.Equal(60, snapshot.FramesPerSecond);
    }

    [Fact]
    public void Frames_older_than_one_second_are_left_out()
    {
        var stats = new CaptureStats();
        stats.AddFrame(Ms(0), 5.0, 100);
        stats.AddFrame(Ms(1500), 1.0, 20);

        var snapshot = stats.Snapshot(Ms(1600));

        Assert.Equal(1, snapshot.FramesPerSecond);
        Assert.Equal(1.0, snapshot.MaxFrameMs);
        Assert.Equal(20, snapshot.AverageChangedPercent);
    }

    [Fact]
    public void Average_and_max_frame_time_come_from_the_last_second()
    {
        var stats = new CaptureStats();
        stats.AddFrame(Ms(100), 1.0, 0);
        stats.AddFrame(Ms(200), 3.0, 0);

        var snapshot = stats.Snapshot(Ms(300));

        Assert.Equal(2.0, snapshot.AverageFrameMs);
        Assert.Equal(3.0, snapshot.MaxFrameMs);
    }

    [Fact]
    public void A_still_screen_reads_as_zero()
    {
        var stats = new CaptureStats();
        stats.AddFrame(Ms(0), 1.0, 5);

        Assert.Equal(default, stats.Snapshot(Ms(5000)));
    }
}
