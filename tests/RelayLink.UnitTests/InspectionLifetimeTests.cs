using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class InspectionLifetimeTests
{
    [Fact]
    public async Task Active_approval_suspends_business_idle_without_restarting_admission_timeout()
    {
        using var lifecycle = new InspectionLifetime(CancellationToken.None, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100));
        Assert.True(lifecycle.Activate());
        using (lifecycle.SuspendIdle())
        {
            await Task.Delay(300);
            Assert.False(lifecycle.Token.IsCancellationRequested);
        }
        await Task.Delay(250);
        Assert.True(lifecycle.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Pending_wait_does_not_use_active_idle_timeout()
    {
        using var lifecycle = new InspectionLifetime(CancellationToken.None, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
        await Task.Delay(250);
        Assert.False(lifecycle.Token.IsCancellationRequested);
        Assert.True(lifecycle.Activate());
        await Task.Delay(250);
        Assert.True(lifecycle.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Stream_blocked_write_is_bounded_when_idle_is_disabled()
    {
        using var source = new MemoryStream(new byte[] { 1 });
        using var destination = new BlockedWriteStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RelayPump.CopyStreamToStreamAsync(source, destination,
            CancellationToken.None, blockedWriteTimeout: TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Fault_in_one_direction_aborts_and_observes_the_other()
    {
        using var lifetime = new CancellationTokenSource();
        var other = Task.Delay(Timeout.Infinite, lifetime.Token);
        var aborts = 0;
        await Assert.ThrowsAsync<IOException>(() => RelayPump.CompleteBidirectionalAsync(Task.FromException(new IOException("test")), other,
            () => { Interlocked.Increment(ref aborts); lifetime.Cancel(); }, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, aborts);
        Assert.True(other.IsCanceled);
    }

    private sealed class BlockedWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
