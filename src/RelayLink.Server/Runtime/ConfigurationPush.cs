using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.Server.Runtime;

public readonly record struct AgentPushReport(bool PushedToAgent, string? FailureReason)
{
    public static AgentPushReport Sent { get; } = new(true, null);
    public static AgentPushReport Offline { get; } = new(false, null);
    public static AgentPushReport Failed(string reason) => new(false, reason);

    public static AgentPushReport From(bool attempted, IReadOnlyList<string> failures, bool onlineWhenNoPush)
    {
        if (failures.Count > 0) return Failed(string.Join("; ", failures));
        if (attempted) return Sent;
        return onlineWhenNoPush ? Sent : Offline;
    }
}

internal static class ConfigurationPush
{
    public static async Task<string?> TrySendAsync(Session session, ClientConfigSnapshot snapshot, string version, CancellationToken cancellationToken)
    {
        try
        {
            await session.SendConfigurationUpdateAsync(snapshot, version, cancellationToken);
            return null;
        }
        catch (Exception exception) when (IsPushFailure(exception, cancellationToken))
        {
            return Reason(exception);
        }
    }

    private static bool IsPushFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is ProtocolException or IOException or InvalidOperationException or ObjectDisposedException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static string Reason(Exception exception)
    {
        var reported = exception is FrameWriteException { InnerException: not null } ? exception.InnerException! : exception;
        return reported switch
        {
            ProtocolException => "configuration push queue is full",
            OperationCanceledException => "control connection write timed out",
            IOException => "control connection write failed",
            _ => "control session is closed"
        };
    }
}
