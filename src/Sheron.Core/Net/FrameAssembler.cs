namespace Sheron.Core.Net;

/// <summary>
/// One encoded frame put back together from its video packets. <see cref="CompletedAt"/> is when its last needed packet
/// came (a Stopwatch timestamp); <see cref="CapturedAt"/> the capture time on this PC's clock, once the link knows the
/// other PC's clock (TRD › Automatic quality › Delay figure).
/// </summary>
public sealed record AssembledFrame(uint Number, bool KeyFrame, byte[] Data, long? CapturedAt = null, long CompletedAt = 0);

/// <summary>
/// Puts frames back together from video packets, rebuilding lost data shards from repair shards (TRD › Packets), and
/// hands them on in order (TRD › Automatic quality › Jitter buffer). A frame that completes while an earlier one is
/// still missing waits up to <see cref="HoldTime"/>; then the missing ones count in <see cref="LostFrames"/>. With no
/// hold time, or for a keyframe, a newer frame goes on at once and older ones are dropped. Bad packets are ignored.
/// Used by the receiving thread only.
/// </summary>
public sealed class FrameAssembler
{
    public const int MaxPending = 64;

    private readonly Dictionary<uint, PendingFrame> _pending = [];
    private readonly Dictionary<uint, AssembledFrame> _held = [];
    private readonly Dictionary<(int, int), ReedSolomon> _codes = [];
    private uint? _last;
    private long _heldSince;

    /// <summary>Frames that never arrived whole, or arrived after a newer one went on: the viewer needs a keyframe.</summary>
    public int LostFrames { get; private set; }

    /// <summary>Data shards rebuilt from repair shards.</summary>
    public int RebuiltShards { get; private set; }

    /// <summary>Times the hold ran out and a missing frame was given up (part of <see cref="LostFrames"/>).</summary>
    public int GaveUp { get; private set; }

    /// <summary>How long a complete frame may wait for an earlier missing one; zero hands frames on at once.</summary>
    public TimeSpan HoldTime { get; set; }

    /// <summary>Frames waiting for an earlier one.</summary>
    public int Held => _held.Count;

    /// <summary>Counts the waits: a new number each time frames start waiting for a missing one.</summary>
    public int HoldNumber { get; private set; }

    /// <summary>How long the frames now waiting have waited; zero when none is waiting.</summary>
    public TimeSpan HeldFor(long now) => _held.Count == 0 ? TimeSpan.Zero : System.Diagnostics.Stopwatch.GetElapsedTime(_heldSince, now);

    /// <summary>Takes one video packet with no hold time; returns the frame it completes, if any.</summary>
    public AssembledFrame? Add(ReadOnlySpan<byte> packet)
    {
        var output = new List<AssembledFrame>(1);
        Add(packet, 0, output);
        return output.Count > 0 ? output[^1] : null;
    }

    /// <summary>
    /// Takes one video packet that arrived at <paramref name="now"/> (a Stopwatch timestamp) and adds to
    /// <paramref name="output"/> the frames that may go on now, in order.
    /// </summary>
    public void Add(ReadOnlySpan<byte> packet, long now, List<AssembledFrame> output)
    {
        if (!VideoHeader.TryRead(packet, out var h) || !Fits(h, packet.Length)) return;
        if (_last is { } last && !IsNewer(h.Frame, last)) return;   // too late: this frame, or a newer one, went on
        if (_held.ContainsKey(h.Frame)) return;                      // complete already, waiting its turn

        if (!_pending.TryGetValue(h.Frame, out var frame))
        {
            if (_pending.Count >= MaxPending) DropOldest();
            _pending[h.Frame] = frame = new PendingFrame(h.FrameLength, h.KeyFrame, h.Blocks);
        }
        else if (frame.Length != h.FrameLength || frame.KeyFrame != h.KeyFrame)
        {
            return;
        }

        var block = frame.Blocks[h.Block] ??= new PendingBlock(h.DataShards, h.ParityShards);
        if (block.ParityShards != h.ParityShards || block.Done || block.Shards[h.Shard] is not null) return;
        block.Shards[h.Shard] = packet[VideoHeader.Size..].ToArray();
        if (++block.Received < block.DataShards) return;

        int missing = 0;
        for (int j = 0; j < block.DataShards; j++)
            if (block.Shards[j] is null) missing++;
        if (missing > 0)
        {
            Code(block.DataShards, block.ParityShards).Reconstruct(block.Shards);
            RebuiltShards += missing;
        }
        block.Done = true;
        if (++frame.DoneBlocks < frame.Blocks.Length) return;

        _pending.Remove(h.Frame);
        var whole = Join(h.Frame, frame, now);
        bool next = _last is not { } previous || h.Frame == unchecked(previous + 1);
        if (next || frame.KeyFrame || HoldTime <= TimeSpan.Zero)
        {
            Release(whole, output);
            return;
        }
        if (_held.Count == 0)
        {
            _heldSince = now;
            HoldNumber++;
        }
        _held[h.Frame] = whole;
    }

    /// <summary>When the hold time is up, gives up on the missing frame and hands on what waited, in order.</summary>
    public void Expire(long now, List<AssembledFrame> output)
    {
        if (_held.Count == 0) return;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_heldSince, now) < HoldTime) return;
        GaveUp++;
        Release(_held[Oldest(_held.Keys)], output);
        if (_held.Count > 0)
        {
            _heldSince = now;   // the next gap gets its own hold time
            HoldNumber++;
        }
    }

    /// <summary>Hands on <paramref name="frame"/> (anything skipped is lost), then every waiting frame that follows it.</summary>
    private void Release(AssembledFrame frame, List<AssembledFrame> output)
    {
        // Every number skipped is a lost frame, whether some of its packets came or none did.
        if (_last is { } last) LostFrames += (int)(frame.Number - last - 1);
        _last = frame.Number;
        _held.Remove(frame.Number);
        output.Add(frame);
        foreach (var older in _pending.Keys.Where(n => !IsNewer(n, frame.Number)).ToList()) _pending.Remove(older);
        foreach (var older in _held.Keys.Where(n => !IsNewer(n, frame.Number)).ToList()) _held.Remove(older);
        while (_held.Remove(unchecked(frame.Number + 1), out var following))
        {
            _last = following.Number;
            output.Add(following);
            frame = following;
        }
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

    private static AssembledFrame Join(uint number, PendingFrame frame, long now)
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
        return new AssembledFrame(number, frame.KeyFrame, data, CompletedAt: now);
    }

    private void DropOldest() => _pending.Remove(Oldest(_pending.Keys));

    private static uint Oldest(IEnumerable<uint> numbers)
    {
        uint? oldest = null;
        foreach (uint n in numbers)
            if (oldest is not { } o || IsNewer(o, n)) oldest = n;
        return oldest!.Value;
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
