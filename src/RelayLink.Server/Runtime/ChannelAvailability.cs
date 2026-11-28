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

    public static string? PeerState(ClientConfiguration client, ChannelConfiguration channel, Session? session, bool controlTls)
    {
        if (!channel.AuthorizedClientsOnly) return null;
        if (!client.Enabled || !channel.Enabled) return "disabled";
        if (!controlTls) return "control_tls_required";
        if (string.IsNullOrEmpty(client.E2eCertificateSha256)) return "certificate_missing";
        if (session is not { IsReady: true }) return "target_offline";
        if (!SupportsTransport(channel.TransportProtocol, session.ProtocolVersion) || (channel.ApplicationProtocol == "http" && session.ProtocolVersion < 5)) return "unsupported_protocol";
        if (channel.ApplicationProtocol != "http" || session.IsHttpReady(channel)) return "ready";
        return session.HttpStates.FirstOrDefault(item => item.ChannelId == channel.ChannelId)?.State == "restart_required" ? "restart_required" : "resource_unavailable";
    }

    public static bool IsOutboundMappingAvailable(OutboundMappingConfiguration mapping, PeerMappingAddress? address, ChannelConfiguration? target, Session? caller, Session? targetSession) =>
        mapping.Enabled &&
        address is not null &&
        target is { Enabled: true } &&
        caller is not null && !caller.LifetimeToken.IsCancellationRequested &&
        targetSession is not null && !targetSession.LifetimeToken.IsCancellationRequested &&
        (target.ApplicationProtocol != "http" || targetSession.IsHttpReady(target)) &&
        (target.TransportProtocol != "udp" || (caller.ProtocolVersion >= 4 && targetSession.ProtocolVersion >= 4));

    public static bool SupportsTransport(string transportProtocol, byte protocolVersion) =>
        transportProtocol == "tcp" || protocolVersion >= 4;
}
