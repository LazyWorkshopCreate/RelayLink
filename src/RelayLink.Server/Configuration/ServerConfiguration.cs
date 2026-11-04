using System.Net;
using System.Text.Json.Serialization;

namespace RelayLink.Server.Configuration;

public sealed record ServerConfiguration(
    int SchemaVersion,
    TunnelConfiguration Tunnel,
    DashboardConfiguration Dashboard,
    string ClientsDirectory,
    LimitsConfiguration Limits,
    HistoryConfiguration? History)
{
    public IReadOnlyList<SecurityGroupConfiguration> SecurityGroups { get; init; } = [];
    public AuditConfiguration? Audit { get; init; }
}

public sealed record AuditConfiguration(string FilePath, int RetentionDays = 90);

public sealed record SecurityGroupConfiguration(string Id, string Name, IReadOnlyList<string> Entries);

public sealed record HistoryConfiguration(bool Enabled, string FilePath, int SampleIntervalSeconds = 5, int RetentionDays = 90);

public sealed record TunnelConfiguration(
    string ListenAddress,
    int Port,
    bool TlsEnabled,
    string CertificatePemPath,
    string PrivateKeyPemPath,
    int HandshakeTimeoutSeconds,
    int HeartbeatIntervalSeconds,
    int HeartbeatTimeoutSeconds)
{
    public int DataPort { get; init; }
    [JsonIgnore]
    public int EffectiveDataPort => DataPort == 0 ? Port + 1 : DataPort;
    public string? AgentServerHost { get; init; }
    public string? TrustedCaPemPath { get; init; }

    [JsonIgnore]
    public string? DefaultAgentServerHost => !string.IsNullOrWhiteSpace(AgentServerHost)
        ? AgentServerHost
        : ListenAddress is "0.0.0.0" or "::" ? null : ListenAddress;
}

public sealed record DashboardConfiguration(string ListenAddress, int Port, int RefreshSeconds, DashboardAdminConfiguration Admin);

public sealed record DashboardAdminConfiguration(string Username, string PasswordHash, int SessionLifetimeMinutes);

public sealed record LimitsConfiguration(
    int MaxConnections,
    int MaxPendingConnections,
    int MaxUnauthenticatedConnections,
    int MaxChannelsPerClient,
    int OpenTimeoutSeconds,
    int BlockedWriteTimeoutSeconds,
    int HalfCloseDrainTimeoutSeconds)
{
    // 0 disables the application idle timeout. Missing JSON uses 900 seconds.
    public int TcpIdleTimeoutSeconds { get; init; } = 900;
}

public sealed record ClientConfiguration(
    int SchemaVersion,
    string ClientId,
    string DisplayName,
    bool Enabled,
    string Secret,
    int MaxConnections,
    int MaxPendingConnections,
    IReadOnlyList<ChannelConfiguration> Channels)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? AgentServerHost { get; init; }
    public string? TrustedCaPemPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? E2eCertificateSha256 { get; init; }
    public IReadOnlyList<OutboundMappingConfiguration> OutboundMappings { get; init; } = [];
}

public sealed record OutboundMappingConfiguration(
    string MappingId,
    bool Enabled,
    string LocalAddress,
    string TargetClientId,
    string TargetChannelId,
    string AccessSecret,
    string TargetCertificateSha256)
{
    // Read old client files during migration; the server never uses this value.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LocalPort { get; init; }
}

public sealed record ChannelConfiguration(
    string ChannelId,
    string DisplayName,
    bool Enabled,
    string ListenAddress,
    int ListenPort,
    string TargetHost,
    int TargetPort,
    int MaxConnections,
    int TargetConnectTimeoutSeconds)
{
    public string ApplicationProtocol { get; init; } = "raw";
    public RelayLink.Protocol.HttpProxyOptions? Http { get; init; }
    public string TransportProtocol { get; init; } = "tcp";
    public int UdpIdleTimeoutSeconds { get; init; } = 60;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool AuthorizedClientsOnly { get; init; }
    public bool EndToEndEncryptionEnabled { get; init; } = true;
    public string InspectionMode { get; init; } = "none";
    public string? SecurityGroupId { get; init; }
    public string? AccessSecret { get; init; }
    [JsonIgnore]
    public string? Application => ChannelApplication.FromTags(Tags);

    [JsonIgnore]
    public IPEndPoint ListenEndPoint => new(IPAddress.Parse(ListenAddress), ListenPort);

    public bool HasSameRuntimeSettings(ChannelConfiguration? other) =>
        other is not null &&
        ChannelId == other.ChannelId &&
        DisplayName == other.DisplayName &&
        Enabled == other.Enabled &&
        ApplicationProtocol == other.ApplicationProtocol &&
        RelayLink.Protocol.JsonProtocolSerializer.Serialize(Http).SequenceEqual(RelayLink.Protocol.JsonProtocolSerializer.Serialize(other.Http)) &&
        TransportProtocol == other.TransportProtocol &&
        UdpIdleTimeoutSeconds == other.UdpIdleTimeoutSeconds &&
        ListenAddress == other.ListenAddress &&
        ListenPort == other.ListenPort &&
        TargetHost == other.TargetHost &&
        TargetPort == other.TargetPort &&
        MaxConnections == other.MaxConnections &&
        TargetConnectTimeoutSeconds == other.TargetConnectTimeoutSeconds &&
        AuthorizedClientsOnly == other.AuthorizedClientsOnly &&
        EndToEndEncryptionEnabled == other.EndToEndEncryptionEnabled &&
        InspectionMode == other.InspectionMode &&
        Application == other.Application &&
        InspectionPolicy.Same(Inspection, other.Inspection) &&
        SecurityGroupId == other.SecurityGroupId &&
        AccessSecret == other.AccessSecret;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InspectionPolicy? Inspection { get; init; }
    // Runtime location is deliberately excluded from the persisted channel schema.
    [JsonIgnore]
    public string ConfigurationDirectory { get; init; } = Environment.CurrentDirectory;
    [JsonIgnore]
    public InspectionPolicy? ResolvedInspection => InspectionPolicy.ResolvePaths(Inspection, ConfigurationDirectory);
}

public sealed record LoadedConfiguration(ServerConfiguration Server, IReadOnlyDictionary<string, ClientConfiguration> Clients);
