using System.Diagnostics;
using Sheron.Core.Net;

namespace Sheron.Tests.Net;

/// <summary>Plan step 10: the assembler's jitter buffer hands frames on in order (TRD › Automatic quality › Jitter buffer).</summary>
public class JitterBufferTests
{
    private static List<byte[]> Packets(VideoPacketizer packetizer, int size, bool keyFrame = false)
    {
        var frame = new byte[size];
        new Random(size).NextBytes(frame);
        return packetizer.Packetize(frame, keyFrame);
    }

    private static List<uint> Add(FrameAssembler assembler, IEnumerable<byte[]> packets, long now)
    {
        var output = new List<AssembledFrame>();
        foreach (var packet in packets) assembler.Add(packet, now, output);
        return [.. output.Select(f => f.Number)];
    }

    [Fact]
    public void A_frame_that_overtakes_an_earlier_one_waits_for_it_and_both_go_on_in_order()
    {
        var packetizer = new VideoPacketizer();
        var f0 = Packets(packetizer, 3000, keyFrame: true);
        var f1 = Packets(packetizer, 3000);
        var f2 = Packets(packetizer, 3000);
        var assembler = new FrameAssembler { HoldTime = TimeSpan.FromMilliseconds(200) };
        long t = Stopwatch.GetTimestamp();

        Assert.Equal([0u], Add(assembler, f0, t));
        Assert.Empty(Add(assembler, f2, t));          // frame 1 is still missing: frame 2 waits
        Assert.Equal(1, assembler.Held);
        Assert.Equal([1u, 2u], Add(assembler, f1, t));
        Assert.Equal(0, assembler.LostFrames);
    }

    [Fact]
    public void When_the_hold_time_is_up_the_missing_frame_is_lost_and_the_waiting_ones_go_on()
    {
        var packetizer = new VideoPacketizer();
        var f0 = Packets(packetizer, 3000, keyFrame: true);
        Packets(packetizer, 3000);                     // frame 1 never arrives
        var f2 = Packets(packetizer, 3000);
        var f3 = Packets(packetizer, 3000);
        var assembler = new FrameAssembler { HoldTime = TimeSpan.FromMilliseconds(100) };
        long t = Stopwatch.GetTimestamp();
        Add(assembler, f0, t);
        Add(assembler, f2, t);
        Add(assembler, f3, t);

        var output = new List<AssembledFrame>();
        assembler.Expire(t + Stopwatch.Frequency / 20, output);   // 50 ms: still waiting
        Assert.Empty(output);
        assembler.Expire(t + Stopwatch.Frequency / 5, output);    // 200 ms: given up
        Assert.Equal([2u, 3u], output.Select(f => f.Number));
        Assert.Equal((1, 1), (assembler.LostFrames, assembler.GaveUp));
    }

    [Fact]
    public void A_keyframe_goes_on_at_once_and_with_no_hold_time_nothing_waits()
    {
        var packetizer = new VideoPacketizer();
        var f0 = Packets(packetizer, 3000, keyFrame: true);
        Packets(packetizer, 3000);
        var key = Packets(packetizer, 3000, keyFrame: true);
        var held = new FrameAssembler { HoldTime = TimeSpan.FromMilliseconds(200) };
        long t = Stopwatch.GetTimestamp();
        Add(held, f0, t);
        Assert.Equal([2u], Add(held, key, t));
        Assert.Equal(1, held.LostFrames);

        var packetizer2 = new VideoPacketizer();
        var g0 = Packets(packetizer2, 3000, keyFrame: true);
        Packets(packetizer2, 3000);
        var g2 = Packets(packetizer2, 3000);
        var none = new FrameAssembler();
        Add(none, g0, t);
        Assert.Equal([2u], Add(none, g2, t));
    }
}
