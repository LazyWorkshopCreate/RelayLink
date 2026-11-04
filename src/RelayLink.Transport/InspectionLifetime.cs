namespace RelayLink.Transport;

// Before activation a bounded admission lease covers input, approval and target setup.
// Active traffic has an idle deadline, never an overall connection deadline.
public sealed class InspectionLifetime : IDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly RelayIdleWatch idle;
    private readonly object gate = new();
    private bool active;
    private bool disposed;

    public InspectionLifetime(CancellationToken parent, TimeSpan pendingTimeout, TimeSpan idleTimeout)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent);
        lifetime.CancelAfter(pendingTimeout);
        idle = new RelayIdleWatch(idleTimeout, lifetime.Token, initiallyActive: false);
    }

    public CancellationToken Token => idle.Token;
    public void Touch() => idle.Touch();
    public IDisposable SuspendIdle() => idle.Suspend();

    public bool Activate()
    {
        lock (gate)
        {
            if (disposed || active || Token.IsCancellationRequested) return false;
            active = true;
            lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
            idle.Activate();
            return true;
        }
    }

    public void Abort()
    {
        lock (gate)
        {
            if (!disposed) lifetime.Cancel();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
        }
        idle.Dispose();
        lifetime.Dispose();
    }
}
