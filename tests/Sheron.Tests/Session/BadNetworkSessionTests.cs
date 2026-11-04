using System.Diagnostics;
using System.Net;
using Sheron.Core.Net;
using Sheron.Core.Security;
using Sheron.Core.Session;
using Sheron.Core.Video;
using Sheron.Sim;
using Xunit.Abstractions;

namespace Sheron.Tests.Session;

/// <summary>
/// Plan step 10 "Done when" (TRD › Automatic quality): a real-time session through the network simulator at
/// 300 ms ± 100 ms each way and 5 % loss each way stays usable, with no freeze longer than 1 s. The remote PC's side
/// sends frames as automatic quality sets them (sizes from the bitrate and fps) and a keyframe on request; the viewer
/// shows only frames it could decode (after a gap, nothing until a keyframe) and asks for a keyframe as the app does.
/// </summary>
[Collection(Timing.Name)]
public class BadNetworkSessionTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Run = TimeSpan.FromSeconds(20);

    [Fact]
    public void At_300_ms_plus_or_minus_100_and_5_percent_loss_the_picture_never_freezes_for_a_second()
    {
        var net = new SimNetwork(seed: 300);
        var internet = net.AddSite("internet", null);
        net.Impair(internet, internet, new LinkImpairment(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100), 0.05));
        using var hostUdp = net.Bind(internet, new IPEndPoint(IPAddress.Parse("10.1.1.1"), 47920));
        using var viewerUdp = net.Bind(internet, new IPEndPoint(IPAddress.Parse("10.2.2.2"), 50000));
        using var hostKey = DeviceKey.Create();
        using var viewerKey = DeviceKey.Create();
        using var host = Link.Host(hostKey, hostUdp);
        using var viewer = Link.Viewer(viewerKey, viewerUdp, hostUdp.LocalEndPoint);
        var clock = Stopwatch.StartNew();

        // Remote PC: automatic quality from the viewer's reports; keyframes on request.
        var quality = new QualityController();
        var levels = new List<string>();
        bool keyFrameWanted = true;
        int inputsIn = 0, inputsOutOfOrder = 0, lastInput = -1;
        host.ReportReceived += report =>
        {
            lock (quality)
                if (quality.OnReport(report, clock.Elapsed)) levels.Add($"{clock.Elapsed.TotalSeconds:0.0} s: {quality.Current}");
        };
        host.ControlReceived += message =>
        {
            if (ControlMessage.TypeOf(message) == ControlType.KeyFrameRequest)
            {
                keyFrameWanted = true;
                return;
            }
            Assert.True(InputMessage.TryReadMove(message, out var at));
            int x = at.X;
            if (x != lastInput + 1) inputsOutOfOrder++;
            lastInput = x;
            inputsIn++;
        };

        // Viewer: what the decoder could show, and keyframe requests after a gap (TRD › After a lost frame).
        var shown = new List<TimeSpan>();
        var gapsAt = new List<string>();
        var delays = new List<double>();
        uint? lastNumber = null;
        bool waitingForKeyFrame = false;
        TimeSpan askedAt = TimeSpan.Zero;
        int keyFramesAsked = 0;
        viewer.FrameReceived += frame =>
        {
            var now = clock.Elapsed;
            lock (shown)
            {
                bool gap = lastNumber is { } last && frame.Number != unchecked(last + 1);
                if (gap) gapsAt.Add($"{now.TotalSeconds:0.00} s ({frame.Number - lastNumber!.Value - 1} missing{(frame.KeyFrame ? ", keyframe" : "")})");
                lastNumber = frame.Number;
                if (frame.KeyFrame) waitingForKeyFrame = false;
                else if (gap && !waitingForKeyFrame)
                {
                    waitingForKeyFrame = true;
                    askedAt = now;
                    keyFramesAsked++;
                    viewer.SendControl(ControlMessage.KeyFrameRequest());
                }
                else if (waitingForKeyFrame && now - askedAt >= Link.AskKeyFrameAgainAfter(viewer.Snapshot().RoundTrip))
                {
                    askedAt = now;
                    keyFramesAsked++;
                    viewer.SendControl(ControlMessage.KeyFrameRequest());
                }
                if (waitingForKeyFrame) return;
                shown.Add(now);
                if (frame.CapturedAt is { } captured) delays.Add(Stopwatch.GetElapsedTime(captured).TotalMilliseconds);
            }
        };

        Assert.True(Within(() => host.Snapshot().State == LinkState.Connected && viewer.Snapshot().State == LinkState.Connected, 10_000),
            "no session at 600 ms round trip");

        // Frames as the encoder would make them at the current level; input from the viewer every 50 ms.
        var stop = clock.Elapsed + Run;
        var random = new Random(10);
        int frameIndex = 0, inputsOut = 0;
        var nextInput = TimeSpan.Zero;
        while (clock.Elapsed < stop)
        {
            QualityLevel level;
            lock (quality) level = quality.Current;
            bool key = keyFrameWanted || frameIndex % 300 == 0;
            keyFrameWanted = false;
            int size = level.BitsPerSecond / 8 / level.FramesPerSecond * level.ScalePercent / 100;
            var frame = new byte[key ? size * 8 : random.Next(size / 2, size * 3 / 2)];
            random.NextBytes(frame);
            host.SendFrame(frame, key);
            frameIndex++;
            if (clock.Elapsed >= nextInput)
            {
                viewer.SendControl(InputMessage.Move(new AbsolutePoint((ushort)inputsOut++, 0)));
                nextInput = clock.Elapsed + TimeSpan.FromMilliseconds(50);
            }
            Thread.Sleep(1000 / level.FramesPerSecond);
        }
        Assert.True(Within(() => inputsIn == inputsOut, 5_000), $"{inputsIn} of {inputsOut} input messages arrived");

        List<TimeSpan> times;
        lock (shown) times = [.. shown];
        Assert.True(times.Count > 0, "nothing shown");
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).Append(stop - times[^1]).ToList();
        var longest = gaps.Max();
        var v = viewer.Snapshot();
        var h = host.Snapshot();
        double medianDelay = delays.Count == 0 ? double.NaN : delays.OrderBy(d => d).ElementAt(delays.Count / 2);
        output.WriteLine($"Shown {times.Count} frames in {Run.TotalSeconds:0} s ({times.Count / Run.TotalSeconds:0.0} fps); longest gap {longest.TotalMilliseconds:0} ms");
        output.WriteLine($"Frames sent {h.FramesSent}, lost after repair {v.LostFrames} (hold ran out {v.GaveUp} times), keyframes asked {keyFramesAsked}, shards rebuilt {v.RebuiltShards}");
        output.WriteLine($"Packet loss measured {v.PacketLoss:P1} (reported to the remote PC: {h.PacketLoss:P1}); jitter {v.JitterMs} ms; hold {v.HoldTime.TotalMilliseconds:0} ms; queue {v.QueueMs} ms");
        output.WriteLine($"Capture → shown, median {medianDelay:0} ms over {delays.Count} frames (clock matched: {v.ClockKnown}); round trip {h.RoundTrip?.TotalMilliseconds:0} ms");
        output.WriteLine($"Input: {inputsIn} of {inputsOut}, {inputsOutOfOrder} out of order");
        output.WriteLine("Quality changes: " + (levels.Count == 0 ? "none" : string.Join("; ", levels)));
        output.WriteLine("Gaps in frame numbers: " + (gapsAt.Count == 0 ? "none" : string.Join("; ", gapsAt)));
        output.WriteLine("Pauses over 300 ms: " + string.Join("; ", times.Zip(times.Skip(1), (a, b) => (a, b))
            .Where(p => p.b - p.a > TimeSpan.FromMilliseconds(300)).Select(p => $"{p.a.TotalSeconds:0.00} s +{(p.b - p.a).TotalMilliseconds:0} ms")));

        Assert.True(longest < TimeSpan.FromSeconds(1), $"a freeze of {longest.TotalMilliseconds:0} ms");
        Assert.True(times.Count >= 10 * Run.TotalSeconds, $"only {times.Count} frames shown");
        Assert.Equal(0, inputsOutOfOrder);
        Assert.True(v.ClockKnown);
        Assert.InRange(medianDelay, 150, 900);
    }

    private static bool Within(Func<bool> condition, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(5);
        }
        return true;
    }
}
