namespace RelayLink.Protocol;

public enum ErrorCode
{
    AuthFailed,
    ProtocolVersionUnsupported,
    DuplicateSession,
    ConfigurationInvalid,
    ConfigurationMismatch,
    ChannelUnavailable,
    CapacityExceeded,
    OpenTimeout,
    TokenInvalid,
    TokenExpired,
    TokenAlreadyUsed,
    TargetDnsFailed,
    TargetConnectFailed,
    TargetConnectTimeout,
    ProtocolError,
    ConnectionAborted
}
