using System.Net;

namespace Sheron.Core.Net;

/// <summary>
/// A transport that first gives back one packet already read from it, then reads on: the connector reads the
/// viewer's Hello to learn the path, and hands it to the session so the viewer need not send it again.
/// </summary>
public sealed class HeldPacketTransport(IPacketTransport inner, byte[] packet, IPEndPoint from) : IPacketTransport
{
    private byte[]? _held = packet;

    public IPacketTransport Inner { get; } = inner;

    public IPEndPoint LocalEndPoint => Inner.LocalEndPoint;

    public bool TryReceive(byte[] buffer, TimeSpan timeout, out int length, out IPEndPoint source)
    {
        if (Interlocked.Exchange(ref _held, null) is { } held)
        {
            held.CopyTo(buffer, 0);
            length = held.Length;
            source = from;
            return true;
        }
        return Inner.TryReceive(buffer, timeout, out length, out source);
    }

    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to) => Inner.Send(packet, to);
}
