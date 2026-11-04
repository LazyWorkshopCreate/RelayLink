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

    [Fact]
    public void Public_channel_reasons_cover_offline_tls_and_legacy()
    {
        var jump = Channel("jump", "tcp") with { InspectionMode = "server-jump" };
        var registry = new SessionRegistry();
        Assert.Equal("agent_offline", ChannelAvailability.PublicChannelUnavailableReason(jump, null));
        Assert.True(registry.TryRegister(Client(jump), out var session));
        Assert.Equal("agent_not_ready", ChannelAvailability.PublicChannelUnavailableReason(jump, session));
        session.MarkReady();
        Assert.Null(ChannelAvailability.PublicChannelUnavailableReason(jump, session));
        Assert.Equal(
            "control_tls_disabled",
            ChannelAvailability.PublicChannelUnavailableReason(jump, session, controlTlsEnabled: false));
        session.ProtocolVersion = 6;
        Assert.Equal("agent_protocol_legacy", ChannelAvailability.PublicChannelUnavailableReason(jump, session));
        session.ProtocolVersion = 7;
        Assert.Equal(
            "peer_only",
            ChannelAvailability.PublicChannelUnavailableReason(Channel("peer", "tcp") with { AuthorizedClientsOnly = true }, session));
        Assert.Equal(
            "channel_disabled",
            ChannelAvailability.PublicChannelUnavailableReason(jump with { Enabled = false }, session));
        Assert.Equal(
            "http_not_ready",
            ChannelAvailability.PublicChannelUnavailableReason(Channel("web", "tcp") with { ApplicationProtocol = "http" }, session));
        session.ProtocolVersion = 3;
        Assert.Equal(
            "transport_unsupported",
            ChannelAvailability.PublicChannelUnavailableReason(Channel("udp", "udp"), session));
    }

    [Fact]
    public void Mapping_reasons_explain_each_precondition_and_match_availability()
    {
        var mapping = new OutboundMappingConfiguration("visited-private", true, "127.0.0.1", "visited", "private", Convert.ToBase64String(new byte[32]), new string('a', 64));
        var address = new PeerMappingAddress(mapping.MappingId, "127.0.0.1", 20000);
        var peer = Channel("private", "tcp") with { InspectionMode = "server-peer", AuthorizedClientsOnly = true };
        var caller = new Session(Guid.NewGuid(), "caller", DateTimeOffset.UtcNow);
        var targetSession = new Session(Guid.NewGuid(), "visited", DateTimeOffset.UtcNow);

        Assert.Equal("mapping_disabled", MappingReason(mapping with { Enabled = false }, address, peer, caller, targetSession));
        Assert.Equal("address_not_reported", MappingReason(mapping, null, peer, caller, targetSession));
        Assert.Equal("caller_offline", MappingReason(mapping, address, peer, null, targetSession));
        Assert.Equal("caller_not_ready", MappingReason(mapping, address, peer, caller, targetSession));
        caller.MarkReady();
        Assert.Equal("target_offline", MappingReason(mapping, address, peer, caller, null));
        Assert.Equal("target_not_ready", MappingReason(mapping, address, peer, caller, targetSession));
        targetSession.MarkReady();
        Assert.Equal(
            "control_tls_disabled",
            ChannelAvailability.OutboundMappingUnavailableReason(
                mapping, address, peer, caller, targetSession, controlTlsEnabled: false));
        Assert.Equal(
            "target_channel_disabled",
            MappingReason(mapping, address, peer with { Enabled = false }, caller, targetSession));
        Assert.Equal(
            "target_not_peer_mode",
            MappingReason(mapping, address, peer with { AuthorizedClientsOnly = false }, caller, targetSession));
        targetSession.ProtocolVersion = 6;
        Assert.Equal("target_protocol_legacy", MappingReason(mapping, address, peer, caller, targetSession));
        targetSession.ProtocolVersion = 7;
        Assert.Null(MappingReason(mapping, address, peer, caller, targetSession));
        Assert.True(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, peer, caller, targetSession));

        var http = Channel("web", "tcp") with { ApplicationProtocol = "http" };
        Assert.Equal("target_offline", MappingReason(mapping, address, http, caller, null));
        Assert.Equal("http_not_ready", MappingReason(mapping, address, http, caller, targetSession));
    }

    private static string? MappingReason(
        OutboundMappingConfiguration mapping,
        PeerMappingAddress? address,
        ChannelConfiguration? target,
        Session? caller,
        Session? targetSession) =>
        ChannelAvailability.OutboundMappingUnavailableReason(mapping, address, target, caller, targetSession);

    private static ChannelConfiguration Channel(string id, string transport) =>
        new(id, id, true, "127.0.0.1", 9, "127.0.0.1", 9, 2, 5) { TransportProtocol = transport };

    private static ClientConfiguration Client(params ChannelConfiguration[] channels) =>
        new(1, "agent", "Agent", true, Convert.ToBase64String(new byte[32]), 4, 2, channels);
}
