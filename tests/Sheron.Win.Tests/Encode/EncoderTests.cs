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
    public void Intel_oneVPL_encoder_makes_an_H264_keyframe() =>
        OnMtaThread(() => AssertEncodes(EncoderKind.IntelVpl));

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
    /// Plan step 4 check: drives the hardware encoder in different ways and says how many frames it needed before
    /// the first output. Intel Quick Sync fails with E_UNEXPECTED on its first output unless the scenario is
    /// display remoting (25 Oct). Fails, listing every result, unless the TRD settings (the last line) give
    /// output for the very first frame.
    /// </summary>
    [Fact]
    public void Hardware_encoder_settings_check() => OnMtaThread(() =>
    {
        var trd = EncoderOptions.Default;
        var variants = new (string Name, EncoderOptions Options, int Frames)[]
        {
            ("1 without display remoting (old settings)", trd with { Scenario = 0 }, 30),
            ("2 display remoting, no low latency", trd with { LowLatency = false, LowLatencyAttribute = false }, 30),
            ("3 display remoting, low latency through ICodecAPI only", trd with { LowLatencyAttribute = false }, 30),
            ("4 display remoting, low latency through MF_LOW_LATENCY only", trd with { LowLatency = false }, 30),
            ("5 display remoting, low latency, encoder's own types", trd with { OfferedTypes = true }, 30),
            ("6 TRD settings, up to 30 frames", trd, 30),
            ("7 TRD settings: first frame must come straight back", trd, 1),
        };

        using var duplicator = OpenWithFirstFrame();
        var results = variants
            .Select(v => (v.Name, Result: TryEncode(duplicator, EncoderKind.HardwareMemory, v.Options, maxFrames: v.Frames)))
            .ToList();
        string report = string.Join(Environment.NewLine, results.Select(r => $"{r.Name}: {r.Result.Text}"));
        Assert.True(results[^1].Result.Ok, Environment.NewLine + report);
    });

    /// <summary>
    /// Plan step 4b "Done when": the Intel oneVPL encoder's own time (frame in → output ready) is 5 ms or less on
    /// average. Measures 60 frames at 60 fps pace after a 10-frame warm-up, for both oneVPL input ways, with Media
    /// Foundation's best route for comparison (25 Oct: about 8 ms, never below 6.5 ms). Fails with the whole table
    /// unless the better oneVPL line is at or under 5 ms.
    /// </summary>
    [Fact]
    public void Hardware_encoder_is_fast_enough() => OnMtaThread(() =>
    {
        var trd = EncoderOptions.Default;
        var variants = new (string Name, EncoderKind Kind, EncoderOptions Options)[]
        {
            ("1 Intel oneVPL, copy into encoder surface", EncoderKind.IntelVpl, trd),
            ("2 Intel oneVPL, import our texture", EncoderKind.IntelVpl, trd with { VplImport = true }),
            ("3 Media Foundation, GPU input (for comparison)", EncoderKind.HardwareGpu, trd),
        };

        using var timer = new TimerResolution();
        using var duplicator = OpenWithFirstFrame();
        var results = variants.Select(v => (v.Name, Text: MeasureOwnTime(duplicator, v.Kind, v.Options, out double average)
            , Average: average)).ToList();
        string report = string.Join(Environment.NewLine, results.Select(r => $"{r.Name}: {r.Text}"));
        double best = results.Take(2).Where(r => r.Average > 0).Select(r => r.Average).DefaultIfEmpty(double.MaxValue).Min();
        Assert.True(best <= 5.0, $"{Environment.NewLine}timer 1 ms: {timer.IsFine}{Environment.NewLine}{report}");
    });

    /// <summary>Encodes the captured frame 70 times at 60 fps pace; reports the encoder's own time after warm-up.</summary>
    private static string MeasureOwnTime(DesktopDuplicator duplicator, EncoderKind kind, EncoderOptions options, out double average)
    {
        average = 0;
        var settings = VideoSettings.ForScreen(duplicator.Width, duplicator.Height);
        using var encoder = EncoderFactory.Create(duplicator.Device, duplicator.Context, settings, kind,
            duplicator.AdapterVendorId, duplicator.AdapterIndex, out var failures, options);
        if (encoder is null) return "did not start: " + string.Join("; ", failures);
        try
        {
            using var converter = new Nv12Converter(duplicator.Device, duplicator.Context, duplicator.Width,
                duplicator.Height, settings, encoder.InputBindFlags);
            var own = new List<double>();
            var round = new List<double>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 70; i++)
            {
                var due = TimeSpan.FromSeconds(i / 60.0);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                var frames = encoder.Encode(converter.Convert(duplicator.Frame!), clock.Elapsed);
                double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (i < 10) continue;   // warm-up
                round.Add(ms);
                own.AddRange(frames.Select(f => f.EncoderOwnMs));
            }
            if (own.Count == 0) return $"no output [{string.Join(", ", encoder.Settings)}]";
            average = own.Average();
            return $"inside encoder avg {average:0.00} ms (min {own.Min():0.00}, max {own.Max():0.00}), "
                + $"round trip avg {round.Average():0.00} ms, held back {encoder.HeldBack} [{string.Join(", ", encoder.Settings)}]";
        }
        catch (Exception error)
        {
            return $"{encoder.Details}: {EncoderFactory.Describe(error)}";
        }
    }

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
            duplicator.AdapterVendorId, duplicator.AdapterIndex, out var failures, options);
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
