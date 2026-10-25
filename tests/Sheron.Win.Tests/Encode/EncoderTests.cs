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
    /// Plan step 4 check: drives the hardware encoder in different ways, to find what it accepts. Earlier runs
    /// showed Intel Quick Sync fails with E_UNEXPECTED when output is collected in low-latency mode.
    /// Fails, listing every result, unless the TRD settings (the last line) work.
    /// </summary>
    [Fact]
    public void Hardware_encoder_settings_check() => OnMtaThread(() =>
    {
        var noLowLatency = EncoderOptions.Default with { LowLatency = false, LowLatencyAttribute = false };
        var variants = new (string Name, EncoderOptions Options, int Frames, int Bitrate)[]
        {
            ("1 no low latency, up to 30 frames", noLowLatency, 30, 8_000_000),
            ("2 display remoting, no low latency, up to 30 frames", noLowLatency with { Scenario = 1 }, 30, 8_000_000),
            ("3 low latency, we provide the output buffer", EncoderOptions.Default with { CallerAllocatesOutput = true }, 1, 8_000_000),
            ("4 low latency, output through Vortice", EncoderOptions.Default with { VorticeProcessOutput = true }, 1, 8_000_000),
            ("5 low latency, aligned input buffer", EncoderOptions.Default with { AlignedInput = true }, 1, 8_000_000),
            ("6 low latency through MF_LOW_LATENCY only", EncoderOptions.Default with { LowLatency = false }, 1, 8_000_000),
            ("7 low latency through ICodecAPI only", EncoderOptions.Default with { LowLatencyAttribute = false }, 1, 8_000_000),
            ("8 TRD settings at 30 Mbps", EncoderOptions.Default, 1, 30_000_000),
            ("9 encoder's own types, we provide the output buffer", EncoderOptions.Default with { OfferedTypes = true, CallerAllocatesOutput = true }, 1, 8_000_000),
            ("10 TRD settings", EncoderOptions.Default, 1, 8_000_000),
        };

        using var duplicator = OpenWithFirstFrame();
        var results = variants
            .Select(v => (v.Name, Result: TryEncode(duplicator, EncoderKind.HardwareMemory, v.Options, maxFrames: v.Frames, bitsPerSecond: v.Bitrate)))
            .ToList();
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

    /// <summary>
    /// Starts a fresh encoder and encodes the captured frame, again up to <paramref name="maxFrames"/> times until
    /// output comes back; says what happened.
    /// </summary>
    private static (bool Ok, string Text) TryEncode(DesktopDuplicator duplicator, EncoderKind kind, EncoderOptions options,
        int maxFrames = 1, int bitsPerSecond = 8_000_000)
    {
        var settings = VideoSettings.ForScreen(duplicator.Width, duplicator.Height) with { BitsPerSecond = bitsPerSecond };
        using var encoder = EncoderFactory.Create(duplicator.Device, duplicator.Context, settings, kind,
            duplicator.AdapterVendorId, out var failures, options);
        if (encoder is null) return (false, "did not start: " + string.Join("; ", failures));

        string applied = string.Join(", ", encoder.Settings);
        try
        {
            using var converter = new Nv12Converter(duplicator.Device, duplicator.Context, duplicator.Width,
                duplicator.Height, settings, encoder.InputBindFlags);
            IReadOnlyList<EncodedFrame> frames = [];
            int sent = 0;
            while (frames.Count == 0 && sent < maxFrames)
            {
                frames = encoder.Encode(converter.Convert(duplicator.Frame!), TimeSpan.FromSeconds(sent / 60.0));
                sent++;
            }

            // Low latency: the encoder answers the first frame at once, with a keyframe in Annex B form.
            if (frames.Count == 0) return (false, $"no output after {sent} frames [{applied}]");
            if (maxFrames == 1 && frames.Count != 1) return (false, $"{frames.Count} frames back instead of 1 [{applied}]");
            var frame = frames[0];
            if (!frame.KeyFrame) return (false, $"first frame is not a keyframe [{applied}]");
            if (frame.Data is not ([0, 0, 1, ..] or [0, 0, 0, 1, ..])) return (false, $"output is not Annex B H.264 [{applied}]");
            return (true, $"OK after {sent} frame(s), {frame.Data.Length / 1024} kB keyframe [{applied}]");
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
