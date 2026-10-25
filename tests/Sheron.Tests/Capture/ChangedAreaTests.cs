using Sheron.Core.Capture;

namespace Sheron.Tests.Capture;

public class ChangedAreaTests
{
    [Fact]
    public void A_quarter_of_the_screen_is_25_percent()
    {
        var changed = new[] { new ScreenRect(0, 0, 960, 540) };

        Assert.Equal(25.0, ChangedArea.Percent(changed, 1920, 1080));
    }

    [Fact]
    public void Parts_off_the_screen_do_not_count()
    {
        var changed = new[] { new ScreenRect(-100, -100, 100, 100) };

        // Only the 100 × 100 part inside a 200 × 100 screen counts.
        Assert.Equal(50.0, ChangedArea.Percent(changed, 200, 100));
    }

    [Fact]
    public void Overlapping_rectangles_never_go_above_100_percent()
    {
        var whole = new ScreenRect(0, 0, 1920, 1080);

        Assert.Equal(100.0, ChangedArea.Percent(new[] { whole, whole }, 1920, 1080));
    }

    [Fact]
    public void No_changes_is_zero()
    {
        Assert.Equal(0.0, ChangedArea.Percent(Array.Empty<ScreenRect>(), 1920, 1080));
    }

    [Fact]
    public void An_upside_down_rectangle_has_no_area()
    {
        Assert.Equal(0, new ScreenRect(10, 10, 5, 5).Area);
    }
}
