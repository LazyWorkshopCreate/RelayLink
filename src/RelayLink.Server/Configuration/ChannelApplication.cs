namespace RelayLink.Server.Configuration;

public static class ChannelApplication
{
    public static string? FromTags(IReadOnlyList<string>? tags)
    {
        if (tags is null) return null;
        string? found = null;
        foreach (var tag in tags)
        {
            if (!tag.StartsWith("app:", StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null || tag is not ("app:ssh" or "app:rdp" or "app:http" or "app:https"))
                throw new ConfigurationException("A channel can store only one canonical application tag: app:ssh, app:rdp, app:http, or app:https.");
            found = tag[4..];
        }
        return found;
    }

    public static string Tag(string application) => "app:" + application;

    public static void Validate(ChannelConfiguration channel, string owner, string directory)
    {
        if (channel.InspectionMode is not ("none" or "server-peer" or "server-jump"))
            throw new ConfigurationException($"Invalid inspection mode for {owner}.");
        var application = FromTags(channel.Tags);
        if (channel.TransportProtocol == "udp" && (application is not null || channel.InspectionMode != "none"))
            throw new ConfigurationException($"UDP channel {owner} cannot carry an application or inspection mode.");
        if (channel.ApplicationProtocol == "http" && (channel.InspectionMode != "none" || application is not (null or "http" or "https")))
            throw new ConfigurationException($"HTTP reverse proxy {owner} cannot use inspection mode or a non-HTTP application.");
        if (channel.Inspection is not null && (channel.InspectionMode == "none" || application is not ("ssh" or "http" or "https")))
            throw new ConfigurationException($"Inspection policy is only valid on SSH, HTTP or HTTPS inspection channels for {owner}.");
        if (channel.InspectionMode != "none") InspectionPolicy.Validate(channel, owner, directory);
        if (channel.InspectionMode == "none") return;
        if (channel.TransportProtocol != "tcp" || channel.ApplicationProtocol != "raw" || !channel.EndToEndEncryptionEnabled)
            throw new ConfigurationException($"Inspection channel {owner} must stay on raw TCP with the stored encryption flag enabled.");
        if (channel.InspectionMode == "server-peer" && (!channel.AuthorizedClientsOnly || application is not ("ssh" or "rdp" or "http" or "https")))
            throw new ConfigurationException($"Server-visible peer channel {owner} requires an authorized channel and an application.");
        if (channel.InspectionMode == "server-jump" && (channel.AuthorizedClientsOnly || application is not (null or "ssh" or "rdp" or "http" or "https")))
            throw new ConfigurationException($"Jump proxy {owner} cannot be an authorized peer channel.");
    }
}
