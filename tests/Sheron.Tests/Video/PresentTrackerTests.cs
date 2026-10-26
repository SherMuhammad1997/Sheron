using Sheron.Core.Video;

namespace Sheron.Tests.Video;

public class PresentTrackerTests
{
    /// <summary>A video frame captured at <paramref name="captured"/>, each later point 1 000 ticks on.</summary>
    private static FrameTimes Frame(long captured) => new(captured, captured + 1000, captured + 2000, captured + 3000);

    [Fact]
    public void A_present_reported_at_its_own_refresh_was_shown_at_that_refresh()
    {
        var tracker = new PresentTracker();
        tracker.Presented(5, Frame(10_000));
        var shown = new List<FrameTimes>();

        int untimed = tracker.Report(new ScreenReport(5, PresentRefreshCount: 100, SyncRefreshCount: 100, SyncTicks: 19_000), shown);

        Assert.Equal(0, untimed);
        Assert.Equal(19_000, Assert.Single(shown).Displayed);
    }

    [Fact]
    public void A_present_reported_one_refresh_late_is_moved_back_one_refresh()
    {
        var tracker = new PresentTracker();
        var shown = new List<FrameTimes>();
        // Two readings 1 667 ticks apart measure the refresh.
        tracker.Report(new ScreenReport(3, 98, 98, 10_000), shown);
        tracker.Report(new ScreenReport(3, 98, 99, 11_667), shown);
        tracker.Presented(4, Frame(8_000));

        tracker.Report(new ScreenReport(4, PresentRefreshCount: 99, SyncRefreshCount: 100, SyncTicks: 13_334), shown);

        Assert.Equal(11_667, Assert.Single(shown).Displayed);
    }

    [Fact]
    public void Older_frames_never_reported_are_untimed()
    {
        var tracker = new PresentTracker();
        tracker.Presented(1, Frame(1_000));
        tracker.Presented(2, Frame(2_000));
        tracker.Presented(3, Frame(3_000));
        var shown = new List<FrameTimes>();

        int untimed = tracker.Report(new ScreenReport(3, 50, 50, 9_000), shown);

        Assert.Equal(2, untimed);
        Assert.Equal(3_000, Assert.Single(shown).Captured);
    }

    [Fact]
    public void A_pointer_redraw_of_the_same_frame_counts_that_frame_once_from_its_first_present()
    {
        var tracker = new PresentTracker();
        tracker.Presented(1, Frame(1_000));
        tracker.Presented(2, Frame(1_000) with { Presented = 7_000 });
        var shown = new List<FrameTimes>();

        int untimed = tracker.Report(new ScreenReport(2, 50, 50, 9_000), shown);

        Assert.Equal(0, untimed);
        var frame = Assert.Single(shown);
        Assert.Equal(4_000, frame.Presented);
        Assert.Equal(9_000, frame.Displayed);
    }

    [Fact]
    public void Presents_not_yet_on_screen_keep_waiting()
    {
        var tracker = new PresentTracker();
        tracker.Presented(1, Frame(1_000));
        tracker.Presented(2, Frame(2_000));
        var shown = new List<FrameTimes>();

        tracker.Report(new ScreenReport(1, 50, 50, 9_000), shown);

        Assert.Single(shown);
        Assert.Equal(1, tracker.Waiting);
    }
}
