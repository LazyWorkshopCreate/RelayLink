using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class ChannelAvailabilityTests
{
    [Fact]
    public void Overview_count_uses_the_same_rules_as_a_channel_and_hides_v3_udp()
    {
        var client = Client(Channel("sql", "tcp"), Channel("echo", "udp"), Channel("peer", "tcp") with { AuthorizedClientsOnly = true });
        var registry = new SessionRegistry();
        Assert.Equal(0, ChannelAvailability.CountAvailablePublicChannels([client], registry));
        Assert.True(registry.TryRegister(client, out var session));
        session.ProtocolVersion = 3;
        Assert.False(ChannelAvailability.IsPublicChannelAvailable(client.Channels[1], session));
        Assert.True(ChannelAvailability.IsPublicChannelAvailable(client.Channels[0], session));
        Assert.False(ChannelAvailability.IsPublicChannelAvailable(client.Channels[2], session));
        Assert.Equal(1, ChannelAvailability.CountAvailablePublicChannels([client], registry));

        session.ProtocolVersion = 4;
        Assert.True(ChannelAvailability.IsPublicChannelAvailable(client.Channels[1], session));
        Assert.Equal(2, ChannelAvailability.CountAvailablePublicChannels([client], registry));

        var http = Channel("web", "tcp") with { ApplicationProtocol = "http" };
        Assert.False(ChannelAvailability.IsPublicChannelAvailable(http, session));
    }

    [Fact]
    public void Udp_mapping_requires_v4_on_both_agents()
    {
        var mapping = new OutboundMappingConfiguration("visited-private", true, "127.0.0.1", "visited", "private", Convert.ToBase64String(new byte[32]), new string('a', 64));
        var address = new PeerMappingAddress(mapping.MappingId, "127.0.0.1", 20000);
        var udp = Channel("private", "udp");
        var caller = new Session(Guid.NewGuid(), "caller", DateTimeOffset.UtcNow) { ProtocolVersion = 3 };
        var target = new Session(Guid.NewGuid(), "visited", DateTimeOffset.UtcNow) { ProtocolVersion = 4 };
        Assert.False(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, udp, caller, target));
        Assert.False(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, udp, caller, null));
        caller.ProtocolVersion = 4;
        Assert.True(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, udp, caller, target));
        Assert.True(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, Channel("private", "tcp"), new Session(Guid.NewGuid(), "caller", DateTimeOffset.UtcNow) { ProtocolVersion = 3 }, target));
        Assert.False(ChannelAvailability.IsOutboundMappingAvailable(mapping, null, udp, caller, target));
        Assert.False(ChannelAvailability.IsOutboundMappingAvailable(mapping with { Enabled = false }, address, udp, caller, target));
    }

    private static ChannelConfiguration Channel(string id, string transport) =>
        new(id, id, true, "127.0.0.1", 9, "127.0.0.1", 9, 2, 5) { TransportProtocol = transport };

    private static ClientConfiguration Client(params ChannelConfiguration[] channels) =>
        new(1, "agent", "Agent", true, Convert.ToBase64String(new byte[32]), 4, 2, channels);
}
