namespace RelayLink.Agent;

public sealed class AgentConnectionQuota(int maxConnections, int maxPendingConnections)
{
    private readonly object gate = new();
    private int pending;
    private int active;
    private long rejected;

    public (int Pending, int Active, long Rejected) Snapshot
    {
        get { lock (gate) return (pending, active, rejected); }
    }

    public Lease? TryAcquirePending()
    {
        lock (gate)
        {
            if (pending >= maxPendingConnections || pending + active >= maxConnections)
            {
                rejected++;
                return null;
            }
            pending++;
            return new Lease(this);
        }
    }

    private void Activate()
    {
        lock (gate) { pending--; active++; }
    }

    private void Release(bool wasActive)
    {
        lock (gate)
        {
            if (wasActive) active--;
            else pending--;
        }
    }

    public sealed class Lease(AgentConnectionQuota owner) : IDisposable
    {
        private readonly object gate = new();
        private int state; // 0=pending, 1=active, 2=released

        public void MarkActive()
        {
            lock (gate)
            {
                if (state == 0) { owner.Activate(); state = 1; }
                else if (state == 2) throw new ObjectDisposedException(nameof(Lease));
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (state == 2) return;
                owner.Release(state == 1);
                state = 2;
            }
        }
    }
}
