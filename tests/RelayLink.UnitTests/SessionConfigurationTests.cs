using RelayLink.Protocol;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class SessionConfigurationTests
{
    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public async Task Consecutive_updates_accept_each_ack_in_wire_order(byte protocolVersion)
    {
        await using var stream = new MemoryStream();
        var session = new Session(Guid.NewGuid(), "agent", DateTimeOffset.UtcNow) { ProtocolVersion = protocolVersion };
        session.AttachControlWriter(new FrameWriter(stream, protocolVersion: protocolVersion));
        var snapshot = new ClientConfigSnapshot("agent", "Agent", 10, 5, []);
        await session.SendConfigurationUpdateAsync(snapshot, "first", CancellationToken.None);
        await session.SendConfigurationUpdateAsync(snapshot, "second", CancellationToken.None);
        Assert.Throws<ProtocolException>(() => session.ConfirmConfiguration("second"));
        session.ConfirmConfiguration("first");
        Assert.Equal("first", session.ConfigVersion);
        session.ConfirmConfiguration("second");
        Assert.Equal("second", session.ConfigVersion);
        Assert.Throws<ProtocolException>(() => session.ConfirmConfiguration("second"));
    }

    [Fact]
    public async Task Unacknowledged_updates_are_bounded_and_overflow_closes_session()
    {
        var session = new Session(Guid.NewGuid(), "agent", DateTimeOffset.UtcNow);
        session.AttachControlWriter(new FrameWriter(Stream.Null));
        session.MarkReady();
        var snapshot = new ClientConfigSnapshot("agent", "Agent", 10, 5, []);
        for (var index = 0; index < 32; index++)
            await session.SendConfigurationUpdateAsync(snapshot, index.ToString(), CancellationToken.None);
        await Assert.ThrowsAsync<ProtocolException>(async () =>
            await session.SendConfigurationUpdateAsync(snapshot, "overflow", CancellationToken.None));
        Assert.False(session.IsReady);
        Assert.True(session.LifetimeToken.IsCancellationRequested);
    }
}
