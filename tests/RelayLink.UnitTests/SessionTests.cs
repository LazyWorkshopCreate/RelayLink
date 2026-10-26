using System.Text;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class SessionTests
{
    [Fact]
    public void Removing_authenticated_control_session_cancels_its_data_lifetime()
    {
        var registry = new SessionRegistry();
        var client = new ClientConfiguration(1, "agent", "Agent", true, Convert.ToBase64String(new byte[32]), 2, 2, []);
        Assert.True(registry.TryRegister(client, out var session));
        Assert.False(session.IsReady);
        session.MarkReady();
        Assert.True(session.IsReady);
        Assert.False(session.LifetimeToken.IsCancellationRequested);
        Assert.False(registry.Remove(client.ClientId, Guid.NewGuid()));
        Assert.False(session.LifetimeToken.IsCancellationRequested);

        Assert.True(registry.Remove(client.ClientId, session.SessionId));
        Assert.True(session.LifetimeToken.IsCancellationRequested);
        Assert.False(session.IsReady);
    }

    [Fact]
    public void Duplicate_registration_abandons_the_unused_session()
    {
        var registry = new SessionRegistry();
        var client = new ClientConfiguration(1, "agent", "Agent", true, Convert.ToBase64String(new byte[32]), 2, 2, []);
        Assert.True(registry.TryRegister(client, out var active));
        Assert.False(registry.TryRegister(client, out var rejected));
        Assert.False(active.LifetimeToken.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => rejected.LifetimeToken);
        Assert.True(registry.TryGet(client.ClientId, out var current));
        Assert.Same(active, current);
    }

    [Fact]
    public async Task Heartbeat_time_and_rtt_are_published_as_one_snapshot()
    {
        const int samples = 256;
        var session = new Session(Guid.NewGuid(), "agent", DateTimeOffset.UnixEpoch);
        var mismatches = 0;
        var observed = 0;
        var writePermit = new SemaphoreSlim(1, 1);
        var readPermit = new SemaphoreSlim(0, 1);
        var reader = Task.Run(async () =>
        {
            for (var sample = 0; sample < samples; sample++)
            {
                await readPermit.WaitAsync();
                var before = session.Heartbeat;
                var utc = session.LastHeartbeatUtc;
                var rtt = session.LastHeartbeatRtt;
                var after = session.Heartbeat;
                Interlocked.Increment(ref observed);
                if (!ReferenceEquals(before, after) || before.Rtt is not { } publishedRtt ||
                    rtt is not { } value || value.Ticks != utc.Ticks || utc.Ticks != before.Utc.Ticks || value.Ticks != publishedRtt.Ticks)
                    Interlocked.Increment(ref mismatches);
                writePermit.Release();
            }
        });
        var writer = Task.Run(async () =>
        {
            for (var index = 1; index <= samples; index++)
            {
                await writePermit.WaitAsync();
                var stamp = DateTimeOffset.UnixEpoch.AddTicks(index);
                session.PublishHeartbeat(stamp, TimeSpan.FromTicks(stamp.Ticks));
                readPermit.Release();
            }
        });
        await Task.WhenAll(writer, reader);
        var finalUtc = session.LastHeartbeatUtc;
        var finalRtt = session.LastHeartbeatRtt;
        Assert.Equal(finalUtc.Ticks, finalRtt?.Ticks);
        Assert.Equal(finalUtc.Ticks, session.Heartbeat.Utc.Ticks);
        Assert.Equal(samples, Volatile.Read(ref observed));
        Assert.Equal(0, Volatile.Read(ref mismatches));
    }

    [Fact]
    public async Task Rollback_configuration_is_still_queued_before_the_previous_update_is_acknowledged()
    {
        var registry = new SessionRegistry();
        var client = new ClientConfiguration(1, "agent", "Agent", true, Convert.ToBase64String(new byte[32]), 2, 2, []);
        Assert.True(registry.TryRegister(client, out var session));
        using var buffer = new MemoryStream();
        session.AttachControlWriter(new FrameWriter(buffer));
        session.SetConfigVersion("version-a");
        var snapshot = new ClientConfigSnapshot("agent", "Agent", 2, 2, []);
        await session.SendConfigurationUpdateAsync(snapshot, "version-b", CancellationToken.None);
        Assert.Equal("version-a", session.ConfigVersion);
        Assert.Equal("version-b", session.ConfigVersionPendingOrCurrent);
        await session.SendConfigurationUpdateAsync(snapshot with { MaxConnections = 1 }, "version-a", CancellationToken.None);
        Assert.Equal("version-a", session.ConfigVersion);
        Assert.Equal("version-a", session.ConfigVersionPendingOrCurrent);
    }
}
