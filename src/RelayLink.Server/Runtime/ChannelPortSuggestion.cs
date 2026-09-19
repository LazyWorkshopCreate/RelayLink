using System.Net;
using System.Net.Sockets;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class ChannelPortSuggestion
{
    public const int FirstPort = 19000;

    public static int? Find(LoadedConfiguration configuration, Func<int, bool>? isBindable = null)
    {
        var reserved = new HashSet<int>
        {
            configuration.Server.Tunnel.Port,
            configuration.Server.Tunnel.EffectiveDataPort,
            configuration.Server.Dashboard.Port
        };
        foreach (var channel in configuration.Clients.Values.SelectMany(client => client.Channels))
        {
            // A disabled ordinary channel may be enabled later; keep its port assignment distinct.
            if (!channel.AuthorizedClientsOnly) reserved.Add(channel.ListenPort);
        }

        isBindable ??= CanBindWildcardIpv4;
        for (var port = FirstPort; port <= IPEndPoint.MaxPort; port++)
        {
            if (!reserved.Contains(port) && isBindable(port)) return port;
        }
        return null;
    }

    private static bool CanBindWildcardIpv4(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }
}
