using System.Buffers.Binary;
using System.Security.Cryptography;
using Sheron.Core.Net;

namespace Sheron.Core.Security;

/// <summary>
/// Seals and opens packets for one session (TRD › Security › Sealed packet): AES-256-GCM, one key per direction,
/// the packet counter as nonce, and a replay window. Made by the handshake. <see cref="Seal"/> and
/// <see cref="TryOpen"/> may each be called from their own thread.
/// </summary>
public sealed class SecureSession : IDisposable
{
    public const int HeaderSize = 9;
    public const int TagSize = 16;
    public const int NonceSize = 12;

    /// <summary>Bytes a sealed packet adds to the plain one (within <see cref="PacketLimits.SealOverhead"/>).</summary>
    public const int Overhead = HeaderSize + TagSize;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly ReplayWindow _window = new();
    private readonly object _receiveLock = new();
    private long _sent = -1;

    /// <param name="sendKey">32 bytes; copied, so the caller may wipe it.</param>
    internal SecureSession(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey, byte[] remoteDeviceKey)
    {
        _send = new AesGcm(sendKey, TagSize);
        _receive = new AesGcm(receiveKey, TagSize);
        RemoteDeviceKey = remoteDeviceKey;
        RemoteFingerprint = KeyFingerprint.Of(remoteDeviceKey);
    }

    /// <summary>The other side's device public key, as its handshake signature proved.</summary>
    public byte[] RemoteDeviceKey { get; }

    /// <summary>What the viewer pins (<see cref="KnownKeys"/>).</summary>
    public byte[] RemoteFingerprint { get; }

    /// <summary>Packets refused: changed, replayed, too old or not sealed.</summary>
    public int Refused { get; private set; }

    /// <summary>Encrypts one plain packet (video, reliable or ack) into a sealed packet.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        if (plain.Length > PacketLimits.MaxPlain)
            throw new ArgumentException($"A plain packet of {plain.Length} bytes is over {PacketLimits.MaxPlain}.", nameof(plain));
        ulong counter = (ulong)Interlocked.Increment(ref _sent);
        var packet = new byte[Overhead + plain.Length];
        packet[0] = (byte)PacketKind.Sealed;
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(1), counter);
        Span<byte> nonce = stackalloc byte[NonceSize];
        Nonce(counter, nonce);
        _send.Encrypt(nonce, plain, packet.AsSpan(HeaderSize, plain.Length), packet.AsSpan(HeaderSize + plain.Length), packet.AsSpan(0, HeaderSize));
        return packet;
    }

    /// <summary>
    /// Opens a sealed packet. False, with nothing changed, when any byte was changed, the counter came before or is
    /// older than the replay window, or the packet is not sealed.
    /// </summary>
    public bool TryOpen(ReadOnlySpan<byte> packet, out byte[] plain)
    {
        plain = [];
        lock (_receiveLock)
        {
            if (packet.Length < Overhead || packet[0] != (byte)PacketKind.Sealed) return Refuse();
            ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(packet[1..]);
            if (!_window.IsNew(counter)) return Refuse();

            var opened = new byte[packet.Length - Overhead];
            Span<byte> nonce = stackalloc byte[NonceSize];
            Nonce(counter, nonce);
            try
            {
                _receive.Decrypt(nonce, packet.Slice(HeaderSize, opened.Length), packet[^TagSize..], opened, packet[..HeaderSize]);
            }
            catch (CryptographicException)
            {
                return Refuse();
            }
            _window.Mark(counter);
            plain = opened;
            return true;
        }
    }

    private bool Refuse()
    {
        Refused++;
        return false;
    }

    /// <summary>4 zero bytes, then the counter.</summary>
    private static void Nonce(ulong counter, Span<byte> nonce)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }
}
