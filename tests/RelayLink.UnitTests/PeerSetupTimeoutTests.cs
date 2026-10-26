using System.Text;
using RelayLink.Agent;
using RelayLink.Protocol;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class PeerSetupTimeoutTests
{
    [Theory]
    [InlineData(0, 0, 20)]
    [InlineData(0, 60, 60)]
    [InlineData(45_000, 10, 45)]
    [InlineData(5_000, 60, 60)]
    public void Agent_deadline_uses_the_larger_of_the_delivered_and_local_timeouts(int deliveredMilliseconds, int localSeconds, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PeerSessionCoordinator.PeerSetupDeadline(deliveredMilliseconds, localSeconds));
    }

    [Fact]
    public void Server_setup_budget_is_the_sum_of_open_and_target_connect_timeouts()
    {
        Assert.Equal(25_000, PeerRelayRegistry.PeerSetupTimeoutMilliseconds(20, 5));
        Assert.Equal(int.MaxValue, PeerRelayRegistry.PeerSetupTimeoutMilliseconds(int.MaxValue / 1000, 300));
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData(ProtocolConstants.Version)]
    public void Legacy_and_current_frames_keep_a_setup_budget_above_the_old_20_second_fallback(byte version)
    {
        var milliseconds = PeerRelayRegistry.PeerSetupTimeoutMilliseconds(30, 40);
        var granted = new PeerOpenGrantedMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "token")
        {
            SetupTimeoutMilliseconds = milliseconds
        };
        var opened = new PeerOpenMessage(Guid.NewGuid(), Guid.NewGuid(), "caller", "channel", "token")
        {
            SetupTimeoutMilliseconds = milliseconds
        };
        var restoredGrant = JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>(JsonProtocolSerializer.Serialize(granted, version));
        var restoredOpen = JsonProtocolSerializer.Deserialize<PeerOpenMessage>(JsonProtocolSerializer.Serialize(opened, version));
        var callerBudget = PeerSessionCoordinator.PeerSetupDeadline(restoredGrant.SetupTimeoutMilliseconds, 0);
        var targetBudget = PeerSessionCoordinator.PeerSetupDeadline(restoredOpen.SetupTimeoutMilliseconds, 40);
        Assert.Equal(TimeSpan.FromSeconds(70), callerBudget);
        Assert.Equal(TimeSpan.FromSeconds(70), targetBudget);
        Assert.True(callerBudget > TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_grant_without_the_setup_field_still_uses_the_20_second_caller_fallback()
    {
        var restored = JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>("""{"requestId":"11111111-1111-1111-1111-111111111111","connectionId":"22222222-2222-2222-2222-222222222222","sessionId":"33333333-3333-3333-3333-333333333333","token":"token"}"""u8);
        Assert.Equal(0, restored.SetupTimeoutMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(20), PeerSessionCoordinator.PeerSetupDeadline(restored.SetupTimeoutMilliseconds, 0));
    }
}
