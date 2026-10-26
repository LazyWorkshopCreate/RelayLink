using System.Net;
using System.Net.Sockets;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class ChannelPortSuggestion
{
    public const int FirstPort = 19000;

    public static int? Find(LoadedConfiguration configuration, Func<int, bool>? isBindable = null, string transportProtocol = "tcp")
    {
        if (transportProtocol is not ("tcp" or "udp")) throw new ArgumentException("Invalid transport protocol.");
        var reserved = new HashSet<int>
        {
            configuration.Server.Tunnel.Port,
            configuration.Server.Tunnel.EffectiveDataPort,
            configuration.Server.Dashboard.Port
        };
        if (transportProtocol == "udp") reserved.Clear();
        foreach (var channel in configuration.Clients.Values.SelectMany(client => client.Channels))
        {
            // A disabled ordinary channel may be enabled later; keep its port assignment distinct.
            if (!channel.AuthorizedClientsOnly && channel.TransportProtocol == transportProtocol) reserved.Add(channel.ListenPort);
        }

        isBindable ??= port => CanBindWildcardIpv4(port, transportProtocol);
        for (var port = FirstPort; port <= IPEndPoint.MaxPort; port++)
        {
            if (!reserved.Contains(port) && isBindable(port)) return port;
        }
        return null;
    }

    private static bool CanBindWildcardIpv4(int port, string transportProtocol)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, transportProtocol == "udp" ? SocketType.Dgram : SocketType.Stream, transportProtocol == "udp" ? ProtocolType.Udp : ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }
}
