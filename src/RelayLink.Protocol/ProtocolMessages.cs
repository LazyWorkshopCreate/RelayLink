namespace RelayLink.Protocol;

public sealed record RegisterMessage(string ClientId, string Secret, string AgentVersion, string? E2eCertificateSha256 = null);
public sealed record RegisterAcceptedMessage(Guid SessionId, string ConfigVersion, ClientConfigSnapshot Config, int HeartbeatIntervalSeconds, int HeartbeatTimeoutSeconds);
public sealed record ConfigAckMessage(Guid SessionId, string ConfigVersion);
public sealed record ConfigUpdateMessage(Guid SessionId, string ConfigVersion, ClientConfigSnapshot Config);
public sealed record ReadyMessage(Guid SessionId);
public sealed record PingMessage(long Sequence);
public sealed record PongMessage(long Sequence);
public sealed record OpenMessage(Guid SessionId, Guid ConnectionId, string ChannelId, string ConfigVersion, string Token, int RemainingOpenTimeoutMs);
public sealed record OpenFailedMessage(Guid SessionId, Guid ConnectionId, ErrorCode ErrorCode);
public sealed record CancelOpenMessage(Guid SessionId, Guid ConnectionId, ErrorCode Reason);
public sealed record BindDataMessage(Guid SessionId, Guid ConnectionId, string ChannelId, string Token);
public sealed record BindAcceptedMessage(Guid ConnectionId);
public sealed record TargetReadyMessage(Guid ConnectionId, int TargetConnectDurationMs);
public sealed record StartMessage(Guid ConnectionId);
public sealed record ErrorMessage(ErrorCode Code);
public sealed record ResetMessage(ErrorCode Code);
public sealed record PeerOpenRequestMessage(Guid RequestId, string MappingId);
public sealed record PeerOpenGrantedMessage(Guid RequestId, Guid ConnectionId, Guid SessionId, string Token);
public sealed record PeerOpenMessage(Guid ConnectionId, Guid SessionId, string CallerClientId, string ChannelId, string Token);
public sealed record PeerBindDataMessage(Guid ConnectionId, Guid SessionId, string Token, string Role);
public sealed record PeerBindAcceptedMessage(Guid ConnectionId);
public sealed record PeerOpenRejectedMessage(Guid RequestId, ErrorCode ErrorCode);

public sealed record ClientConfigSnapshot(
    string ClientId,
    string DisplayName,
    int MaxConnections,
    int MaxPendingConnections,
    IReadOnlyList<ChannelSnapshot> Channels)
{
    public IReadOnlyList<OutboundMappingSnapshot> OutboundMappings { get; init; } = [];
}

public sealed record OutboundMappingSnapshot(string MappingId, bool Enabled, string LocalAddress, string TargetClientId, string TargetChannelId, string AccessSecret, string TargetCertificateSha256);
public sealed record PeerMappingAddress(string MappingId, string LocalAddress, int LocalPort);
public sealed record PeerMappingStatusMessage(Guid SessionId, string ConfigVersion, IReadOnlyList<PeerMappingAddress> Addresses);

public sealed record ChannelSnapshot(
    string ChannelId,
    string DisplayName,
    bool Enabled,
    string TargetHost,
    int TargetPort,
    int MaxConnections,
    int TargetConnectTimeoutSeconds)
{
    public bool AuthorizedClientsOnly { get; init; }
    public string? AccessSecret { get; init; }
    public string? E2eCertificateSha256 { get; init; }
}
