using Sheron.Core.Capture;
using Sheron.Core.Video;

namespace Sheron.Tests.Video;

public class LetterboxTests
{
    [Fact]
    public void A_wide_window_gets_bars_left_and_right()
    {
        // 1920 × 1200 is 16:10; 490 high makes it 784 wide.
        Assert.Equal(new ScreenRect(88, 0, 872, 490), Letterbox.Fit(1920, 1200, 960, 490));
    }

    [Fact]
    public void A_tall_window_gets_bars_top_and_bottom()
    {
        Assert.Equal(new ScreenRect(0, 210, 960, 750), Letterbox.Fit(1920, 1080, 960, 960));
    }

    [Fact]
    public void The_same_shape_fills_the_window()
    {
        Assert.Equal(new ScreenRect(0, 0, 960, 600), Letterbox.Fit(1920, 1200, 960, 600));
    }

    [Fact]
    public void An_empty_window_or_picture_gives_nothing()
    {
        Assert.Equal(default, Letterbox.Fit(1920, 1200, 0, 600));
        Assert.Equal(default, Letterbox.Fit(0, 0, 960, 600));
    }
}
