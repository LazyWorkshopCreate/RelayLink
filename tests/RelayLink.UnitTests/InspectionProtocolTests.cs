using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class InspectionProtocolTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Older_snapshots_preserve_normal_channels_and_exclude_inspection(byte version)
    {
        var normal = new ChannelConfiguration("normal", "Normal", true, "127.0.0.1", 1234, "127.0.0.1", 1235, 10, 5);
        var inspected = normal with { ChannelId = "inspected", InspectionMode = "server-jump", Tags = ["app:http"] };
        var client = new ClientConfiguration(1, "test", "Test", true, "test-only", 10, 5, [normal, inspected]);
        Assert.Equal("normal", Assert.Single(ConfigurationSnapshotFactory.Create(client, protocolVersion: version).Snapshot.Channels).ChannelId);
        Assert.Equal(2, ConfigurationSnapshotFactory.Create(client).Snapshot.Channels.Count);
    }

    [Fact]
    public void Wait_budget_is_not_serialized_to_v6()
    {
        var message = new OpenMessage(Guid.NewGuid(), Guid.NewGuid(), "test", "hash", "test-token", 2000)
        { InspectionWaitTimeoutMilliseconds = 60_000 };
        Assert.DoesNotContain("inspectionWaitTimeoutMilliseconds", System.Text.Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(message, 6)));
        Assert.Contains("inspectionWaitTimeoutMilliseconds", System.Text.Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(message)));
    }
}
