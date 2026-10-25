using Sheron.Core.Video;

namespace Sheron.Tests.Video;

public class VideoSettingsTests
{
    [Fact]
    public void Odd_screen_sizes_are_cut_to_even()
    {
        var settings = VideoSettings.ForScreen(1367, 769);

        Assert.Equal(1366, settings.Width);
        Assert.Equal(768, settings.Height);
    }

    [Fact]
    public void Defaults_match_the_TRD()
    {
        var settings = VideoSettings.ForScreen(1920, 1200);

        Assert.Equal((1920, 1200), (settings.Width, settings.Height));
        Assert.Equal(60, settings.FramesPerSecond);
        Assert.Equal(8_000_000, settings.BitsPerSecond);
        Assert.Equal(300, settings.KeyFrameInterval);
    }
}
