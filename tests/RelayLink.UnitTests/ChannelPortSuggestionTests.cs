using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class ChannelPortSuggestionTests
{
    [Fact]
    public void Starts_at_first_cloud_port_when_it_is_available()
    {
        Assert.Equal(19000, ChannelPortSuggestion.Find(Configuration(7443, 7444, 18080, []), _ => true));
    }

    [Fact]
    public void Skips_service_ports_existing_channels_and_unbindable_ports()
    {
        var channels = new[]
        {
            Channel("disabled", 19003, enabled: false),
            Channel("private", 19004, authorized: true),
            Channel("regular", 19006)
        };
        var configuration = Configuration(19000, 19001, 19002, channels);
        var probes = new List<int>();

        var port = ChannelPortSuggestion.Find(configuration, candidate =>
        {
            probes.Add(candidate);
            return candidate != 19004;
        });

        Assert.Equal(19005, port);
        Assert.Equal(new[] { 19004, 19005 }, probes);
    }

    private static LoadedConfiguration Configuration(int tunnelPort, int dataPort, int dashboardPort, IReadOnlyList<ChannelConfiguration> channels)
    {
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", tunnelPort, false, "", "", 10, 15, 45) { DataPort = dataPort },
            new DashboardConfiguration("127.0.0.1", dashboardPort, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            "clients", new LimitsConfiguration(100, 20, 10, 10, 20, 120, 300), null);
        var client = new ClientConfiguration(1, "agent", "Agent", true, "unused", 10, 10, channels);
        return new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration> { [client.ClientId] = client });
    }

    private static ChannelConfiguration Channel(string id, int port, bool enabled = true, bool authorized = false) =>
        new(id, id, enabled, "0.0.0.0", port, "127.0.0.1", 3389, 10, 10) { AuthorizedClientsOnly = authorized };
}
