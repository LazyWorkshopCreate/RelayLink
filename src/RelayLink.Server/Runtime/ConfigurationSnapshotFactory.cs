using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class ConfigurationSnapshotFactory
{
    // A published configuration is replaced as a whole, including changes to a mapping's target.
    private static readonly ConditionalWeakTable<LoadedConfiguration, ConcurrentDictionary<(string, byte, bool), (ClientConfigSnapshot, string)>> published = new();

    public static (ClientConfigSnapshot Snapshot, string Version) Create(ClientConfiguration client, LoadedConfiguration configuration, Session session) =>
        Create(client, configuration, session.ProtocolVersion, session.MappingEncryptionStatus);

    public static (ClientConfigSnapshot Snapshot, string Version) Create(ClientConfiguration client, LoadedConfiguration? configuration = null, byte protocolVersion = ProtocolConstants.Version, bool mappingEncryptionStatus = false)
    {
        mappingEncryptionStatus &= protocolVersion >= 5;
        if (configuration is null || !ReferenceEquals(configuration.Clients.GetValueOrDefault(client.ClientId), client))
            return Build(client, configuration, protocolVersion, mappingEncryptionStatus);
        var cache = published.GetOrCreateValue(configuration);
        var key = (client.ClientId, protocolVersion, mappingEncryptionStatus);
        if (cache.TryGetValue(key, out var existing)) return existing;
        lock (cache)
            return cache.GetOrAdd(key, _ => Build(client, configuration, protocolVersion, mappingEncryptionStatus));
    }

    private static (ClientConfigSnapshot Snapshot, string Version) Build(ClientConfiguration client, LoadedConfiguration? configuration, byte protocolVersion, bool mappingEncryptionStatus)
    {
        ChannelConfiguration? Target(OutboundMappingConfiguration mapping) => configuration?.Clients.GetValueOrDefault(mapping.TargetClientId)?.Channels.SingleOrDefault(c => c.ChannelId == mapping.TargetChannelId);
        var snapshot = new ClientConfigSnapshot(
            client.ClientId,
            client.DisplayName,
            client.MaxConnections,
            client.MaxPendingConnections,
            client.Channels.OrderBy(channel => channel.ChannelId, StringComparer.Ordinal)
                .Where(channel => protocolVersion >= 5 || channel.ApplicationProtocol == "raw")
                .Where(channel => protocolVersion >= 4 || channel.TransportProtocol == "tcp")
                .Select(channel => new ChannelSnapshot(channel.ChannelId, channel.DisplayName, channel.Enabled, channel.TargetHost, channel.TargetPort, channel.MaxConnections, channel.TargetConnectTimeoutSeconds)
                {
                    ApplicationProtocol = channel.ApplicationProtocol,
                    Http = channel.Http,
                    TransportProtocol = channel.TransportProtocol,
                    UdpIdleTimeoutSeconds = channel.UdpIdleTimeoutSeconds,
                    AuthorizedClientsOnly = channel.AuthorizedClientsOnly,
                    EndToEndEncryptionEnabled = channel.EndToEndEncryptionEnabled,
                    AccessSecret = channel.AuthorizedClientsOnly ? channel.AccessSecret : null
                }).ToArray())
        {
            OutboundMappings = client.OutboundMappings.OrderBy(mapping => mapping.MappingId, StringComparer.Ordinal)
                .Where(mapping => protocolVersion >= 5 || (Target(mapping)?.ApplicationProtocol ?? "raw") == "raw")
                .Where(mapping => protocolVersion >= 4 || (Target(mapping)?.TransportProtocol ?? "tcp") == "tcp")
                .Select(mapping => new OutboundMappingSnapshot(mapping.MappingId, mapping.Enabled, mapping.LocalAddress, mapping.TargetClientId, mapping.TargetChannelId, mapping.AccessSecret, mapping.TargetCertificateSha256)
                {
                    EndToEndEncryptionEnabled = mappingEncryptionStatus ? Target(mapping)?.EndToEndEncryptionEnabled : null,
                    ApplicationProtocol = Target(mapping)?.ApplicationProtocol ?? "raw",
                    Http = Target(mapping) is { Http: {} http } target ? new HttpPeerOptions(http.PeerEntryScheme, http.PeerAuthorityHost, HttpProxyPolicy.Hash(ToHttpSnapshot(target))) : null,
                    TransportProtocol = Target(mapping)?.TransportProtocol ?? "tcp",
                    UdpIdleTimeoutSeconds = Target(mapping)?.UdpIdleTimeoutSeconds ?? 60
                }).ToArray()
        };
        return (snapshot, ConfigurationSnapshotHasher.Compute(snapshot, protocolVersion));
    }
    public static ChannelSnapshot ToHttpSnapshot(ChannelConfiguration channel) => new(channel.ChannelId, channel.DisplayName, channel.Enabled, channel.TargetHost, channel.TargetPort, channel.MaxConnections, channel.TargetConnectTimeoutSeconds)
    { ApplicationProtocol = channel.ApplicationProtocol, Http = channel.Http, AuthorizedClientsOnly = channel.AuthorizedClientsOnly, EndToEndEncryptionEnabled = channel.EndToEndEncryptionEnabled };
}
