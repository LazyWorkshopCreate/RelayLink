using RelayLink.Protocol;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class ChannelAvailability
{
    public static int CountAvailablePublicChannels(IEnumerable<ClientConfiguration> clients, SessionRegistry sessions) =>
        clients.Sum(client =>
        {
            sessions.TryGet(client.ClientId, out var session);
            return client.Channels.Count(channel => IsPublicChannelAvailable(channel, session));
        });

    public static bool IsPublicChannelAvailable(ChannelConfiguration channel, Session? session) =>
        channel.Enabled &&
        !channel.AuthorizedClientsOnly &&
        session is not null &&
        SupportsTransport(channel.TransportProtocol, session.ProtocolVersion) &&
        (channel.ApplicationProtocol != "http" || session.IsHttpReady(channel));

    public static bool IsOutboundMappingAvailable(OutboundMappingConfiguration mapping, PeerMappingAddress? address, ChannelConfiguration? target, Session? caller, Session? targetSession) =>
        mapping.Enabled &&
        address is not null &&
        (target?.ApplicationProtocol != "http" || (target is not null && targetSession?.IsHttpReady(target) == true)) &&
        (target?.TransportProtocol != "udp" || (caller?.ProtocolVersion >= 4 && targetSession?.ProtocolVersion >= 4));

    public static bool SupportsTransport(string transportProtocol, byte protocolVersion) =>
        transportProtocol == "tcp" || protocolVersion >= 4;
}
