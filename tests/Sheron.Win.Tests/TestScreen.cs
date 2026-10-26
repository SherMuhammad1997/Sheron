using Sheron.Core.Capture;
using Sheron.Win.Capture;

namespace Sheron.Win.Tests;

/// <summary>Helpers for the tests that open the screen (all in the <see cref="DesktopDuplication"/> collection).</summary>
internal static class TestScreen
{
    public static DesktopDuplicator OpenWithFirstFrame() => OpenWithFirstFrame(out _);

    /// <summary>Opens the main monitor and waits for its first frame, which holds the whole desktop.</summary>
    /// <param name="shape">The pointer shape, if Windows sent it with the first frames.</param>
    public static DesktopDuplicator OpenWithFirstFrame(out PointerShape? shape)
    {
        shape = null;
        var duplicator = DesktopDuplicator.OpenMainMonitor();
        var captured = CaptureResult.Still;
        for (int i = 0; i < 20 && captured.Status != CaptureStatus.NewFrame; i++)
        {
            captured = duplicator.Capture(100);
            shape = captured.Shape ?? shape;
        }
        Assert.Equal(CaptureStatus.NewFrame, captured.Status);
        return duplicator;
    }

    /// <summary>Media Foundation needs COM, which an MTA thread sets up when it starts.</summary>
    public static void OnMtaThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
