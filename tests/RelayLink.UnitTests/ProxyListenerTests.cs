using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class ProxyListenerTests
{
    [Fact]
    public async Task Loopback_listener_can_change_to_wildcard_on_same_port()
    {
        var port = FreePort();
        var initial = Configuration("127.0.0.1", port);
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial), NullLogger<ProxyListenerService>.Instance);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);

        await service.ApplyConfigurationAsync(Configuration("0.0.0.0", port), CancellationToken.None);

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Parse("127.0.0.2"), port);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Failed_bind_keeps_the_previous_listener()
    {
        var port = FreePort();
        var initial = Configuration("127.0.0.1", port);
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial), NullLogger<ProxyListenerService>.Instance);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);
        var occupiedPort = FreePort();
        using var competitor = new TcpListener(IPAddress.Any, occupiedPort);
        competitor.Start();

        await Assert.ThrowsAsync<ConfigurationException>(() => service.ApplyConfigurationAsync(Configuration("0.0.0.0", occupiedPort), CancellationToken.None));

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, port);
        await service.StopAsync(CancellationToken.None);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static LoadedConfiguration Configuration(string address, int port)
    {
        var channel = new ChannelConfiguration("test", "Test", true, address, port, "127.0.0.1", 3389, 10, 10);
        var client = new ClientConfiguration(1, "agent", "Agent", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 10, 10, [channel]);
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            "clients", new LimitsConfiguration(100, 20, 10, 10, 20, 120, 300), null);
        return new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration> { [client.ClientId] = client });
    }
}
