using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Sheron.Core.Rendezvous;

/// <summary>Byte 2 of a rendezvous message, after its length (TRD › Server › Rendezvous protocol).</summary>
public enum RendezvousType : byte
{
    Challenge = 1,
    Registered = 2,
    Found = 3,
    Incoming = 4,
    NotFound = 5,
    Pong = 6,
    /// <summary>Server → remote PC: the viewer asks for the relay (Plan step 12).</summary>
    RelayAsked = 7,
    /// <summary>Server → PC on the TCP relay: both sides have joined.</summary>
    RelayReady = 8,
    /// <summary>Server → PC on the TCP relay: one packet from the other PC.</summary>
    RelayDelivered = 9,
    Register = 10,
    Find = 11,
    Ping = 12,
    /// <summary>Viewer → server: no direct path; use the relay.</summary>
    RelayRequest = 13,
    /// <summary>PC → server: join the relay over this TLS connection (its UDP is blocked).</summary>
    RelayJoin = 14,
    /// <summary>PC on the TCP relay → server: one packet for the other PC.</summary>
    RelaySend = 15,
    /// <summary>PC → server: in a session (1) or free (0); a busy PC is not introduced to anyone (Plan step 15a).</summary>
    Busy = 16,
    /// <summary>PC → server: which of these IDs are online (the home's Recent list).</summary>
    Status = 17,
    /// <summary>Server → PC: the answer to Status.</summary>
    StatusAnswer = 18,
}

/// <summary>Byte 0 of the UDP messages around a session: with the server, and between the two PCs (Plan step 12).</summary>
public enum UdpMessage : byte
{
    AddressCheck = 0x30,
    AddressSeen = 0x31,
    RelayJoin = 0x32,
    RelayReady = 0x33,
    Punch = 0x34,
    PunchAck = 0x35,
}

/// <summary>Which end of a connection a PC is.</summary>
public enum Role : byte
{
    Viewer = 1,
    Host = 2,
}

public enum AddressKind : byte
{
    /// <summary>An address on the PC's own network.</summary>
    Lan = 1,
    /// <summary>The address the server sees: the PC's router on the internet.</summary>
    Public = 2,
}

public enum NotFoundReason : byte
{
    Unknown = 1,
    Offline = 2,
    TooManyTries = 3,
    /// <summary>That PC is in another session (PRD decision 5 Nov).</summary>
    Busy = 4,
}

/// <summary>A PC as the server's Status answer gives it.</summary>
public enum PcState : byte
{
    /// <summary>Not looked up: too many lookups this minute.</summary>
    NotChecked = 0,
    /// <summary>Not online, or no PC has that ID.</summary>
    Offline = 1,
    Online = 2,
    /// <summary>Online, in a session.</summary>
    Busy = 3,
}

/// <summary>A UDP address where a PC may be reached.</summary>
public readonly record struct PeerAddress(AddressKind Kind, IPEndPoint EndPoint);

/// <summary>
/// What the server says about the other PC of a connection: the connect ID both sides got, its ID, its device public
/// key (checked against the session handshake) and its addresses.
/// </summary>
public sealed record PeerInfo(byte[] ConnectId, uint Id, byte[] PublicKey, IReadOnlyList<PeerAddress> Addresses);

/// <summary>Framing and bodies of rendezvous messages, shared by the app and the server.</summary>
public static class RendezvousWire
{
    public const byte Version = 1;
    public const int MaxMessage = 4096;
    public const int ChallengeSize = 32;
    public const int ConnectIdSize = 16;
    public const int TokenSize = 16;
    public const int PublicKeySize = 64;
    public const int SignatureSize = 64;
    /// <summary>The 33 characters of an ID: digits, then capital letters without I, L and O (TRD › Rendezvous › IDs).</summary>
    public const string IdAlphabet = "0123456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public const int IdLength = 4;

    /// <summary>How many IDs there are: 33⁴.</summary>
    public const uint IdCount = 33 * 33 * 33 * 33;

    /// <summary>IDs of digits only, given out first: 0000–9999.</summary>
    public const int DigitIds = 10_000;

    private static readonly byte[] RegisterLabel = Encoding.ASCII.GetBytes("Sheron 1 register");

    /// <summary>What a PC signs to register: the label, then the server's challenge.</summary>
    public static byte[] RegisterSigned(ReadOnlySpan<byte> challenge) => [.. RegisterLabel, .. challenge];

    /// <summary>
    /// An ID as people read it: 4 characters, such as "4821" or "7K2A". On the wire an ID is 1 + its value in base 33,
    /// so 0 never names a PC.
    /// </summary>
    public static string Show(uint id)
    {
        if (!IsId(id)) return "–";
        uint value = id - 1;
        var text = new char[IdLength];
        for (int i = IdLength - 1; i >= 0; i--)
        {
            text[i] = IdAlphabet[(int)(value % 33)];
            value /= 33;
        }
        return new string(text);
    }

