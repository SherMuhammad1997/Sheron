using System.Net;
using System.Net.Sockets;

namespace Sheron.Core.Net;

/// <summary>
/// Where a PC's packets go in and out: a UDP socket, the TCP relay through the server, or the network simulator in
/// tests (TRD › Server › Direct link, relay and TCP fallback). One thread receives; sending may come from any thread.
/// </summary>
public interface IPacketTransport
{
    IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for one packet. False when none came. Throws
    /// <see cref="ObjectDisposedException"/> once the transport is closed.
    /// </summary>
    bool TryReceive(byte[] buffer, TimeSpan timeout, out int length, out IPEndPoint from);

    /// <summary>Sends one packet. A packet the network refuses is dropped, like a lost one.</summary>
    void Send(ReadOnlySpan<byte> packet, IPEndPoint to);
}

/// <summary>A UDP socket with 4 MB buffers, so a keyframe's packets fit while they are read (TRD › Connect by IP).</summary>
public sealed class UdpTransport : IPacketTransport, IDisposable
{
    public const int SocketBufferBytes = 4 << 20;

    private readonly Socket _socket;

    /// <param name="port">0: any free port.</param>
    public UdpTransport(int port = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = SocketBufferBytes,
            SendBufferSize = SocketBufferBytes,
        };
        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    public IPEndPoint LocalEndPoint { get; }

    public bool TryReceive(byte[] buffer, TimeSpan timeout, out int length, out IPEndPoint from)
    {
        length = 0;
        from = LocalEndPoint;
        if (_socket.Available == 0 && !_socket.Poll((int)Math.Min(timeout.TotalMicroseconds, int.MaxValue), SelectMode.SelectRead))
            return false;
        try
        {
            EndPoint source = new IPEndPoint(IPAddress.Any, 0);
            length = _socket.ReceiveFrom(buffer, ref source);
            from = (IPEndPoint)source;
            return true;
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
        {
            // Windows reports an ICMP "port unreachable" for an earlier packet as a reset; oversized packets are not
            // Sheron's. Neither is a packet, neither stops anything.
            return false;
        }
    }

    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        try
        {
            _socket.SendTo(packet, SocketFlags.None, to);
        }
        catch (SocketException)
        {
            // Network gone for a moment: lost like any other packet; resends and timeouts handle it.
        }
    }

    public void Dispose() => _socket.Dispose();
}
