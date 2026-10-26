namespace Sheron.Core.Net;

/// <summary>
/// Cuts encoded frames into video packets with repair shards (TRD › Packets). Used by the sending thread only.
/// </summary>
public sealed class VideoPacketizer
{
    private readonly Dictionary<(int, int), ReedSolomon> _codes = [];
    private uint _nextFrame;

    /// <summary>The link's loss rate as the viewer reports it (Plan step 10); sets how many repair shards are made.</summary>
    public double LossRate { get; set; } = FecPolicy.MinLossRate;

    /// <summary>Packets for one frame: per block its data shards, then its repair shards. Each is at most <see cref="PacketLimits.MaxPlain"/> bytes.</summary>
    public List<byte[]> Packetize(ReadOnlySpan<byte> frame, bool keyFrame)
    {
        if (frame.Length > VideoLayout.MaxFrameLength)
            throw new ArgumentException($"A frame of {frame.Length} bytes is larger than {VideoLayout.MaxFrameLength}.", nameof(frame));

        var (shards, size) = VideoLayout.Shards(frame.Length);
        int blocks = VideoLayout.Blocks(shards);
        uint number = _nextFrame++;
        var packets = new List<byte[]>();
        for (int block = 0; block < blocks; block++)
        {
            int k = VideoLayout.DataShardsIn(block, shards, blocks);
            int first = VideoLayout.FirstShardOf(block, shards, blocks);
            int m = FecPolicy.ParityShards(k, LossRate);

            // Each shard is made right inside the packet it goes out in.
            var shardsOfBlock = new Memory<byte>[k + m];
            for (int j = 0; j < k + m; j++)
            {
                var packet = new byte[VideoHeader.Size + size];
                new VideoHeader(keyFrame, j >= k, number, frame.Length, block, blocks, j, k, m).Write(packet);
                shardsOfBlock[j] = packet.AsMemory(VideoHeader.Size);
                if (j < k)
                {
                    int offset = (first + j) * size;
                    int count = Math.Clamp(frame.Length - offset, 0, size);
                    frame.Slice(offset, count).CopyTo(shardsOfBlock[j].Span);   // the rest stays zero: padding
                }
                packets.Add(packet);
            }
            Code(k, m).Encode(shardsOfBlock);
        }
        return packets;
    }

    private ReedSolomon Code(int dataShards, int parityShards)
    {
        if (!_codes.TryGetValue((dataShards, parityShards), out var code))
            _codes[(dataShards, parityShards)] = code = new ReedSolomon(dataShards, parityShards);
        return code;
    }
}
