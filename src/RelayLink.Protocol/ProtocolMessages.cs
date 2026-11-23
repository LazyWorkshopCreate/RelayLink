namespace RelayLink.Protocol;

public sealed record RegisterMessage(string ClientId, string Secret, string AgentVersion, string? E2eCertificateSha256 = null);
public sealed record RegisterAcceptedMessage(Guid SessionId, string ConfigVersion, ClientConfigSnapshot Config, int HeartbeatIntervalSeconds, int HeartbeatTimeoutSeconds)
{
    // Delivered at registration. Older peers ignore these fields. 0 disables TCP idle timeout.
    public int HalfCloseDrainTimeoutSeconds { get; init; } = 300;
    public int TcpIdleTimeoutSeconds { get; init; }
}
public sealed record ConfigAckMessage(Guid SessionId, string ConfigVersion);
public sealed record ConfigUpdateMessage(Guid SessionId, string ConfigVersion, ClientConfigSnapshot Config);
public sealed record ReadyMessage(Guid SessionId);
public sealed record PingMessage(long Sequence);
public sealed record PongMessage(long Sequence);
public sealed record OpenMessage(Guid SessionId, Guid ConnectionId, string ChannelId, string ConfigVersion, string Token, int RemainingOpenTimeoutMs, HttpIngressContext? HttpIngress = null);
public sealed record OpenFailedMessage(Guid SessionId, Guid ConnectionId, ErrorCode ErrorCode);
public sealed record CancelOpenMessage(Guid SessionId, Guid ConnectionId, ErrorCode Reason);
public sealed record BindDataMessage(Guid SessionId, Guid ConnectionId, string ChannelId, string Token);
public sealed record BindAcceptedMessage(Guid ConnectionId);
public sealed record TargetReadyMessage(Guid ConnectionId, int TargetConnectDurationMs);
public sealed record StartMessage(Guid ConnectionId);
public sealed record ErrorMessage(ErrorCode Code,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ControlRejectionReason? Reason = null);
public sealed record ResetMessage(ErrorCode Code);
public sealed record PeerOpenRequestMessage(Guid RequestId, string MappingId);
public sealed record PeerOpenGrantedMessage(Guid RequestId, Guid ConnectionId, Guid SessionId, string Token, bool EndToEndEncryptionEnabled = true, string TransportProtocol = "tcp", HttpIngressContext? HttpIngress = null)
{
    // Sum of the server open timeout and the target channel connect timeout.
    // 0 means an older peer omitted the field.
    public int SetupTimeoutMilliseconds { get; init; }
}
public sealed record PeerOpenMessage(Guid ConnectionId, Guid SessionId, string CallerClientId, string ChannelId, string Token, bool EndToEndEncryptionEnabled = true, string TransportProtocol = "tcp", HttpIngressContext? HttpIngress = null)
{
    public int SetupTimeoutMilliseconds { get; init; }
}
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

public sealed record OutboundMappingSnapshot(string MappingId, bool Enabled, string LocalAddress, string TargetClientId, string TargetChannelId, string AccessSecret, string TargetCertificateSha256)
{
    public string ApplicationProtocol { get; init; } = "raw";
    public HttpPeerOptions? Http { get; init; }
    public string TransportProtocol { get; init; } = "tcp";
    public int UdpIdleTimeoutSeconds { get; init; } = 60;
}
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
    public string ApplicationProtocol { get; init; } = "raw";
    public HttpProxyOptions? Http { get; init; }
    public string TransportProtocol { get; init; } = "tcp";
    public int UdpIdleTimeoutSeconds { get; init; } = 60;
    public bool AuthorizedClientsOnly { get; init; }
    public bool EndToEndEncryptionEnabled { get; init; } = true;
    public string? AccessSecret { get; init; }
}
