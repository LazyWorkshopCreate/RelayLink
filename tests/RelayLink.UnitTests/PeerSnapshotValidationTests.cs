using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Agent;
using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class PeerSnapshotValidationTests
{
    [Fact]
    public async Task Invalid_outbound_snapshot_is_rejected_before_listeners_change()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-peer-validate-");
        try
        {
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var agent = new AgentConfiguration("127.0.0.1", 1, "caller", secret, false, null, new ReconnectConfiguration(1, 2, 3))
            {
                E2eIdentityPath = Path.Combine(directory.FullName, "identity.pfx"),
                PortStatePath = Path.Combine(directory.FullName, "ports.json"),
                OutboundPortRangeStart = 25000,
                OutboundPortRangeEnd = 25040
            };
            using var identity = AgentIdentity.LoadOrCreate(agent.E2eIdentityPath, agent.ClientId);
            await using var coordinator = new PeerSessionCoordinator(agent, identity, Guid.NewGuid(), new FrameWriter(Stream.Null),
                NullLogger.Instance, new AgentConnectionQuota(10, 5), CancellationToken.None);
            coordinator.ApplyConfiguration(Snapshot(Mapping("map-a", "127.0.0.1", secret)));
            var port = Assert.Single(coordinator.Addresses).LocalPort;

            var rejected = Assert.Throws<AgentPermanentException>(() => coordinator.ApplyConfiguration(Snapshot(
                Mapping("map-a", "127.0.0.1", secret),
                Mapping("map-a", "0.0.0.0", "not-base64"))));
            Assert.Contains("duplicated", rejected.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(port, Assert.Single(coordinator.Addresses).LocalPort);
            using var caller = new TcpClient();
            await caller.ConnectAsync(IPAddress.Loopback, port);

            var malformed = Assert.Throws<AgentPermanentException>(() => coordinator.ApplyConfiguration(Snapshot(Mapping("map-b", "127.0.0.1", "%%%"))));
            Assert.Contains("Base64", malformed.Message, StringComparison.Ordinal);
            Assert.Equal(port, Assert.Single(coordinator.Addresses).LocalPort);
            Assert.Contains("http mappings require tcp", PeerSessionCoordinator.InvalidOutboundMappingReason(
            [
                Mapping("map-c", "127.0.0.1", secret) with { ApplicationProtocol = "http", TransportProtocol = "udp" }
            ]), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Snapshot_client_id_must_match_the_profile_on_register_and_update()
    {
        var foreign = new ClientConfigSnapshot("other-agent", "Other", 2, 2, []);
        var version = ConfigurationSnapshotHasher.Compute(foreign);
        var registration = new RegisterAcceptedMessage(Guid.NewGuid(), version, foreign, 15, 45);
        var rejected = Assert.Throws<AgentPermanentException>(() => ControlSessionWorker.ValidateSnapshot("this-agent", registration));
        Assert.Contains("invalid configuration", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<AgentPermanentException>(() => ControlSessionWorker.ValidateSnapshot("this-agent", version, foreign));

        var own = new ClientConfigSnapshot("this-agent", "This", 2, 2, []);
        var ownVersion = ConfigurationSnapshotHasher.Compute(own);
        ControlSessionWorker.ValidateSnapshot("this-agent", new RegisterAcceptedMessage(Guid.NewGuid(), ownVersion, own, 15, 45));
        ControlSessionWorker.ValidateSnapshot("this-agent", ownVersion, own);
    }

    private static ClientConfigSnapshot Snapshot(params OutboundMappingSnapshot[] mappings) =>
        new("caller", "Caller", 10, 5, []) { OutboundMappings = mappings };

    private static OutboundMappingSnapshot Mapping(string id, string address, string secret) =>
        new(id, true, address, "target", "channel", secret, new string('A', 64));
}
