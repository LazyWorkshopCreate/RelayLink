using RelayLink.Transport;

namespace RelayLink.Agent;

internal sealed class InspectionConnectionState(AgentConnectionQuota.Lease lease, CancellationToken sessionToken, int waitMilliseconds) : IDisposable
{
    private readonly object gate = new();
    // Only the Server plugin can distinguish application activity, keepalives and
    // approval waits. The Agent keeps transport deadlines and session cancellation.
    private readonly InspectionLifetime lifetime = new(sessionToken, TimeSpan.FromMilliseconds(Math.Clamp(waitMilliseconds, 1, 900_000)), TimeSpan.Zero);
    private bool disposed;
    public CancellationToken Token => lifetime.Token;
    public void Touch() => lifetime.Touch();
    public void Activate()
    {
        lock (gate)
        {
            if (!disposed && lifetime.Activate()) lease.MarkActive();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Dispose();
        }
    }
}
