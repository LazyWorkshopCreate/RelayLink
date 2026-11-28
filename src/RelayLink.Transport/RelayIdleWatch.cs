namespace RelayLink.Transport;

// Cancels Token when no Touch has arrived for the idle interval. A non-positive interval disables the watch.
public sealed class RelayIdleWatch : IDisposable
{
    private readonly CancellationTokenSource owned = new();
    private readonly CancellationTokenSource? linked;
    private readonly TimeProvider timeProvider;
    private long lastActivity;
    private readonly Task watcher = Task.CompletedTask;
    private int disposed;

    public RelayIdleWatch(TimeSpan idleTimeout, CancellationToken parent = default, TimeProvider? timeProvider = null, Task? startAfter = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        lastActivity = this.timeProvider.GetTimestamp();
        if (idleTimeout <= TimeSpan.Zero)
        {
            Token = parent;
            return;
        }

        linked = CancellationTokenSource.CreateLinkedTokenSource(parent, owned.Token);
        Token = linked.Token;
        watcher = WatchAsync(idleTimeout, startAfter);
    }

    public CancellationToken Token { get; }

    public void Touch() => Interlocked.Exchange(ref lastActivity, timeProvider.GetTimestamp());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        try { owned.Cancel(); }
        catch (ObjectDisposedException) { }
        if (linked is null)
        {
            owned.Dispose();
            return;
        }
        _ = watcher.ContinueWith(_ =>
        {
            linked.Dispose();
            owned.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WatchAsync(TimeSpan idleTimeout, Task? startAfter)
    {
        try
        {
            if (startAfter is not null)
            {
                await startAfter.WaitAsync(Token);
                Touch();
            }
            while (!Token.IsCancellationRequested)
            {
                var remaining = idleTimeout - timeProvider.GetElapsedTime(Volatile.Read(ref lastActivity));
                if (remaining <= TimeSpan.Zero)
                {
                    try { owned.Cancel(); }
                    catch (ObjectDisposedException) { }
                    return;
                }

                var slice = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
                await Task.Delay(slice, timeProvider, Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
    }
}
