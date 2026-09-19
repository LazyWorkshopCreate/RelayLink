using RelayLink.Protocol;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class ConfigurationSnapshotFactory
{
    public static (ClientConfigSnapshot Snapshot, string Version) Create(ClientConfiguration client)
    {
        var snapshot = new ClientConfigSnapshot(
            client.ClientId,
            client.DisplayName,
            client.MaxConnections,
            client.MaxPendingConnections,
            client.Channels.OrderBy(channel => channel.ChannelId, StringComparer.Ordinal)
                .Select(channel => new ChannelSnapshot(channel.ChannelId, channel.DisplayName, channel.Enabled, channel.TargetHost, channel.TargetPort, channel.MaxConnections, channel.TargetConnectTimeoutSeconds)
                {
                    AuthorizedClientsOnly = channel.AuthorizedClientsOnly,
                    AccessSecret = channel.AuthorizedClientsOnly ? channel.AccessSecret : null
                }).ToArray())
        {
            OutboundMappings = client.OutboundMappings.OrderBy(mapping => mapping.MappingId, StringComparer.Ordinal)
                .Select(mapping => new OutboundMappingSnapshot(mapping.MappingId, mapping.Enabled, mapping.LocalAddress, mapping.TargetClientId, mapping.TargetChannelId, mapping.AccessSecret, mapping.TargetCertificateSha256)).ToArray()
        };
        return (snapshot, ConfigurationSnapshotHasher.Compute(snapshot));
    }
}
