using RelayLink.Agent;

namespace RelayLink.UnitTests;

public sealed class AgentConnectionQuotaTests
{
    [Fact]
    public void Pending_and_total_limits_are_shared_and_leases_release_once()
    {
        var east = new AgentConnectionQuota(2, 1);
        var west = new AgentConnectionQuota(2, 1);
        using var first = east.TryAcquirePending();
        Assert.NotNull(first);
        Assert.Null(east.TryAcquirePending());
        first.MarkActive();
        using var second = east.TryAcquirePending();
        Assert.NotNull(second);
        Assert.Null(east.TryAcquirePending());
        using var otherServer = west.TryAcquirePending();
        Assert.NotNull(otherServer);
        second.MarkActive();
        first.Dispose();
        first.Dispose();

        Assert.Equal((0, 1, 2L), east.Snapshot);
        Assert.Equal((1, 0, 0L), west.Snapshot);
        second.Dispose();
        Assert.Equal((0, 0, 2L), east.Snapshot);
    }
}
