using Sheron.Core.Video;
using Sheron.Win.Capture;
using Sheron.Win.Encode;

namespace Sheron.Win.Tests.Encode;

/// <summary>
/// Needs a real, unlocked Windows desktop: run from Sher's PC, not over Remote Desktop. One test per encoder route,
/// so a failure shows which route does not work on this PC.
/// </summary>
[Collection(DesktopDuplication.Collection)]
public class EncoderTests
{
    [Fact]
    public void Hardware_encoder_with_GPU_input_makes_an_H264_keyframe() =>
        OnMtaThread(() => EncodeFirstFrame(EncoderKind.HardwareGpu));

    [Fact]
    public void Hardware_encoder_with_memory_input_makes_an_H264_keyframe() =>
        OnMtaThread(() => EncodeFirstFrame(EncoderKind.HardwareMemory));

    [Fact]
    public void Software_encoder_makes_an_H264_keyframe() =>
        OnMtaThread(() => EncodeFirstFrame(EncoderKind.Software));

    private static void EncodeFirstFrame(EncoderKind kind)
    {
        using var duplicator = DesktopDuplicator.OpenMainMonitor();
        var settings = VideoSettings.ForScreen(duplicator.Width, duplicator.Height);
        using var encoder = EncoderFactory.Create(duplicator.Device, duplicator.Context, settings, kind,
            duplicator.AdapterVendorId, out var failures);
        Assert.True(encoder is not null, "No encoder started: " + string.Join("; ", failures));
        using var converter = new Nv12Converter(duplicator.Device, duplicator.Context, duplicator.Width, duplicator.Height,
            settings, encoder!.InputBindFlags);

        // The first frame after opening holds the current desktop image; allow a few waits for it.
        var captured = CaptureResult.Still;
        for (int i = 0; i < 20 && captured.Status != CaptureStatus.NewFrame; i++)
            captured = duplicator.Capture(100);
        Assert.Equal(CaptureStatus.NewFrame, captured.Status);

        IReadOnlyList<EncodedFrame> frames;
        try
        {
            frames = encoder.Encode(converter.Convert(duplicator.Frame!), TimeSpan.Zero);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"{encoder.Details}, texture bind flags 0x{(uint)converter.BindFlags:X}: {EncoderFactory.Describe(error)}", error);
        }

        // Low latency: the encoder answers this frame at once, and the first frame is a keyframe.
        var frame = Assert.Single(frames);
        Assert.True(frame.KeyFrame, $"{encoder.Details}: first frame is not a keyframe");
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
