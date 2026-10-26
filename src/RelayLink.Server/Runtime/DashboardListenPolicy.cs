using System.Net;

namespace RelayLink.Server.Runtime;

public static class DashboardListenPolicy
{
    public static bool IsLoopback(string? listenAddress) =>
        IPAddress.TryParse(listenAddress, out var address) && IPAddress.IsLoopback(address);

    public static string NonLoopbackWarning(string listenAddress) =>
        $"SECURITY: management endpoint {listenAddress} is not a loopback address. The admin UI is plain HTTP and is not meant to be reachable from other hosts. Bind dashboard.listenAddress to 127.0.0.1 and open the admin UI through an end-to-end encrypted peer channel from an Agent installed on this server.";
}
