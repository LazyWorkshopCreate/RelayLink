using System.Security.Cryptography;

namespace RelayLink.Protocol;

public static class ConfigurationSnapshotHasher
{
    public static string Compute(ClientConfigSnapshot snapshot)
    {
        var canonical = snapshot with
        {
            Channels = snapshot.Channels.OrderBy(channel => channel.ChannelId, StringComparer.Ordinal).ToArray(),
            OutboundMappings = snapshot.OutboundMappings.OrderBy(mapping => mapping.MappingId, StringComparer.Ordinal).ToArray()
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonProtocolSerializer.Serialize(canonical)));
    }
}
