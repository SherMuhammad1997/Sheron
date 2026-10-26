using System.Buffers.Binary;

namespace Sheron.Core.Net;

/// <summary>
/// The receiving half of the reliable channel (TRD › Packets): puts fragments back in order, drops duplicates, hands
/// on each message when its last fragment is in, and makes the ack to send back. Used by one thread only.
/// </summary>
public sealed class ReliableReceiver
{
    public const int AckSize = 13;

    private readonly Dictionary<uint, (byte[] Data, bool Last)> _early = [];
    private readonly List<byte[]> _parts = [];
    private uint _next;

    /// <summary>Fragments that came more than once.</summary>
    public int Duplicates { get; private set; }

    /// <summary>
    /// Takes one reliable packet and adds the messages it completes, in order, to <paramref name="delivered"/>.
    /// False when the packet is not a well-formed reliable packet; then nothing changes.
    /// </summary>
    public bool OnPacket(ReadOnlySpan<byte> packet, List<byte[]> delivered)
    {
        if (packet.Length < ReliableSender.PacketHeaderSize || packet[0] != (byte)PacketKind.Reliable) return false;
        int count = packet[1];
        var entries = new List<(uint Sequence, bool Last, byte[] Data)>(count);
        int at = ReliableSender.PacketHeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (packet.Length - at < ReliableSender.EntryHeaderSize) return false;
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet[at..]);
            bool last = (packet[at + 4] & 1) != 0;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(packet[(at + 5)..]);
            at += ReliableSender.EntryHeaderSize;
            if (packet.Length - at < length) return false;
            entries.Add((sequence, last, packet.Slice(at, length).ToArray()));
            at += length;
        }
        if (count == 0 || at != packet.Length) return false;

        foreach (var (sequence, last, data) in entries)
        {
            int ahead = (int)(sequence - _next);
            if (ahead < 0 || _early.ContainsKey(sequence))
            {
                Duplicates++;
                continue;
            }
            if (ahead >= ReliableSender.Window) continue;   // outside the window: the sender never does this
            _early[sequence] = (data, last);
        }

        while (_early.Remove(_next, out var fragment))
        {
            _next++;
            _parts.Add(fragment.Data);
            if (!fragment.Last) continue;
            delivered.Add(_parts.Count == 1 ? _parts[0] : _parts.SelectMany(p => p).ToArray());
            _parts.Clear();
        }
        return true;
    }

    /// <summary>The ack for everything received so far; send it back after every reliable packet.</summary>
    public byte[] Ack()
    {
        var ack = new byte[AckSize];
        ack[0] = (byte)PacketKind.Ack;
        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(1), _next);
        ulong later = 0;
        foreach (uint sequence in _early.Keys)
        {
            int bit = (int)(sequence - _next) - 1;
            if (bit is >= 0 and < 64) later |= 1UL << bit;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(ack.AsSpan(5), later);
        return ack;
    }
}
