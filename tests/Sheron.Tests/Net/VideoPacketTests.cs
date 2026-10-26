using Sheron.Core.Net;

namespace Sheron.Tests.Net;

public class VideoPacketTests
{
    private static byte[] Frame(int length, int seed)
    {
        var frame = new byte[length];
        new Random(seed).NextBytes(frame);
        return frame;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1152)]
    [InlineData(1153)]
    [InlineData(16_700)]     // a frame at 8 Mbit/s and 60 fps
    [InlineData(250_000)]    // a large keyframe: 218 shards in 7 blocks
    public void A_frame_comes_back_byte_for_byte_with_every_packet_within_the_limit(int length)
    {
        var frame = Frame(length, length);
        var packets = new VideoPacketizer().Packetize(frame, keyFrame: true);

        Assert.All(packets, p => Assert.InRange(p.Length, VideoHeader.Size + 1, PacketLimits.MaxPlain));
        var assembler = new FrameAssembler();
        AssembledFrame? result = null;
        foreach (var packet in packets) result ??= assembler.Add(packet);

        Assert.NotNull(result);
        Assert.True(result.KeyFrame);
        Assert.Equal(frame, result.Data);
    }

    [Fact]
    public void Shards_are_dealt_into_blocks_of_at_most_32_as_evenly_as_possible()
    {
        // 218 shards → 7 blocks: 32, 31, 31, 31, 31, 31, 31.
        var (shards, _) = VideoLayout.Shards(250_000);
        int blocks = VideoLayout.Blocks(shards);

        Assert.Equal((218, 7), (shards, blocks));
        Assert.Equal(new[] { 32, 31, 31, 31, 31, 31, 31 }, Enumerable.Range(0, blocks).Select(b => VideoLayout.DataShardsIn(b, shards, blocks)));
        Assert.Equal(32, VideoLayout.FirstShardOf(1, shards, blocks));
        Assert.Equal(218 - 31, VideoLayout.FirstShardOf(6, shards, blocks));
    }

    [Fact]
    public void Lost_data_shards_are_rebuilt_from_repair_shards()
    {
        var frame = Frame(16_700, 1);
        var packetizer = new VideoPacketizer { LossRate = 0.05 };
        var packets = packetizer.Packetize(frame, keyFrame: false);
        Assert.Equal(15 + 5, packets.Count);

        // Lose 5 data shards: exactly as many as there are repair shards.
        var assembler = new FrameAssembler();
        AssembledFrame? result = null;
        foreach (var packet in packets.Where((_, i) => i % 3 != 0 || i >= 15)) result ??= assembler.Add(packet);

        Assert.NotNull(result);
        Assert.Equal(frame, result.Data);
        Assert.Equal(5, assembler.RebuiltShards);
    }

    [Fact]
    public void Packets_out_of_order_and_twice_still_give_the_frame_once()
    {
        var frame = Frame(40_000, 2);
        var packets = new VideoPacketizer().Packetize(frame, keyFrame: false);
        var assembler = new FrameAssembler();

        var results = packets.AsEnumerable().Reverse().Concat(packets).Select(p => assembler.Add(p)).Where(f => f is not null).ToList();

        Assert.Equal(frame, Assert.Single(results)!.Data);
    }

    [Fact]
    public void A_frame_that_never_arrives_is_counted_lost_and_a_late_one_is_dropped()
    {
        var packetizer = new VideoPacketizer();
        var first = packetizer.Packetize(Frame(5_000, 3), false);
        var second = packetizer.Packetize(Frame(5_000, 4), false);
        var third = packetizer.Packetize(Frame(5_000, 5), false);
        var assembler = new FrameAssembler();

        foreach (var p in first) assembler.Add(p);
        foreach (var p in third) assembler.Add(p);
        var late = second.Select(p => assembler.Add(p)).Where(f => f is not null).ToList();

        Assert.Equal(1, assembler.LostFrames);
        Assert.Empty(late);
    }

    [Fact]
    public void Bad_packets_are_ignored()
    {
        var packets = new VideoPacketizer().Packetize(Frame(3_000, 6), false);
        var assembler = new FrameAssembler();
        var random = new Random(7);

        // Too short, wrong kind, a shard of the wrong size, and random headers on a real packet.
        Assert.Null(assembler.Add(new byte[] { 1, 0, 0 }));
        Assert.Null(assembler.Add(new byte[] { 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Null(assembler.Add(packets[0].AsSpan(0, packets[0].Length - 1)));
        for (int i = 0; i < 1000; i++)
        {
            var broken = packets[i % packets.Count].ToArray();
            broken[1 + random.Next(14)] = (byte)random.Next(256);
            assembler.Add(broken);   // must not throw
        }
    }
}
