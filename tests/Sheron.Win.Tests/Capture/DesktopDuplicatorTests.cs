using Sheron.Win.Capture;

namespace Sheron.Win.Tests.Capture;

/// <summary>Needs a real, unlocked Windows desktop: run from Sher's PC, not over Remote Desktop.</summary>
[Collection(DesktopDuplication.Collection)]
public class DesktopDuplicatorTests
{
    [Fact]
    public void The_main_monitor_gives_a_first_frame_with_the_whole_desktop()
    {
        using var duplicator = DesktopDuplicator.OpenMainMonitor();
        Assert.True(duplicator.Width > 0 && duplicator.Height > 0);

        // The first frame after opening holds the current desktop image; allow a few waits for it.
        CaptureResult result = CaptureResult.Still;
        for (int i = 0; i < 20 && result.Status != CaptureStatus.NewFrame; i++)
            result = duplicator.Capture(100);

        Assert.Equal(CaptureStatus.NewFrame, result.Status);
        Assert.NotNull(duplicator.Frame);
        Assert.Equal((uint)duplicator.Width, duplicator.Frame!.Description.Width);
    }
}
