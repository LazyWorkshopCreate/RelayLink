using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RelayLink.Agent;

public sealed record AgentProcessConfiguration
{
    public int DashboardPort { get; init; } = 18081;
    public int OutboundPortRangeStart { get; init; } = 20000;
    public int OutboundPortRangeEnd { get; init; } = 59999;
    public int MaxConnections { get; init; } = 2000;
    public IReadOnlyList<AgentServerProfile> Servers { get; init; } = [];
}

public sealed record AgentServerProfile
{
    public string ProfileId { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string ServerHost { get; init; } = string.Empty;
    public int ServerPort { get; init; }
    public int DataPort { get; init; }
    public bool UseTls { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
    public string? TrustedCaPemBase64 { get; init; }
    public int MaxConnections { get; init; } = 100;
    public int MaxPendingConnections { get; init; } = 20;
    public ReconnectConfiguration? Reconnect { get; init; }
}

public static class AgentProcessConfigurationLoader
{
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AgentProcessConfiguration Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return Parse(document.RootElement);
        }
        catch (JsonException) { throw new AgentConfigurationException("Invalid Agent JSON configuration."); }
        catch (IOException) { throw new AgentConfigurationException("Unable to read Agent configuration."); }
        catch (UnauthorizedAccessException) { throw new AgentConfigurationException("Unable to read Agent configuration."); }
    }

    public static AgentConfiguration CreateRuntimeConfiguration(AgentProcessConfiguration process, AgentServerProfile profile, string configurationPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))!;
        var state = Path.Combine(directory, "state", profile.ProfileId);
        return new AgentConfiguration(profile.ServerHost, profile.ServerPort, profile.ClientId, profile.Secret, profile.UseTls, null, profile.Reconnect!)
        {
            ProfileId = profile.ProfileId,
            DataPort = profile.DataPort,
            TrustedCaPemBase64 = profile.TrustedCaPemBase64,
            MaxConnections = profile.MaxConnections,
            MaxPendingConnections = profile.MaxPendingConnections,
            DashboardPort = process.DashboardPort,
            OutboundPortRangeStart = process.OutboundPortRangeStart,
            OutboundPortRangeEnd = process.OutboundPortRangeEnd,
            E2eIdentityPath = Path.Combine(state, "identity.pfx"),
            PortStatePath = Path.Combine(state, "ports.json")
        };
    }

    public static AgentProcessConfiguration Parse(JsonElement root)
    {
        RejectDuplicates(root);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            throw new AgentConfigurationException("Agent configuration must contain a servers array; run the config migrator for legacy input.");
        AgentProcessConfiguration configuration;
        try { configuration = root.Deserialize<AgentProcessConfiguration>(Options) ?? throw new AgentConfigurationException("Agent configuration is empty."); }
        catch (JsonException) { throw new AgentConfigurationException("Agent configuration contains unknown or invalid fields."); }
        Validate(configuration);
        return configuration;
    }

    public static void Validate(AgentProcessConfiguration configuration)
    {
        if (configuration.DashboardPort is < 0 or > 65535 ||
            configuration.OutboundPortRangeStart is < 1024 or > 65535 ||
            configuration.OutboundPortRangeEnd < configuration.OutboundPortRangeStart ||
            configuration.OutboundPortRangeEnd > 65535 ||
            configuration.MaxConnections is < 1 or > 100000 || configuration.Servers is null)
            throw new AgentConfigurationException("Agent process settings contain invalid values.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        long allocated = 0;
        foreach (var server in configuration.Servers)
        {
            if (server is null || !Identifier.IsMatch(server.ProfileId ?? string.Empty) ||
                !identities.Add(server.ProfileId!) ||
                !Identifier.IsMatch(server.ClientId ?? string.Empty) ||
                string.IsNullOrWhiteSpace(server.ServerHost) || server.ServerHost != server.ServerHost.Trim() ||
                server.ServerPort is < 1 or > 65535 || server.DataPort is < 1 or > 65535 ||
                server.DataPort == server.ServerPort ||
                server.MaxConnections is < 1 or > 100000 ||
                server.MaxPendingConnections is < 1 or > 100000 ||
                server.MaxPendingConnections > server.MaxConnections ||
                server.Reconnect is null || server.Reconnect.InitialDelaySeconds is < 1 or > 60 ||
                server.Reconnect.MaxDelaySeconds < server.Reconnect.InitialDelaySeconds ||
                server.Reconnect.MaxDelaySeconds > 300 ||
                server.Reconnect.PermanentErrorDelaySeconds is < 1 or > 3600)
                throw new AgentConfigurationException("Agent server profile contains invalid values or a duplicate profileId.");
            allocated += server.MaxConnections;
            try
            {
                if (Convert.FromBase64String(server.Secret).Length < 32)
                    throw new AgentConfigurationException("Agent server secret must contain at least 32 bytes.");
            }
            catch (FormatException) { throw new AgentConfigurationException("Agent server secret must be Base64."); }

            if (server.UseTls)
            {
                if (string.IsNullOrWhiteSpace(server.TrustedCaPemBase64))
                    throw new AgentConfigurationException("A private trustedCaPemBase64 is required for each TLS server profile.");
                var trust = new AgentConfiguration(server.ServerHost, server.ServerPort, server.ClientId!, server.Secret, true, null, server.Reconnect)
                { TrustedCaPemBase64 = server.TrustedCaPemBase64 };
                foreach (var certificate in AgentConfigurationLoader.LoadTrustedRoots(trust).Cast<X509Certificate2>()) certificate.Dispose();
            }
            else if (server.TrustedCaPemBase64 is not null)
                throw new AgentConfigurationException("trustedCaPemBase64 is only valid when useTls is true.");
        }
        if (allocated > configuration.MaxConnections)
            throw new AgentConfigurationException("Server profile connection limits exceed the Agent process limit.");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new AgentConfigurationException("Agent configuration contains duplicate JSON fields.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
