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
        OnMtaThread(() => AssertEncodes(EncoderKind.HardwareGpu));

    [Fact]
    public void Hardware_encoder_with_memory_input_makes_an_H264_keyframe() =>
        OnMtaThread(() => AssertEncodes(EncoderKind.HardwareMemory));

    [Fact]
    public void Software_encoder_makes_an_H264_keyframe() =>
        OnMtaThread(() => AssertEncodes(EncoderKind.Software));

    /// <summary>
    /// Plan step 4 check: gives the hardware encoder more and more of the TRD settings, to find what it accepts.
    /// Fails, listing every result, unless the TRD settings (the last line) work.
    /// </summary>
    [Fact]
    public void Hardware_encoder_settings_check() => OnMtaThread(() =>
    {
        var offered = EncoderOptions.Bare with { OfferedTypes = true };
        var variants = new (string Name, EncoderOptions Options)[]
        {
            ("1 encoder's own types, nothing extra", offered),
            ("2 = 1 + no B-frames", offered with { NoBFrames = true }),
            ("3 = 2 + low latency", offered with { NoBFrames = true, LowLatency = true }),
            ("4 = 3 + constant bitrate", offered with { NoBFrames = true, LowLatency = true, ConstantBitrate = true }),
            ("5 = 4 + keyframe interval", offered with { NoBFrames = true, LowLatency = true, ConstantBitrate = true, KeyFrameInterval = true }),
            ("6 = 5 + colour marks", EncoderOptions.Default with { OfferedTypes = true }),
            ("7 our own types, nothing extra", EncoderOptions.Bare),
            ("8 = 7 + H.264 level 5.1", EncoderOptions.Bare with { Level = 51 }),
            ("9 our own types, TRD settings, 30 fps", EncoderOptions.Default),
            ("10 our own types, TRD settings + level 5.1", EncoderOptions.Default with { Level = 51 }),
            ("11 our own types, TRD settings", EncoderOptions.Default),
        };

        using var duplicator = OpenWithFirstFrame();
        var results = variants.Select(v =>
        {
            int fps = v.Name.EndsWith("30 fps") ? 30 : 60;
            return (v.Name, Result: TryEncode(duplicator, EncoderKind.HardwareMemory, v.Options, fps));
        }).ToList();
        string report = string.Join(Environment.NewLine, results.Select(r => $"{r.Name}: {r.Result.Text}"));
        Assert.True(results[^1].Result.Ok, Environment.NewLine + report);
    });

    private static void AssertEncodes(EncoderKind kind)
    {
        using var duplicator = OpenWithFirstFrame();
        var (ok, text) = TryEncode(duplicator, kind, EncoderOptions.Default);
        Assert.True(ok, text);
    }

    /// <summary>Opens the main monitor and waits for its first frame, which holds the whole desktop.</summary>
    private static DesktopDuplicator OpenWithFirstFrame()
    {
        var duplicator = DesktopDuplicator.OpenMainMonitor();
        var captured = CaptureResult.Still;
        for (int i = 0; i < 20 && captured.Status != CaptureStatus.NewFrame; i++)
            captured = duplicator.Capture(100);
        Assert.Equal(CaptureStatus.NewFrame, captured.Status);
        return duplicator;
    }

    /// <summary>Starts a fresh encoder and encodes the captured frame; says what happened.</summary>
    private static (bool Ok, string Text) TryEncode(DesktopDuplicator duplicator, EncoderKind kind, EncoderOptions options,
        int framesPerSecond = 60)
    {
        var settings = VideoSettings.ForScreen(duplicator.Width, duplicator.Height) with { FramesPerSecond = framesPerSecond };
        using var encoder = EncoderFactory.Create(duplicator.Device, duplicator.Context, settings, kind,
            duplicator.AdapterVendorId, out var failures, options);
        if (encoder is null) return (false, "did not start: " + string.Join("; ", failures));

        string applied = string.Join(", ", encoder.Settings);
        try
        {
            using var converter = new Nv12Converter(duplicator.Device, duplicator.Context, duplicator.Width,
                duplicator.Height, settings, encoder.InputBindFlags);
            var frames = encoder.Encode(converter.Convert(duplicator.Frame!), TimeSpan.Zero);

            // Low latency: the encoder answers this frame at once, and the first frame is a keyframe in Annex B form.
            if (frames.Count != 1) return (false, $"{frames.Count} frames back instead of 1 [{applied}]");
            var frame = frames[0];
            if (!frame.KeyFrame) return (false, $"first frame is not a keyframe [{applied}]");
            if (frame.Data is not ([0, 0, 1, ..] or [0, 0, 0, 1, ..])) return (false, $"output is not Annex B H.264 [{applied}]");
            return (true, $"OK, {frame.Data.Length / 1024} kB keyframe [{applied}]");
        }
        catch (Exception error)
        {
            return (false, $"{encoder.Details}: {EncoderFactory.Describe(error)} [{applied}]");
        }
    }

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
