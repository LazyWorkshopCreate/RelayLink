namespace RelayLink.Server.Runtime;

public sealed record ConnectionStatus(Guid ConnectionId, string Kind, string State, string Source, DateTimeOffset StartedAtUtc, long BytesToTarget, long BytesToCaller);