    public static bool IsId(uint id) => id is >= 1 and <= IdCount;

    /// <summary>The ID of the number <paramref name="n"/>, 0–9999, written in digits ("0042").</summary>
    public static uint DigitId(int n)
    {
        if (n is < 0 or >= DigitIds) throw new ArgumentOutOfRangeException(nameof(n));
        TryParseId(n.ToString("D4"), out uint id);
        return id;
    }

    /// <summary>Is the ID digits only (one of the first 10,000 given out)?</summary>
    public static bool IsDigitsOnly(uint id) => IsId(id) && Show(id).All(char.IsAsciiDigit);

    /// <summary>
    /// An ID as typed: 4 characters, either case, spaces and dashes ignored; O is read as 0, I and L as 1 (they are not
    /// in IDs, so a slip still finds the right PC).
    /// </summary>
    public static bool TryParseId(string text, out uint id)
    {
        id = 0;
        var chars = text.Where(c => c is not (' ' or '-')).Select(c => char.ToUpperInvariant(c) switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            var other => other,
        }).ToList();
        if (chars.Count != IdLength) return false;
        uint value = 0;
        foreach (char c in chars)
        {
            int digit = IdAlphabet.IndexOf(c);
            if (digit < 0) return false;
            value = value * 33 + (uint)digit;
        }
        id = value + 1;
        return true;
    }

    /// <summary>The pin of a server certificate: SHA-256 of its public key (SubjectPublicKeyInfo).</summary>
    public static byte[] Pin(X509Certificate certificate)
    {
        using var full = new X509Certificate2(certificate);
        return SHA256.HashData(full.PublicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Writes one message in one piece: length (type + body), type, body.</summary>
    public static async Task WriteAsync(Stream stream, RendezvousType type, ReadOnlyMemory<byte> body, CancellationToken cancel)
    {
        if (body.Length + 1 > MaxMessage) throw new ArgumentException("Message too long.", nameof(body));
        var message = new byte[3 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(message, (ushort)(body.Length + 1));
        message[2] = (byte)type;
        body.CopyTo(message.AsMemory(3));
        await stream.WriteAsync(message, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Reads one message; null when the other side closed. Throws <see cref="InvalidDataException"/> on a bad length.</summary>
    public static async Task<(RendezvousType Type, byte[] Body)?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[2];
        if (!await ReadExactly(stream, header, cancel).ConfigureAwait(false)) return null;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length is < 1 or > MaxMessage) throw new InvalidDataException($"A rendezvous message of {length} bytes.");
        var message = new byte[length];
        if (!await ReadExactly(stream, message, cancel).ConfigureAwait(false)) return null;
        return ((RendezvousType)message[0], message[1..]);
    }

    private static async Task<bool> ReadExactly(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    public static byte[] Addresses(IReadOnlyList<PeerAddress> addresses)
    {
        var bytes = new List<byte> { (byte)Math.Min(addresses.Count, byte.MaxValue) };
        foreach (var address in addresses.Take(byte.MaxValue))
        {
            var ip = address.EndPoint.Address.IsIPv4MappedToIPv6 ? address.EndPoint.Address.MapToIPv4() : address.EndPoint.Address;
            bytes.Add((byte)address.Kind);
            bytes.Add(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)4 : (byte)6);
            bytes.AddRange(ip.GetAddressBytes());
            bytes.Add((byte)(address.EndPoint.Port & 0xFF));
            bytes.Add((byte)(address.EndPoint.Port >> 8));
        }
        return [.. bytes];
    }

    /// <summary>Reads an address list from the start of <paramref name="bytes"/>; <paramref name="used"/> is its length.</summary>
    public static bool TryReadAddresses(ReadOnlySpan<byte> bytes, out List<PeerAddress> addresses, out int used)
    {
        addresses = [];
        used = 0;
        if (bytes.Length < 1) return false;
        int count = bytes[0], at = 1;
        for (int i = 0; i < count; i++)
        {
            if (bytes.Length - at < 2) return false;
            var kind = (AddressKind)bytes[at];
            int size = bytes[at + 1] switch { 4 => 4, 6 => 16, _ => -1 };
            if (size < 0 || !Enum.IsDefined(kind) || bytes.Length - at < 2 + size + 2) return false;
            var ip = new IPAddress(bytes.Slice(at + 2, size));
            int port = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 2 + size)..]);
            addresses.Add(new PeerAddress(kind, new IPEndPoint(ip, port)));
            at += 2 + size + 2;
        }
        used = at;
        return true;
    }

    /// <summary>Found and Incoming share one body: connect ID, ID, public key, addresses.</summary>
    public static byte[] Peer(PeerInfo peer)
    {
        var body = new byte[ConnectIdSize + 4 + PublicKeySize];
        peer.ConnectId.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(ConnectIdSize), peer.Id);
        peer.PublicKey.CopyTo(body, ConnectIdSize + 4);
        return [.. body, .. Addresses(peer.Addresses)];
    }

    public static bool TryReadPeer(ReadOnlySpan<byte> body, out PeerInfo? peer)
    {
        peer = null;
        const int fixedPart = ConnectIdSize + 4 + PublicKeySize;
        if (body.Length < fixedPart + 1) return false;
        if (!TryReadAddresses(body[fixedPart..], out var addresses, out int used) || fixedPart + used != body.Length) return false;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body[ConnectIdSize..]);
        if (!IsId(id)) return false;
        peer = new PeerInfo(body[..ConnectIdSize].ToArray(), id, body.Slice(ConnectIdSize + 4, PublicKeySize).ToArray(), addresses);
        return true;
    }

    /// <summary>A UDP message whose body is a 16-byte connect ID or token, with an optional role after it.</summary>
    public static byte[] Udp(UdpMessage kind, ReadOnlySpan<byte> idOrToken, Role? role = null) =>
        role is { } r ? [(byte)kind, .. idOrToken, (byte)r] : [(byte)kind, .. idOrToken];

    /// <summary>Reads a UDP message's connect ID or token (and role, when <paramref name="withRole"/>).</summary>
    public static bool TryReadUdp(ReadOnlySpan<byte> packet, UdpMessage kind, bool withRole, out byte[] idOrToken, out Role role)
    {
        idOrToken = [];
        role = default;
        int length = 1 + ConnectIdSize + (withRole ? 1 : 0);
        if (packet.Length != length || packet[0] != (byte)kind) return false;
        idOrToken = packet.Slice(1, ConnectIdSize).ToArray();
        if (withRole)
        {
            role = (Role)packet[^1];
            if (!Enum.IsDefined(role)) return false;
        }
        return true;
    }

    /// <summary>Address seen: the public address the server saw a PC's UDP packet come from.</summary>
    public static byte[] AddressSeen(IPEndPoint seen)
    {
        var addresses = Addresses([new PeerAddress(AddressKind.Public, seen)]);
        return [(byte)UdpMessage.AddressSeen, .. addresses.AsSpan(2)];
    }

    public static bool TryReadAddressSeen(ReadOnlySpan<byte> packet, out IPEndPoint? seen)
    {
        seen = null;
        if (packet.Length < 2 || packet[0] != (byte)UdpMessage.AddressSeen) return false;
        // The same layout as one entry of an address list, without the count and kind.
        byte[] asList = [1, (byte)AddressKind.Public, .. packet[1..]];
        if (!TryReadAddresses(asList, out var list, out int used) || used != asList.Length) return false;
        seen = list[0].EndPoint;
        return true;
    }

    /// <summary>The most IDs one Status may ask about.</summary>
    public const int MaxStatusIds = 10;

    /// <summary>A Status body: count, then the IDs.</summary>
    public static byte[] Status(IReadOnlyList<uint> ids)
    {
        if (ids.Count is < 1 or > MaxStatusIds) throw new ArgumentOutOfRangeException(nameof(ids));
        var body = new byte[1 + 4 * ids.Count];
        body[0] = (byte)ids.Count;
        for (int i = 0; i < ids.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1 + 4 * i), ids[i]);
        return body;
    }

    public static bool TryReadStatus(ReadOnlySpan<byte> body, out List<uint> ids)
    {
        ids = [];
        if (body.Length < 1 || body[0] is < 1 or > MaxStatusIds || body.Length != 1 + 4 * body[0]) return false;
        for (int i = 0; i < body[0]; i++) ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(body[(1 + 4 * i)..]));
        return true;
    }

    /// <summary>A Status answer body: count, then per ID the ID and its state.</summary>
    public static byte[] StatusAnswer(IReadOnlyList<(uint Id, PcState State)> states)
    {
        var body = new byte[1 + 5 * states.Count];
        body[0] = (byte)states.Count;
        for (int i = 0; i < states.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1 + 5 * i), states[i].Id);
            body[5 + 5 * i] = (byte)states[i].State;
        }
        return body;
    }

    public static bool TryReadStatusAnswer(ReadOnlySpan<byte> body, out List<(uint Id, PcState State)> states)
    {
        states = [];
        if (body.Length < 1 || body.Length != 1 + 5 * body[0]) return false;
        for (int i = 0; i < body[0]; i++)
        {
            var state = (PcState)body[5 + 5 * i];
            if (!Enum.IsDefined(state)) return false;
            states.Add((BinaryPrimitives.ReadUInt32LittleEndian(body[(1 + 5 * i)..]), state));
        }
        return true;
    }

    public static byte[] Id(uint id)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, id);
        return body;
    }

    public static bool TryReadId(ReadOnlySpan<byte> body, out uint id)
    {
        id = 0;
        if (body.Length < 4) return false;
        id = BinaryPrimitives.ReadUInt32LittleEndian(body);
        return true;
    }
}
