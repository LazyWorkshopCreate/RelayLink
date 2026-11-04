using System.Diagnostics;

namespace RelayLink.Transport;

// Cancels Token when no Touch has arrived for the idle interval. A non-positive interval disables the watch.
public sealed class RelayIdleWatch : IDisposable
{
    private readonly CancellationTokenSource owned = new();
    private readonly CancellationTokenSource? linked;
    private long lastActivity = Stopwatch.GetTimestamp();
    private readonly Task watcher = Task.CompletedTask;
    private int disposed;
    private int active;
    private int suspensions;

    public RelayIdleWatch(TimeSpan idleTimeout, CancellationToken parent = default, bool initiallyActive = true)
    {
        active = initiallyActive ? 1 : 0;
        if (idleTimeout <= TimeSpan.Zero)
        {
            Token = parent;
            return;
        }

        linked = CancellationTokenSource.CreateLinkedTokenSource(parent, owned.Token);
        Token = linked.Token;
        watcher = WatchAsync(idleTimeout);
    }

    public CancellationToken Token { get; }

    public void Touch() => Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());

    public void Activate() { Touch(); Volatile.Write(ref active, 1); }

    public IDisposable Suspend()
    {
        Interlocked.Increment(ref suspensions);
        return new Suspension(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        try { owned.Cancel(); }
        catch (ObjectDisposedException) { }
        try { watcher.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        linked?.Dispose();
        owned.Dispose();
    }

    private async Task WatchAsync(TimeSpan idleTimeout)
    {
        try
        {
            while (!Token.IsCancellationRequested)
            {
                if (Volatile.Read(ref active) == 0 || Volatile.Read(ref suspensions) > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), Token);
                    continue;
                }
                var remaining = idleTimeout - Stopwatch.GetElapsedTime(Volatile.Read(ref lastActivity));
                if (remaining <= TimeSpan.Zero)
                {
                    try { owned.Cancel(); }
                    catch (ObjectDisposedException) { }
                    return;
                }

                var slice = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
                await Task.Delay(slice, Token);
            }
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
    }

    private sealed class Suspension(RelayIdleWatch owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.Touch();
            Interlocked.Decrement(ref owner.suspensions);
        }
    }
}
