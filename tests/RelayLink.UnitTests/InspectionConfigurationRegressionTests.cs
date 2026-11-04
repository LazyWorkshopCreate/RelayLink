using System.Security.Cryptography;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class InspectionConfigurationRegressionTests
{
    [Theory]
    [InlineData("App:ssh")]
    [InlineData("APP:http")]
    [InlineData("app:SSH")]
    [InlineData("app:ssh|app:rdp")]
    public void Noncanonical_or_duplicate_application_tags_are_rejected_by_the_real_file_loader(string tags)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("relaylink-app-tags-");
        try
        {
            var clients = Path.Combine(directory.FullName, "clients");
            System.IO.Directory.CreateDirectory(clients);
            var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
            var server = new ServerConfiguration(1,
                new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 5, 5, 15),
                new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
                "clients", new LimitsConfiguration(10, 10, 10, 10, 5, 10, 10), null);
            File.WriteAllText(Path.Combine(directory.FullName, "server.json"), System.Text.Json.JsonSerializer.Serialize(server, options));
            var channel = new ChannelConfiguration("ssh", "SSH", true, "127.0.0.1", 19000, "127.0.0.1", 19001, 4, 5)
                { Tags = tags.Split('|') };
            var client = new ClientConfiguration(1, "target", "Target", true,
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 4, 4, [channel]);
            File.WriteAllText(Path.Combine(clients, "target.json"), System.Text.Json.JsonSerializer.Serialize(client, options));

            var exception = Assert.Throws<ConfigurationException>(() =>
                new ConfigurationLoader().Load(Path.Combine(directory.FullName, "server.json")));

            Assert.Contains("canonical application tag", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { System.IO.Directory.Delete(directory.FullName, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("App:ssh")]
    [InlineData("APP:http")]
    [InlineData("app:SSH")]
    public void Application_prefix_variants_are_rejected_instead_of_becoming_free_tags(string tag)
    {
        Assert.Throws<ConfigurationException>(() => ChannelApplication.FromTags([tag]));
    }

    [Fact]
    public void Jump_configuration_requires_control_tls_before_it_can_be_saved()
    {
        var channel = Channel() with { InspectionMode = "server-jump", Tags = ["app:http"] };
        Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().ValidateClientUpdate(Configuration(channel)));
        new ConfigurationLoader().ValidateClientUpdate(Configuration(channel with { InspectionMode = "none" }));
    }

    [Fact]
    public void Ready_inspection_peer_mapping_exposes_its_address_and_rejects_legacy_target()
    {
        var channel = Channel() with { InspectionMode = "server-peer", AuthorizedClientsOnly = true, Tags = ["app:http"] };
        var mapping = new OutboundMappingConfiguration("to-target", true, "127.0.0.1", "target", "web", "test", new string('a', 64));
        var address = new PeerMappingAddress(mapping.MappingId, "127.0.0.1", 20000);
        var caller = new Session(Guid.NewGuid(), "caller", DateTimeOffset.UtcNow) { ProtocolVersion = ProtocolConstants.Version };
        var target = new Session(Guid.NewGuid(), "target", DateTimeOffset.UtcNow) { ProtocolVersion = ProtocolConstants.Version };
        caller.MarkReady();
        target.MarkReady();
        Assert.True(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, channel, caller, target));
        target.ProtocolVersion = 5;
        Assert.False(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, channel, caller, target));
        Assert.False(ChannelAvailability.IsOutboundMappingAvailable(mapping, null, channel, caller, target));
    }

    private static ChannelConfiguration Channel() => new("web", "Web", true, "127.0.0.1", 19000, "127.0.0.1", 19001, 4, 5);

    [Theory]
    [InlineData("legacy-caller")]
    [InlineData("legacy-target")]
    [InlineData("offline-caller")]
    [InlineData("offline-target")]
    [InlineData("disabled-target")]
    [InlineData("no-address")]
    [InlineData("no-tls")]
    [InlineData("rdp")]
    [InlineData("ssh-without-pin")]
    public void Inspection_peer_unavailable_conditions_never_advertise_an_usable_entry(string condition)
    {
        var target = Channel() with { InspectionMode = "server-peer", AuthorizedClientsOnly = true, Tags = ["app:http"] };
        var mapping = new OutboundMappingConfiguration("to-target", true, "127.0.0.1", "target", "web", "test", new string('a', 64));
        PeerMappingAddress? address = new(mapping.MappingId, "127.0.0.1", 20000);
        Session? caller = new(Guid.NewGuid(), "caller", DateTimeOffset.UtcNow) { ProtocolVersion = ProtocolConstants.Version };
        Session? remote = new(Guid.NewGuid(), "target", DateTimeOffset.UtcNow) { ProtocolVersion = ProtocolConstants.Version };
        caller.MarkReady(); remote.MarkReady();
        switch (condition)
        {
            case "legacy-caller": caller.ProtocolVersion = 6; break;
            case "legacy-target": remote.ProtocolVersion = 6; break;
            case "offline-caller": caller = null; break;
            case "offline-target": remote = null; break;
            case "disabled-target": target = target with { Enabled = false }; break;
            case "no-address": address = null; break;
            case "rdp": target = target with { Tags = ["app:rdp"] }; break;
            case "ssh-without-pin": target = target with { Tags = ["app:ssh"] }; break;
        }
        Assert.False(ChannelAvailability.IsOutboundMappingAvailable(mapping, address, target, caller, remote, condition != "no-tls"));
    }

    private static LoadedConfiguration Configuration(ChannelConfiguration channel)
    {
        var client = new ClientConfiguration(1, "target", "Target", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 4, 4, [channel]);
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 5, 5, 15),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            "clients", new LimitsConfiguration(10, 10, 10, 10, 5, 10, 10), null);
        return new(server, new Dictionary<string, ClientConfiguration> { [client.ClientId] = client });
    }
}
