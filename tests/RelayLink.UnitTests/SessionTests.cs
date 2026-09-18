using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

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
}
