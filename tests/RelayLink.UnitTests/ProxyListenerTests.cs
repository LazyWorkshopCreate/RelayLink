using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class ProxyListenerTests
{
    [Fact]
    public async Task Failed_udp_bind_keeps_existing_binding_and_tcp_can_share_its_port()
    {
        var port = FreeTcpAndUdpPort();
        var initial = Configuration("127.0.0.1", port);
        var client = initial.Clients["agent"];
        initial = initial with { Clients = new Dictionary<string, ClientConfiguration>
        { ["agent"] = client with { Channels = [client.Channels[0] with { TransportProtocol = "udp" }] } } };
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial),
            new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<ProxyListenerService>.Instance);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);
        using var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        using var competitor = new UdpClient(AddressFamily.InterNetwork);
        competitor.Client.ExclusiveAddressUse = true;
        competitor.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var occupied = ((IPEndPoint)competitor.Client.LocalEndPoint!).Port;
        var replacement = client.Channels[0] with { TransportProtocol = "udp", ListenPort = occupied };
        var updated = initial with { Clients = new Dictionary<string, ClientConfiguration>
        { ["agent"] = client with { Channels = [replacement] } } };
        await Assert.ThrowsAsync<ConfigurationException>(() => service.ApplyConfigurationAsync(updated, CancellationToken.None));
        using var probe = new UdpClient(AddressFamily.InterNetwork);
        probe.Client.ExclusiveAddressUse = true;
        Assert.Throws<SocketException>(() => probe.Client.Bind(new IPEndPoint(IPAddress.Loopback, port)));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Loopback_listener_can_change_to_wildcard_on_same_port()
    {
        var port = FreePort();
        var initial = Configuration("127.0.0.1", port);
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial), new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<ProxyListenerService>.Instance);
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
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial), new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<ProxyListenerService>.Instance);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);
        var occupiedPort = FreePort();
        using var competitor = new TcpListener(IPAddress.Any, occupiedPort);
        competitor.Start();

        await Assert.ThrowsAsync<ConfigurationException>(() => service.ApplyConfigurationAsync(Configuration("0.0.0.0", occupiedPort), CancellationToken.None));

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, port);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Unexpected_accept_loop_exit_is_logged_and_readiness_drops()
    {
        var port = FreePort();
        var initial = Configuration("127.0.0.1", port);
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        var logger = new CollectingLogger();
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial),
            new AuditService(runtime, NullLogger<AuditService>.Instance), logger);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);
        runtime.IsReady = true;
        var listener = service.ActiveTcpListener("agent", "test");
        Assert.NotNull(listener);
        listener.Stop();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.UnexpectedListenerFailures == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);

        Assert.Equal(1, service.UnexpectedListenerFailures);
        Assert.False(runtime.IsReady);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("agent/test", StringComparison.Ordinal));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reapplying_configuration_restarts_a_failed_listener_and_readiness()
    {
        var port = FreePort();
        var initial = Configuration("127.0.0.1", port);
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial),
            new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<ProxyListenerService>.Instance);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);
        Assert.True(runtime.IsReady);
        var listener = service.ActiveTcpListener("agent", "test");
        Assert.NotNull(listener);
        listener.Stop();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runtime.IsReady && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(runtime.IsReady);

        await service.ApplyConfigurationAsync(initial, CancellationToken.None);

        Assert.True(runtime.IsReady);
        using var caller = new TcpClient();
        using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await caller.ConnectAsync(IPAddress.Loopback, port, connected.Token);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Recoverable_accept_failure_retries_without_dropping_readiness()
    {
        var port = FreePort();
        var initial = Configuration("127.0.0.1", port);
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        var logger = new CollectingLogger();
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial),
            new AuditService(runtime, NullLogger<AuditService>.Instance), logger);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);
        Assert.True(runtime.IsReady);
        service.FailNextAccepts(1);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, port, budget.Token);
        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, port, budget.Token);

        // ConnectAsync returns from the listen backlog while accept and connection
        // handlers are still appending log entries. Entries is a snapshot, and the
        // wait below observes the retry line before the assertion enumerates it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("retry 1 of 3", StringComparison.Ordinal)))
            await Task.Delay(20);

        Assert.Equal(0, service.UnexpectedListenerFailures);
        Assert.True(runtime.IsReady);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("retry 1 of 3", StringComparison.Ordinal));
        await service.StopAsync(CancellationToken.None);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // UDP bind(0) on Windows can return a port that TCP is forbidden to use
    // (WSAEACCES, typically an excluded TCP range). Prove the candidate accepts
    // a loopback TCP listener before the test shares it with the UDP proxy.
    private static int FreeTcpAndUdpPort()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port;
            try { port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port; }
            finally { reservation.Dispose(); }
            try
            {
                using var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                return port;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied) { }
        }
        throw new InvalidOperationException("No loopback port could be bound by both UDP and TCP.");
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

    private sealed class CollectingLogger : ILogger<ProxyListenerService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();
        public IReadOnlyList<(LogLevel Level, string Message)> Entries => entries.ToArray();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
