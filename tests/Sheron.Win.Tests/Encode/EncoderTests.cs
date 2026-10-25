using Sheron.Core.Video;
using Sheron.Win.Capture;
using Sheron.Win.Encode;

namespace Sheron.Win.Tests.Encode;

/// <summary>Needs a real, unlocked Windows desktop: run from Sher's PC, not over Remote Desktop.</summary>
public class EncoderTests
{
    [Fact]
    public void The_best_encoder_turns_the_first_frame_into_an_H264_keyframe() =>
        OnMtaThread(() => EncodeFirstFrame(allowHardware: true));

    [Fact]
    public void The_software_encoder_works_as_the_fallback() =>
        OnMtaThread(() => EncodeFirstFrame(allowHardware: false));

    private static void EncodeFirstFrame(bool allowHardware)
    {
        using var duplicator = DesktopDuplicator.OpenMainMonitor();
        var settings = VideoSettings.ForScreen(duplicator.Width, duplicator.Height);
        using var encoder = EncoderFactory.Create(duplicator.Device, duplicator.Context, settings, allowHardware, out var failures);
        Assert.True(encoder is not null, "No encoder started: " + string.Join("; ", failures));
        if (!allowHardware) Assert.False(encoder!.IsHardware);
        using var converter = new Nv12Converter(duplicator.Device, duplicator.Context, duplicator.Width, duplicator.Height, settings);

        // The first frame after opening holds the current desktop image; allow a few waits for it.
        var captured = CaptureResult.Still;
        for (int i = 0; i < 20 && captured.Status != CaptureStatus.NewFrame; i++)
            captured = duplicator.Capture(100);
        Assert.Equal(CaptureStatus.NewFrame, captured.Status);

        var frames = encoder!.Encode(converter.Convert(duplicator.Frame!), TimeSpan.Zero);

        // Low latency: the encoder answers this frame at once, and the first frame is a keyframe.
        var frame = Assert.Single(frames);
        Assert.True(frame.KeyFrame);
        Assert.True(frame.Data.Length > 100);
        Assert.True(StartsWithStartCode(frame.Data), "Output is not Annex B H.264");
    }

    private static bool StartsWithStartCode(byte[] data) =>
        data is [0, 0, 1, ..] or [0, 0, 0, 1, ..];

    /// <summary>Media Foundation needs COM, which an MTA thread sets up when it starts.</summary>
    private static void OnMtaThread(Action test)
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
