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
