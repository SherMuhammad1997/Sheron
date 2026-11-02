using System.Net;

namespace Sheron.Win;

/// <summary>
/// The Sheron server the app is built with (TRD › Server › Deployment, Connect by ID): its address and the pin of its
/// certificate. Neither is secret. The settings screen (Plan step 15) can change the address.
/// </summary>
internal static class ServerConfig
{
    public const string Host = "168.138.174.180";
    public const int Port = 443;

    /// <summary>SHA-256 of the server certificate's public key, as its log printed it on 2 Nov.</summary>
    public static readonly byte[] Pin = Convert.FromBase64String("WSKEIND259+ky3mRLnRCl9IkaKc9YxIaVzsAhDs3KpQ=");

    /// <summary>Where address checks and the UDP relay go.</summary>
    public static readonly IPEndPoint UdpEndPoint = new(IPAddress.Parse(Host), Port);

    /// <summary>The path a session's packets take, for both windows: direct, relay, or TCP 443 relay.</summary>
    public static string PathOf(IPEndPoint? peer) =>
        peer is null ? "–"
        : peer.Equals(UdpEndPoint) ? "Relay through the server"
        : peer.Port == 0 && peer.Address.Equals(UdpEndPoint.Address) ? "TCP 443 relay through the server"
        : $"Direct · {peer}";
}
