using System.Buffers.Binary;

namespace Sheron.Core.Net;

/// <summary>The 16-byte header of a video packet (TRD › Packets).</summary>
public readonly record struct VideoHeader(bool KeyFrame, bool Repair, uint Frame, int FrameLength, int Block, int Blocks,
    int Shard, int DataShards, int ParityShards)
{
    public const int Size = 16;

    private const byte KeyFrameFlag = 0x01;
    private const byte RepairFlag = 0x02;

    public void Write(Span<byte> packet)
    {
        packet[0] = (byte)PacketKind.Video;
        packet[1] = (byte)((KeyFrame ? KeyFrameFlag : 0) | (Repair ? RepairFlag : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(packet[2..], Frame);
        BinaryPrimitives.WriteInt32LittleEndian(packet[6..], FrameLength);
        packet[10] = (byte)Block;
        packet[11] = (byte)Blocks;
        packet[12] = (byte)Shard;
        packet[13] = (byte)DataShards;
        packet[14] = (byte)ParityShards;
        packet[15] = 0;
    }

    /// <summary>False when the packet is not a video packet or too short to hold one.</summary>
    public static bool TryRead(ReadOnlySpan<byte> packet, out VideoHeader header)
    {
        header = default;
        if (packet.Length <= Size || packet[0] != (byte)PacketKind.Video) return false;
        header = new VideoHeader(
            (packet[1] & KeyFrameFlag) != 0,
            (packet[1] & RepairFlag) != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(packet[6..]),
            packet[10], packet[11], packet[12], packet[13], packet[14]);
        return true;
    }
}

/// <summary>
/// How a frame is cut into shards and blocks (TRD › Packets): the fewest shards of at most
/// <see cref="MaxShardSize"/> bytes, all the same size, dealt into the fewest blocks of at most
/// <see cref="MaxDataShardsPerBlock"/> data shards, the first blocks taking one more when they do not divide evenly.
/// Sender and receiver both work it out from the frame length alone.
/// </summary>
public static class VideoLayout
{
    public const int MaxShardSize = PacketLimits.MaxPlain - VideoHeader.Size;
    public const int MaxDataShardsPerBlock = 32;
    public const int MaxBlocks = 255;

    /// <summary>The largest frame that fits: 255 blocks of 32 shards of 1,152 bytes (about 9.4 MB).</summary>
    public const int MaxFrameLength = MaxBlocks * MaxDataShardsPerBlock * MaxShardSize;

    /// <summary>How many data shards a frame of <paramref name="frameLength"/> bytes needs, and their size.</summary>
    public static (int Shards, int ShardSize) Shards(int frameLength)
    {
        int shards = Math.Max(1, (frameLength + MaxShardSize - 1) / MaxShardSize);
        int size = Math.Max(1, (frameLength + shards - 1) / shards);
        return (shards, size);
    }

    public static int Blocks(int shards) => (shards + MaxDataShardsPerBlock - 1) / MaxDataShardsPerBlock;

    public static int DataShardsIn(int block, int shards, int blocks) => shards / blocks + (block < shards % blocks ? 1 : 0);

    /// <summary>The frame-wide number of a block's first data shard.</summary>
    public static int FirstShardOf(int block, int shards, int blocks) => block * (shards / blocks) + Math.Min(block, shards % blocks);
}
