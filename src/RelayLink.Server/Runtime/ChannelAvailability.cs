using RelayLink.Protocol;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class ChannelAvailability
{
    public static int CountAvailablePublicChannels(IEnumerable<ClientConfiguration> clients, SessionRegistry sessions, bool controlTlsEnabled = true) =>
        clients.Sum(client =>
        {
            sessions.TryGet(client.ClientId, out var session);
            return client.Channels.Count(channel => IsPublicChannelAvailable(channel, session, controlTlsEnabled));
        });

    public static bool IsPublicChannelAvailable(ChannelConfiguration channel, Session? session, bool controlTlsEnabled = true) =>
        PublicChannelUnavailableReason(channel, session, controlTlsEnabled) is null;

    /// <summary>Why the server-side entry cannot accept connections. Null means it is available.</summary>
    public static string? PublicChannelUnavailableReason(ChannelConfiguration channel, Session? session, bool controlTlsEnabled = true)
    {
        if (!channel.Enabled) return "channel_disabled";
        if (channel.InspectionMode is not ("none" or "server-jump")) return "peer_only";
        if (channel.AuthorizedClientsOnly) return "peer_only";
        if (session is null) return "agent_offline";
        if (channel.InspectionMode == "server-jump")
        {
            if (!controlTlsEnabled) return "control_tls_disabled";
            if (!session.IsReady) return "agent_not_ready";
            if (session.ProtocolVersion < ProtocolConstants.InspectionVersion) return "agent_protocol_legacy";
            if (InspectionUnavailableReason(channel) is { } material) return material;
        }
        if (!SupportsTransport(channel.TransportProtocol, session.ProtocolVersion)) return "transport_unsupported";
        if (channel.ApplicationProtocol == "http" && !session.IsHttpReady(channel)) return "http_not_ready";
        return null;
    }

    public static bool IsOutboundMappingAvailable(
        OutboundMappingConfiguration mapping,
        PeerMappingAddress? address,
        ChannelConfiguration? target,
        Session? caller,
        Session? targetSession,
        bool controlTlsEnabled = true) =>
        OutboundMappingUnavailableReason(mapping, address, target, caller, targetSession, controlTlsEnabled) is null;

    /// <summary>Why the agent-side entry cannot accept connections. Null means it is available.</summary>
    public static string? OutboundMappingUnavailableReason(
        OutboundMappingConfiguration mapping,
        PeerMappingAddress? address,
        ChannelConfiguration? target,
        Session? caller,
        Session? targetSession,
        bool controlTlsEnabled = true)
    {
        if (!mapping.Enabled) return "mapping_disabled";
        if (address is null) return "address_not_reported";
        // Configuration loading rejects mappings without a target, so a missing target keeps the
        // historical "no inspection restriction" path instead of inventing a new runtime state.
        if (target is not null && target.InspectionMode != "none")
        {
            if (target is { InspectionMode: "server-peer", Enabled: false }) return "target_channel_disabled";
            if (target is { InspectionMode: "server-peer", AuthorizedClientsOnly: false }) return "target_not_peer_mode";
            if (target.InspectionMode != "server-peer") return "target_inspection_mode_unsupported";
            if (!controlTlsEnabled) return "control_tls_disabled";
            if (caller is null) return "caller_offline";
            if (!caller.IsReady) return "caller_not_ready";
            if (targetSession is null) return "target_offline";
            if (!targetSession.IsReady) return "target_not_ready";
            if (caller.ProtocolVersion < ProtocolConstants.InspectionVersion) return "caller_protocol_legacy";
            if (targetSession.ProtocolVersion < ProtocolConstants.InspectionVersion) return "target_protocol_legacy";
            if (InspectionUnavailableReason(target) is { } material) return material;
        }
        if (target?.ApplicationProtocol == "http")
        {
            if (targetSession is null) return "target_offline";
            if (!targetSession.IsHttpReady(target)) return "http_not_ready";
        }
        if (target?.TransportProtocol == "udp" && !(caller?.ProtocolVersion >= 4 && targetSession?.ProtocolVersion >= 4)) return "udp_unsupported";
        return null;
    }

    public static string? InspectionUnavailableReason(ChannelConfiguration? channel) => channel?.InspectionMode is null or "none" ? null :
        channel.Application switch
        {
            "rdp" => "inspection_not_supported",
            "ssh" when string.IsNullOrEmpty(channel.Inspection?.Ssh?.TargetHostKeySha256) => "target_host_key_missing",
            "https" when channel.Inspection?.Https is null => "inspection_certificate_missing",
            _ => null
        };

    public static bool SupportsTransport(string transportProtocol, byte protocolVersion) =>
        transportProtocol == "tcp" || protocolVersion >= 4;
}
