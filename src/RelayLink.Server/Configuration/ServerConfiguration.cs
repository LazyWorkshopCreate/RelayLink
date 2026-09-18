using System.Net;
using System.Text.Json.Serialization;

namespace RelayLink.Server.Configuration;

public sealed record ServerConfiguration(
    int SchemaVersion,
    TunnelConfiguration Tunnel,
    DashboardConfiguration Dashboard,
    string ClientsDirectory,
    LimitsConfiguration Limits,
    HistoryConfiguration? History);

public sealed record HistoryConfiguration(bool Enabled, string FilePath, int SampleIntervalSeconds, int RetentionDays);

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
    int HalfCloseDrainTimeoutSeconds);

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
    public string? AgentServerHost { get; init; }
    public string? TrustedCaPemPath { get; init; }
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
    public bool AuthorizedClientsOnly { get; init; }
    public string? AccessSecret { get; init; }
    public string? E2eCertificateSha256 { get; init; }
    [JsonIgnore]
    public IPEndPoint ListenEndPoint => new(IPAddress.Parse(ListenAddress), ListenPort);
}

public sealed record LoadedConfiguration(ServerConfiguration Server, IReadOnlyDictionary<string, ClientConfiguration> Clients);
