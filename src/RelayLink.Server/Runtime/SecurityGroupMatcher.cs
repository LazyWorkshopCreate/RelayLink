using System.Globalization;
using System.Net;
using System.Net.Sockets;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class SecurityGroupMatcher
{
    public static bool IsValidEntry(string? entry) => TryParse(entry, out _, out _);

    public static bool IsAllowed(ChannelConfiguration channel, ServerConfiguration server, IPAddress source)
    {
        if (channel.SecurityGroupId is null) return true;
        var group = server.SecurityGroups.SingleOrDefault(item => item.Id == channel.SecurityGroupId);
        return group is not null && group.Entries.Any(entry => Contains(entry, source));
    }

    private static bool Contains(string entry, IPAddress source)
    {
        if (!TryParse(entry, out var network, out var prefix)) return false;
        if (source.IsIPv4MappedToIPv6) source = source.MapToIPv4();
        if (network.AddressFamily != source.AddressFamily) return false;
        var networkBytes = network.GetAddressBytes();
        var sourceBytes = source.GetAddressBytes();
        var wholeBytes = prefix / 8;
        for (var index = 0; index < wholeBytes; index++)
            if (networkBytes[index] != sourceBytes[index]) return false;
        var remainder = prefix % 8;
        if (remainder == 0) return true;
        var mask = 0xff << (8 - remainder);
        return (networkBytes[wholeBytes] & mask) == (sourceBytes[wholeBytes] & mask);
    }

    private static bool TryParse(string? entry, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        if (string.IsNullOrWhiteSpace(entry) || entry.Contains('%') || entry != entry.Trim()) return false;
        var parts = entry.Split('/');
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out network!) || network.IsIPv4MappedToIPv6 ||
            (network.AddressFamily == AddressFamily.InterNetwork && parts[0] != network.ToString())) return false;
        var bits = network.GetAddressBytes().Length * 8;
        prefix = bits;
        return parts.Length == 1 ||
            (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) && prefix >= 0 && prefix <= bits);
    }
}
