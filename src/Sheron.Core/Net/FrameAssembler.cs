namespace Sheron.Core.Net;

/// <summary>One encoded frame put back together from its video packets.</summary>
public sealed record AssembledFrame(uint Number, bool KeyFrame, byte[] Data);

/// <summary>
/// Puts frames back together from video packets, rebuilding lost data shards from repair shards (TRD › Packets).
/// A frame newer than the last one handed on is handed on as soon as all its blocks are in; older ones are dropped,
/// and frames skipped are counted in <see cref="LostFrames"/>. Bad packets are ignored. Used by the receiving thread only.
/// </summary>
public sealed class FrameAssembler
{
    public const int MaxPending = 64;

    private readonly Dictionary<uint, PendingFrame> _pending = [];
    private readonly Dictionary<(int, int), ReedSolomon> _codes = [];
    private uint? _last;

    /// <summary>Frames that never arrived whole, or arrived after a newer one: the viewer needs a keyframe.</summary>
    public int LostFrames { get; private set; }

    /// <summary>Data shards rebuilt from repair shards.</summary>
    public int RebuiltShards { get; private set; }

    /// <summary>Takes one video packet. Returns the frame it completes, if any.</summary>
    public AssembledFrame? Add(ReadOnlySpan<byte> packet)
    {
        if (!VideoHeader.TryRead(packet, out var h) || !Fits(h, packet.Length)) return null;
        if (_last is { } last && !IsNewer(h.Frame, last)) return null;   // too late: a newer frame was handed on

        if (!_pending.TryGetValue(h.Frame, out var frame))
        {
            if (_pending.Count >= MaxPending) DropOldest();
            _pending[h.Frame] = frame = new PendingFrame(h.FrameLength, h.KeyFrame, h.Blocks);
        }
        else if (frame.Length != h.FrameLength || frame.KeyFrame != h.KeyFrame)
        {
            return null;
        }

        var block = frame.Blocks[h.Block] ??= new PendingBlock(h.DataShards, h.ParityShards);
        if (block.ParityShards != h.ParityShards || block.Done || block.Shards[h.Shard] is not null) return null;
        block.Shards[h.Shard] = packet[VideoHeader.Size..].ToArray();
        if (++block.Received < block.DataShards) return null;

        int missing = 0;
        for (int j = 0; j < block.DataShards; j++)
            if (block.Shards[j] is null) missing++;
        if (missing > 0)
        {
            Code(block.DataShards, block.ParityShards).Reconstruct(block.Shards);
            RebuiltShards += missing;
        }
        block.Done = true;
        if (++frame.DoneBlocks < frame.Blocks.Length) return null;
        return HandOn(h.Frame, frame);
    }

    /// <summary>The header agrees with the layout the frame length gives, and the packet holds exactly one shard.</summary>
    private static bool Fits(VideoHeader h, int packetLength)
    {
        if (h.FrameLength < 0 || h.FrameLength > VideoLayout.MaxFrameLength) return false;
        var (shards, size) = VideoLayout.Shards(h.FrameLength);
        int blocks = VideoLayout.Blocks(shards);
        return h.Blocks == blocks && h.Block < blocks && packetLength - VideoHeader.Size == size
            && h.DataShards == VideoLayout.DataShardsIn(h.Block, shards, blocks)
            && h.ParityShards <= h.DataShards && h.Shard < h.DataShards + h.ParityShards
            && h.Repair == h.Shard >= h.DataShards;
    }

    private AssembledFrame HandOn(uint number, PendingFrame frame)
    {
        var data = new byte[frame.Length];
        var (shards, size) = VideoLayout.Shards(frame.Length);
        for (int b = 0; b < frame.Blocks.Length; b++)
        {
            var block = frame.Blocks[b]!;
            int first = VideoLayout.FirstShardOf(b, shards, frame.Blocks.Length);
            for (int j = 0; j < block.DataShards; j++)
            {
                int offset = (first + j) * size;
                int count = Math.Clamp(frame.Length - offset, 0, size);
                block.Shards[j]!.AsSpan(0, count).CopyTo(data.AsSpan(offset));
            }
        }

        // Every number skipped is a lost frame, whether some of its packets came or none did.
        if (_last is { } last) LostFrames += (int)(number - last - 1);
        _last = number;
        foreach (var older in _pending.Keys.Where(n => !IsNewer(n, number)).ToList()) _pending.Remove(older);
        return new AssembledFrame(number, frame.KeyFrame, data);
    }

    private void DropOldest()
    {
        uint oldest = _pending.Keys.First();
        foreach (uint n in _pending.Keys)
            if (IsNewer(oldest, n)) oldest = n;
        _pending.Remove(oldest);
    }

    /// <summary>Frame numbers wrap, so "newer" means less than half the number range ahead.</summary>
    private static bool IsNewer(uint a, uint b) => (int)(a - b) > 0;

    private ReedSolomon Code(int dataShards, int parityShards)
    {
        if (!_codes.TryGetValue((dataShards, parityShards), out var code))
            _codes[(dataShards, parityShards)] = code = new ReedSolomon(dataShards, parityShards);
        return code;
    }

    private sealed class PendingFrame(int length, bool keyFrame, int blocks)
    {
        public int Length { get; } = length;
        public bool KeyFrame { get; } = keyFrame;
        public PendingBlock?[] Blocks { get; } = new PendingBlock?[blocks];
        public int DoneBlocks { get; set; }
    }

    private sealed class PendingBlock(int dataShards, int parityShards)
    {
        public int DataShards { get; } = dataShards;
        public int ParityShards { get; } = parityShards;
        public byte[]?[] Shards { get; } = new byte[]?[dataShards + parityShards];
        public int Received { get; set; }
        public bool Done { get; set; }
    }
}
