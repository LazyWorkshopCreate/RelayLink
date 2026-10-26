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

    public RelayIdleWatch(TimeSpan idleTimeout, CancellationToken parent = default)
    {
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
}
