using Sheron.Core.Session;
using Sheron.Core.Video;

namespace Sheron.Tests.Video;

/// <summary>Plan step 10: the automatic-quality rules (TRD › Automatic quality).</summary>
public class QualityControllerTests
{
    private static readonly LinkReport Calm = new(100, 100, 15, 0, 100_000, 5, 2);
    private static readonly LinkReport LostFrames = new(100, 90, 14, 1, 90_000, 5, 2);       // 1 of 15 frames: 6.7 %
    private static readonly LinkReport Queueing = new(100, 100, 15, 0, 100_000, 150, 20);
    private static readonly LinkReport PacketLossOnly = new(100, 95, 15, 0, 95_000, 5, 2);   // 5 % packets, repaired

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Two_bad_reports_step_down_bitrate_first_then_fps_then_size()
    {
        var q = new QualityController();
        Assert.Equal(new QualityLevel(8_000_000, 60, 100), q.Current);

        var seen = new List<QualityLevel>();
        int t = 0;
        for (int i = 0; i < 100; i++, t += 250)
            if (q.OnReport(i % 2 == 0 ? LostFrames : Queueing, Ms(t))) seen.Add(q.Current);

        Assert.Equal(QualityController.Ladder.Skip(1), seen);
        Assert.Equal(new QualityLevel(1_000_000, 15, 50), q.Current);
    }

    [Fact]
    public void Steps_down_at_most_once_a_second_and_one_bad_report_alone_does_nothing()
    {
        var q = new QualityController();
        Assert.False(q.OnReport(LostFrames, Ms(0)));
        Assert.False(q.OnReport(Calm, Ms(250)));
        Assert.False(q.OnReport(LostFrames, Ms(500)));
        Assert.True(q.OnReport(LostFrames, Ms(750)));
        Assert.Equal(1, q.Level);
        Assert.False(q.OnReport(LostFrames, Ms(1000)));
        Assert.False(q.OnReport(LostFrames, Ms(1250)));
        Assert.False(q.OnReport(LostFrames, Ms(1500)));
        Assert.True(q.OnReport(LostFrames, Ms(1750)));
        Assert.Equal(2, q.Level);
    }

    [Fact]
    public void Packet_loss_that_repair_covers_is_no_reason_to_step_down()
    {
        var q = new QualityController();
        for (int t = 0; t < 10_000; t += 250) q.OnReport(PacketLossOnly, Ms(t));
        Assert.Equal(0, q.Level);
    }

    [Fact]
    public void Two_calm_seconds_step_back_up_one_at_a_time()
    {
        var q = new QualityController();
        q.OnReport(Queueing, Ms(0));
        q.OnReport(Queueing, Ms(250));
        Assert.Equal(1, q.Level);
        q.OnReport(Queueing, Ms(1250));
        q.OnReport(Queueing, Ms(1500));
        Assert.Equal(2, q.Level);

        int t = 1750;
        for (int i = 0; i < 7; i++, t += 250) Assert.False(q.OnReport(Calm, Ms(t)));
        Assert.True(q.OnReport(Calm, Ms(t)));
        Assert.Equal(1, q.Level);
        for (int i = 0; i < 8; i++) q.OnReport(Calm, Ms(t += 250));
        Assert.Equal(0, q.Level);
    }

    [Fact]
    public void The_CPU_guard_caps_fps_then_size_and_lets_go_when_the_PC_is_quiet()
    {
        var q = new QualityController();
        Assert.False(q.OnCpu(25, TimeSpan.Zero));
        Assert.False(q.OnCpu(25, TimeSpan.FromSeconds(4)));
        Assert.True(q.OnCpu(25, TimeSpan.FromSeconds(5)));
        Assert.Equal(new QualityLevel(8_000_000, 30, 100), q.Current);
        Assert.True(q.OnCpu(25, TimeSpan.FromSeconds(10)));
        Assert.Equal(15, q.Current.FramesPerSecond);
        Assert.True(q.OnCpu(25, TimeSpan.FromSeconds(15)));
        Assert.Equal(75, q.Current.ScalePercent);

        q.OnCpu(3, TimeSpan.FromSeconds(16));
        Assert.True(q.OnCpu(3, TimeSpan.FromSeconds(26)));
        Assert.Equal(new QualityLevel(8_000_000, 15, 100), q.Current);
    }

    [Fact]
    public void A_level_gives_even_encoder_sizes()
    {
        var settings = new QualityLevel(1_500_000, 15, 75).For(1366, 767);
        Assert.Equal((1024, 574, 15, 1_500_000), (settings.Width, settings.Height, settings.FramesPerSecond, settings.BitsPerSecond));
    }
}
