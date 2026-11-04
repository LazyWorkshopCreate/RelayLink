using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class SshInspection
{
    public const int MaxChannelsPerSession = 32;
    public const int MaxInitializationRequests = 32;
    public const int MaxInitializationBytes = 64 * 1024;

    public static Task<bool> RelayAsync(Stream caller, Func<CancellationToken, Task<Stream>> openTarget, string clientId, string channelId,
        ChannelConfiguration channel, CredentialStore store, AuditService? audit, Guid connectionId, CancellationToken cancellationToken,
        Action<int>? fromCaller = null, Action<int>? fromTarget = null, Func<CancellationToken, Task>? onAuthenticated = null,
        Action? onActivity = null, TimeSpan? setupTimeout = null, TimeSpan? writeTimeout = null, TimeSpan? halfCloseTimeout = null,
        Func<CancellationToken, ValueTask<IDisposable?>>? acquireApprovalLease = null, Action<string>? onRejected = null, ILogger? logger = null) =>
        new SshInspectionSession(caller, openTarget, clientId, channelId, channel, store, audit, connectionId, cancellationToken,
            fromCaller, fromTarget, onAuthenticated, onActivity, setupTimeout ?? TimeSpan.FromSeconds(20),
            writeTimeout ?? TimeSpan.FromSeconds(20), halfCloseTimeout ?? TimeSpan.FromSeconds(10), acquireApprovalLease, onRejected, logger).RunAsync();
}
