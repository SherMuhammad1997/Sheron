using System.Buffers.Binary;
using System.Numerics;

namespace Sheron.Core.Net;

/// <summary>
/// The sending half of the reliable channel for input and control messages (TRD › Packets): numbers fragments, packs
/// them into reliable packets, and sends them again until an ack covers them. Works on the caller's clock
/// (<c>now</c>), so tests can run it on a virtual one. Used by one thread only.
/// </summary>
public sealed class ReliableSender
{
    /// <summary>Most fragment numbers in flight at once.</summary>
    public const int Window = 1024;

    public const int PacketHeaderSize = 2;
    public const int EntryHeaderSize = 7;
    public const int MaxFragment = PacketLimits.MaxPlain - PacketHeaderSize - EntryHeaderSize;

    public static readonly TimeSpan FirstResendAfter = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MinResendAfter = TimeSpan.FromMilliseconds(30);
    public static readonly TimeSpan MaxResendAfter = TimeSpan.FromSeconds(2);

    /// <summary>Not yet acked, in number order.</summary>
    private readonly List<Fragment> _waiting = [];
    private uint _next;
    private TimeSpan? _roundTrip;
    private TimeSpan _variation;

    /// <summary>Fragments sent but not yet acked, or not yet sent.</summary>
    public int Unacked => _waiting.Count;

    /// <summary>Fragments sent more than once.</summary>
    public int Resends { get; private set; }

    /// <summary>Smoothed round trip, once one is measured.</summary>
    public TimeSpan? RoundTrip => _roundTrip;

    /// <summary>How long a sent fragment waits for its ack before it is sent again (the first time).</summary>
    public TimeSpan ResendAfter => _roundTrip is { } rtt
        ? Clamp(rtt + 4 * _variation, MinResendAfter, MaxResendAfter)
        : FirstResendAfter;

    /// <summary>Queues one message; it goes out at the next <see cref="Poll"/>.</summary>
    public void Send(ReadOnlySpan<byte> message)
    {
        int offset = 0;
        do
        {
            int count = Math.Min(MaxFragment, message.Length - offset);
            _waiting.Add(new Fragment(_next++, message.Slice(offset, count).ToArray(), offset + count == message.Length));
            offset += count;
        }
        while (offset < message.Length);
    }

    /// <summary>The packets to send now: new fragments, and those due to be sent again.</summary>
    public List<byte[]> Poll(TimeSpan now)
    {
        var due = new List<Fragment>();
        if (_waiting.Count > 0)
        {
            uint oldest = _waiting[0].Sequence;
            // A gap the ack shows is only taken as a loss once the fragment's own ack is overdue: a round trip and a
            // quarter after it went. Before that it may just be late, overtaken by a later packet.
            var rtt = _roundTrip ?? FirstResendAfter;
            var lostAfter = rtt + rtt / 4;
            foreach (var f in _waiting)
            {
                if ((int)(f.Sequence - oldest) >= Window) break;
                bool send = f.Sends == 0
                    || (f.Missing && now - f.SentAt >= lostAfter)
                    || now - f.SentAt >= Backoff(f.Sends);
                if (send) due.Add(f);
            }
        }

        var packets = new List<byte[]>();
        int start = 0;
        while (start < due.Count)
        {
            // As many fragments as fit one packet, in number order.
            int size = PacketHeaderSize, end = start;
            while (end < due.Count && end - start < byte.MaxValue && size + EntryHeaderSize + due[end].Data.Length <= PacketLimits.MaxPlain)
                size += EntryHeaderSize + due[end++].Data.Length;
            packets.Add(Pack(due, start, end, size));
            start = end;
        }

        foreach (var f in due)
        {
            if (f.Sends > 0) Resends++;
            f.Sends++;
            f.SentAt = now;
            f.Missing = false;
        }
        return packets;
    }

    /// <summary>Takes an ack packet: drops what it covers, measures the round trip, marks gaps to send again.</summary>
    public void OnAck(ReadOnlySpan<byte> packet, TimeSpan now)
    {
        if (packet.Length < ReliableReceiver.AckSize || packet[0] != (byte)PacketKind.Ack) return;
        uint next = BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]);
        ulong later = BinaryPrimitives.ReadUInt64LittleEndian(packet[5..]);
        // The highest number the ack says arrived; everything below it that is still waiting was lost or is late.
        uint? highest = later == 0 ? null : next + 1 + (uint)(63 - BitOperations.LeadingZeroCount(later));

        for (int i = 0; i < _waiting.Count; i++)
        {
            var f = _waiting[i];
            int ahead = (int)(f.Sequence - next);
            bool acked = ahead < 0 || (ahead >= 1 && ahead <= 64 && (later & (1UL << (ahead - 1))) != 0);
            if (acked)
            {
                // Only fragments sent once give a clean round trip.
                if (f.Sends == 1) Measure(now - f.SentAt);
                _waiting.RemoveAt(i--);
            }
            else if (f.Sends > 0 && highest is { } h && (int)(f.Sequence - h) < 0)
            {
                f.Missing = true;
            }
        }
    }

    private static byte[] Pack(List<Fragment> due, int start, int end, int size)
    {
        var packet = new byte[size];
        packet[0] = (byte)PacketKind.Reliable;
        packet[1] = (byte)(end - start);
        int at = PacketHeaderSize;
        for (int i = start; i < end; i++)
        {
            var f = due[i];
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(at), f.Sequence);
            packet[at + 4] = f.Last ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(at + 5), (ushort)f.Data.Length);
            f.Data.CopyTo(packet, at + EntryHeaderSize);
            at += EntryHeaderSize + f.Data.Length;
        }
        return packet;
    }

    /// <summary>The wait before the next send of a fragment already sent <paramref name="sends"/> times: doubling each time.</summary>
    private TimeSpan Backoff(int sends) =>
        Clamp(ResendAfter * Math.Pow(2, Math.Min(sends - 1, 6)), MinResendAfter, MaxResendAfter);

    /// <summary>Smoothed round trip and its variation, weighted 1/8 and 1/4 for each new sample.</summary>
    private void Measure(TimeSpan sample)
    {
        if (_roundTrip is not { } rtt)
        {
            _roundTrip = sample;
            _variation = sample / 2;
            return;
        }
        _variation = 0.75 * _variation + 0.25 * (rtt - sample).Duration();
        _roundTrip = 0.875 * rtt + 0.125 * sample;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    private sealed class Fragment(uint sequence, byte[] data, bool last)
    {
        public uint Sequence { get; } = sequence;
        public byte[] Data { get; } = data;
        public bool Last { get; } = last;
        public int Sends { get; set; }
        public TimeSpan SentAt { get; set; }
        /// <summary>An ack showed later numbers arrived but not this one.</summary>
        public bool Missing { get; set; }
    }
}
