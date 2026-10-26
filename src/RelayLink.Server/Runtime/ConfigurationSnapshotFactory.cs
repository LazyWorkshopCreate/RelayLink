using RelayLink.Protocol;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class ConfigurationSnapshotFactory
{
    public static (ClientConfigSnapshot Snapshot, string Version) Create(ClientConfiguration client, LoadedConfiguration? configuration = null, byte protocolVersion = ProtocolConstants.Version)
    {
        ChannelConfiguration? Target(OutboundMappingConfiguration mapping) => configuration?.Clients.GetValueOrDefault(mapping.TargetClientId)?.Channels.SingleOrDefault(c => c.ChannelId == mapping.TargetChannelId);
        var snapshot = new ClientConfigSnapshot(
            client.ClientId,
            client.DisplayName,
            client.MaxConnections,
            client.MaxPendingConnections,
            client.Channels.OrderBy(channel => channel.ChannelId, StringComparer.Ordinal)
                .Where(channel => protocolVersion >= 4 || channel.TransportProtocol == "tcp")
                .Select(channel => new ChannelSnapshot(channel.ChannelId, channel.DisplayName, channel.Enabled, channel.TargetHost, channel.TargetPort, channel.MaxConnections, channel.TargetConnectTimeoutSeconds)
                {
                    TransportProtocol = channel.TransportProtocol,
                    UdpIdleTimeoutSeconds = channel.UdpIdleTimeoutSeconds,
                    AuthorizedClientsOnly = channel.AuthorizedClientsOnly,
                    EndToEndEncryptionEnabled = channel.EndToEndEncryptionEnabled,
                    AccessSecret = channel.AuthorizedClientsOnly ? channel.AccessSecret : null
                }).ToArray())
        {
            OutboundMappings = client.OutboundMappings.OrderBy(mapping => mapping.MappingId, StringComparer.Ordinal)
                .Where(mapping => protocolVersion >= 4 || (Target(mapping)?.TransportProtocol ?? "tcp") == "tcp")
                .Select(mapping => new OutboundMappingSnapshot(mapping.MappingId, mapping.Enabled, mapping.LocalAddress, mapping.TargetClientId, mapping.TargetChannelId, mapping.AccessSecret, mapping.TargetCertificateSha256)
                {
                    TransportProtocol = Target(mapping)?.TransportProtocol ?? "tcp",
                    UdpIdleTimeoutSeconds = Target(mapping)?.UdpIdleTimeoutSeconds ?? 60
                }).ToArray()
        };
        return (snapshot, ConfigurationSnapshotHasher.Compute(snapshot, protocolVersion));
    }
}
