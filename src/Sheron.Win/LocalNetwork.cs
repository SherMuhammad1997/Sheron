using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sheron.Win;

/// <summary>This PC's own network addresses.</summary>
internal static class LocalNetwork
{
    /// <summary>
    /// IPv4 addresses on networks that are up. Networks with a default gateway (the Wi-Fi or cable other PCs are on)
    /// come first; virtual ones such as VirtualBox's have none and come after.
    /// </summary>
    public static IReadOnlyList<IPAddress> IPv4Addresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)];
}
