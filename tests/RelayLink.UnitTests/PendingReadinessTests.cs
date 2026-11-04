using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class PendingReadinessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_readiness_keeps_the_control_loop_alive_for_following_heartbeat(bool httpIngress)
    {
        var registry = new PendingConnectionRegistry();
        var session = new Session(Guid.NewGuid(), "target", DateTimeOffset.UtcNow);
        var pending = Create(registry, session);
        registry.Remove(pending);
        using var input = new MemoryStream();
        var writer = new FrameWriter(input);
        await writer.WriteAsync(Readiness(pending.ConnectionId, httpIngress), default);
        session.MarkPing(42, Stopwatch.GetTimestamp());
        await writer.WriteAsync(new Frame(FrameType.Pong, JsonProtocolSerializer.Serialize(new PongMessage(42))), default);
        input.Position = 0;

        await ReadControlAsync(registry, session, input);

        Assert.NotNull(session.LastHeartbeatRtt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Late_readiness_does_not_accept_unknown_ids_or_a_different_session(bool httpIngress, bool wrongSession)
    {
        var registry = new PendingConnectionRegistry();
        var session = new Session(Guid.NewGuid(), "target", DateTimeOffset.UtcNow);
        var pending = Create(registry, session);
        registry.Remove(pending);
        using var input = new MemoryStream();
        await new FrameWriter(input).WriteAsync(Readiness(wrongSession ? pending.ConnectionId : Guid.NewGuid(), httpIngress), default);
        input.Position = 0;

        await Assert.ThrowsAsync<ProtocolException>(() => ReadControlAsync(registry,
            wrongSession ? new Session(Guid.NewGuid(), "target", DateTimeOffset.UtcNow) : session, input));
    }

    [Fact]
    public void Cancelled_readiness_is_ignored_only_for_its_original_control_session()
    {
        var registry = new PendingConnectionRegistry();
        var session = new Session(Guid.NewGuid(), "target", DateTimeOffset.UtcNow);
        var pending = Create(registry, session);
        Assert.False(registry.IsRecentlyCompleted(session.SessionId, pending.ConnectionId));

        registry.Cancel(session.SessionId, pending.ConnectionId);

        Assert.False(registry.TryGet(pending.ConnectionId, out _));
        Assert.True(registry.IsRecentlyCompleted(session.SessionId, pending.ConnectionId));
        Assert.False(registry.IsRecentlyCompleted(Guid.NewGuid(), pending.ConnectionId));
        Assert.False(registry.IsRecentlyCompleted(session.SessionId, Guid.NewGuid()));
        registry.Remove(pending);
        Assert.True(registry.IsRecentlyCompleted(session.SessionId, pending.ConnectionId));
    }

    [Fact]
    public void Late_readiness_retention_is_bounded_and_does_not_override_live_connections()
    {
        var registry = new PendingConnectionRegistry();
        var session = new Session(Guid.NewGuid(), "target", DateTimeOffset.UtcNow);
        var oldest = Create(registry, session);
        registry.Remove(oldest);
        PendingConnection latest = oldest;
        for (var index = 0; index < PendingConnectionRegistry.RecentCompletionCapacity; index++)
        {
            latest = Create(registry, session);
            registry.Remove(latest);
        }
        Assert.False(registry.IsRecentlyCompleted(session.SessionId, oldest.ConnectionId));
        Assert.True(registry.IsRecentlyCompleted(session.SessionId, latest.ConnectionId));
        var replacement = Create(registry, session, latest.ConnectionId);
        Assert.False(registry.IsRecentlyCompleted(session.SessionId, replacement.ConnectionId));
        registry.Remove(replacement);
        Assert.True(registry.IsRecentlyCompleted(session.SessionId, replacement.ConnectionId));
    }

    private static PendingConnection Create(PendingConnectionRegistry registry, Session session, Guid? id = null) =>
        registry.Create(session, new ChannelConfiguration("http", "HTTP", true, "127.0.0.1", 18080,
            "127.0.0.1", 8080, 10, 5), null, TimeSpan.FromSeconds(20), id ?? Guid.NewGuid());

    private static Frame Readiness(Guid connectionId, bool httpIngress) => httpIngress
        ? new(FrameType.HttpIngressReady, JsonProtocolSerializer.Serialize(new HttpIngressReadyMessage(connectionId)))
        : new(FrameType.TargetReady, JsonProtocolSerializer.Serialize(new TargetReadyMessage(connectionId, 1)));

    private static Task ReadControlAsync(PendingConnectionRegistry registry, Session session, Stream input)
    {
        var server = new ServerConfiguration(1, new("127.0.0.1", 7443, false, "", "", 10, 15, 45),
            new("127.0.0.1", 18080, 5, new("admin", "unused", 60)), "clients", new(100, 20, 10, 10, 20, 120, 300), null);
        var loaded = new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>());
        var service = new TunnelAcceptorService(new ServerRuntime(loaded, new SessionRegistry()), null!, null!, registry,
            null!, new MetricsRegistry(loaded), null!, NullLogger<TunnelAcceptorService>.Instance);
        return (Task)typeof(TunnelAcceptorService).GetMethod("RunControlReadLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [new FrameReader(input), session, CancellationToken.None])!;
    }
}
