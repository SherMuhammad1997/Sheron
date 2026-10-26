using System.Diagnostics;
using Sheron.Core.Net;
using Sheron.Sim;
using Xunit.Abstractions;

namespace Sheron.Tests.Net;

/// <summary>
/// Plan step 6 "Done when": video frames and input messages go through the bad-network simulator at 5 % loss each
/// way (TRD › Packets). Figures go to the test output (<c>dotnet test --logger "console;verbosity=detailed"</c>).
/// </summary>
public class BadNetworkTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>
    /// One minute of 60 fps video (a keyframe of 150–200 kB every 300 frames, 5–35 kB frames between) at 5 % loss,
    /// 20 ms ± 5 ms delay. At least 99 % of frames must be rebuilt, each byte for byte.
    /// </summary>
    [Fact]
    public void Video_frames_survive_5_percent_loss()
    {
        const int frames = 3600;
        var link = new SimulatedLink(new LinkSettings(Ms(20), Jitter: Ms(5), LossRate: 0.05), seed: 6);
        var packetizer = new VideoPacketizer { LossRate = 0.05 };
        var assembler = new FrameAssembler();
        var random = new Random(60);
        var sent = new List<byte[]>();
        int whole = 0, wrong = 0;
        long dataBytes = 0, packetBytes = 0;
        var arrived = new List<byte[]>();

        void Take(TimeSpan now)
        {
            arrived.Clear();
            link.Receive(now, arrived);
            foreach (var packet in arrived)
            {
                if (assembler.Add(packet) is not { } frame) continue;
                if (frame.Data.AsSpan().SequenceEqual(sent[(int)frame.Number])) whole++;
                else wrong++;
            }
        }

        for (int i = 0; i < frames; i++)
        {
            var now = Ms(i * 1000.0 / 60);
            bool key = i % 300 == 0;
            var data = new byte[key ? random.Next(150_000, 200_000) : random.Next(5_000, 35_000)];
            random.NextBytes(data);
            sent.Add(data);
            dataBytes += data.Length;
            foreach (var packet in packetizer.Packetize(data, key))
            {
                packetBytes += packet.Length;
                link.Send(packet, now);
            }
            Take(now);
        }
        Take(Ms(frames * 1000.0 / 60 + 1000));

        double share = (double)whole / frames;
        output.WriteLine($"frames rebuilt {whole} of {frames} ({share:P2}), lost {assembler.LostFrames}, wrong {wrong}; "
            + $"packets lost {link.Lost} of {link.Sent} ({(double)link.Lost / link.Sent:P2}); data shards rebuilt {assembler.RebuiltShards}; "
            + $"bytes on the wire {(double)packetBytes / dataBytes:P1} of the video");
        Assert.Equal(0, wrong);
        Assert.True(share >= 0.99, $"only {share:P2} of frames rebuilt");
    }

    /// <summary>
    /// 5,000 input messages (one every millisecond, every 50th a 3 kB one) both ways lossy at 5 %, 30 ms ± 10 ms delay
    /// each way. Every message must arrive exactly once and in order.
    /// </summary>
    [Fact]
    public void Input_messages_all_arrive_in_order_at_5_percent_loss()
    {
        const int count = 5000;
        var forward = new SimulatedLink(new LinkSettings(Ms(30), Jitter: Ms(10), LossRate: 0.05), seed: 7);
        var back = new SimulatedLink(new LinkSettings(Ms(30), Jitter: Ms(10), LossRate: 0.05), seed: 8);
        var sender = new ReliableSender();
        var receiver = new ReliableReceiver();
        var random = new Random(9);
        var sent = new List<byte[]>();
        var sentAt = new List<TimeSpan>();
        var delivered = new List<byte[]>();
        var delays = new List<double>();
        var arrived = new List<byte[]>();

        var now = TimeSpan.Zero;
        for (int tick = 0; tick < count + 10_000 && (delivered.Count < count || sender.Unacked > 0); tick++, now += Tick)
        {
            if (tick < count)
            {
                var message = new byte[tick % 50 == 49 ? 3000 : random.Next(4, 40)];
                random.NextBytes(message);
                sent.Add(message);
                sentAt.Add(now);
                sender.Send(message);
            }
            foreach (var packet in sender.Poll(now)) forward.Send(packet, now);

            arrived.Clear();
            forward.Receive(now, arrived);
            foreach (var packet in arrived)
            {
                int before = delivered.Count;
                receiver.OnPacket(packet, delivered);
                for (int i = before; i < delivered.Count; i++) delays.Add((now - sentAt[i]).TotalMilliseconds);
                back.Send(receiver.Ack(), now);
            }

            arrived.Clear();
            back.Receive(now, arrived);
            foreach (var ack in arrived) sender.OnAck(ack, now);
        }

        delays.Sort();
        output.WriteLine($"delivered {delivered.Count} of {count}; resends {sender.Resends}, duplicates {receiver.Duplicates}; "
            + $"round trip {sender.RoundTrip?.TotalMilliseconds:0} ms; delay median {delays[delays.Count / 2]:0} ms, "
            + $"99 % {delays[(int)(delays.Count * 0.99)]:0} ms, max {delays[^1]:0} ms");
        Assert.Equal(count, delivered.Count);
        for (int i = 0; i < count; i++) Assert.True(sent[i].AsSpan().SequenceEqual(delivered[i]), $"message {i} differs");
        Assert.Equal(0, sender.Unacked);
    }

    /// <summary>
    /// Speed first (CLAUDE.md): packets, repair shards and rebuilding fit the TRD's packet budget of under 1 ms for a
    /// typical frame (16.7 kB: 8 Mbit/s at 60 fps) at 5 % loss. A 200 kB keyframe is measured too.
    /// </summary>
    [Fact]
    public void Packets_for_a_frame_take_under_a_millisecond()
    {
        double typical = Measure(16_700, rounds: 300);
        double keyFrame = Measure(200_000, rounds: 30);
        output.WriteLine($"16.7 kB frame {typical:0.000} ms, 200 kB keyframe {keyFrame:0.000} ms (packetize + rebuild with 5 % of shards lost)");
        Assert.True(typical < 1.0, $"{typical:0.000} ms for a 16.7 kB frame");
    }

    /// <summary>Average time to packetize a frame and rebuild it with every 20th data shard lost.</summary>
    private static double Measure(int length, int rounds)
    {
        var frame = new byte[length];
        new Random(length).NextBytes(frame);
        var packetizer = new VideoPacketizer { LossRate = 0.05 };
        var assembler = new FrameAssembler();
        var clock = new Stopwatch();
        for (int round = -5; round < rounds; round++)   // 5 rounds of warm-up
        {
            if (round == 0) clock.Restart();
            var packets = packetizer.Packetize(frame, keyFrame: false);
            for (int i = 0; i < packets.Count; i++)
                if (i % 20 != 7) assembler.Add(packets[i]);
        }
        return clock.Elapsed.TotalMilliseconds / rounds;
    }
}
