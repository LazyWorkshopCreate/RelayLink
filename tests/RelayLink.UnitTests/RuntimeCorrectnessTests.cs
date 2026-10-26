using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class RuntimeCorrectnessTests
{
    [Fact]
    public async Task Lowering_client_limit_rejects_the_next_acquire_until_held_drops()
    {
        var port = FreePort();
        var initial = Configuration("agent", port, maxConnections: 2);
        var runtime = new ServerRuntime(initial, new SessionRegistry());
        using var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(initial),
            new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<ProxyListenerService>.Instance);
        await service.ApplyConfigurationAsync(initial, CancellationToken.None);
        var limit = service.ClientConnectionLimit("agent");
        Assert.True(limit.TryAcquire());
        Assert.True(limit.TryAcquire());

        await service.ApplyConfigurationAsync(Configuration("agent", port, maxConnections: 1), CancellationToken.None);

        Assert.Equal(1, limit.Maximum);
        Assert.False(limit.TryAcquire());
        limit.Release();
        Assert.False(limit.TryAcquire());
        limit.Release();
        Assert.True(service.ClientConnectionLimit("agent").TryAcquire());
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Udp_peer_relays_do_not_use_the_tcp_idle_timeout()
    {
        Assert.Equal(TimeSpan.Zero, PeerRelayRegistry.IdleTimeoutFor("udp", 900));
        Assert.Equal(TimeSpan.FromSeconds(900), PeerRelayRegistry.IdleTimeoutFor("tcp", 900));
        Assert.Equal(TimeSpan.Zero, PeerRelayRegistry.IdleTimeoutFor("tcp", 0));
    }

    [Fact]
    public async Task Silent_direction_is_cancelled_by_the_idle_watch()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        using var idle = new RelayIdleWatch(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RelayPump.CopySocketToSocketAsync(client.Client, server.Client, idle.Token));
    }

    [Fact]
    public async Task Activity_defers_the_idle_watch()
    {
        using var idle = new RelayIdleWatch(TimeSpan.FromMilliseconds(400));
        var keepAlive = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(100);
                idle.Touch();
            }
        });
        var cancelled = Task.Delay(Timeout.Infinite, idle.Token);
        var elapsed = Task.Delay(500);
        Assert.Same(elapsed, await Task.WhenAny(cancelled, elapsed));
        await keepAlive;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!idle.Token.IsCancellationRequested && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(idle.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Unwritable_client_directory_keeps_the_previous_listener_and_file()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var workspace = new ConfigWorkspace();
        var updatedPort = FreePort();
        File.SetUnixFileMode(workspace.ClientsDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => workspace.Editor.UpdateAsync("agent", "echo",
                Update(updatedPort), CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(workspace.ClientsDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(workspace.Port, workspace.Runtime.Configuration.Clients["agent"].Channels[0].ListenPort);
        Assert.Contains($"\"listenPort\":{workspace.Port}", File.ReadAllText(workspace.ClientPath).Replace(" ", ""));
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, workspace.Port);
        using var rejected = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => rejected.ConnectAsync(IPAddress.Loopback, updatedPort));
        await workspace.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Failed_listener_switch_restores_the_previous_client_file()
    {
        using var workspace = new ConfigWorkspace();
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var blocked = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await Assert.ThrowsAnyAsync<Exception>(() => workspace.Editor.UpdateAsync("agent", "echo", Update(blocked), CancellationToken.None));
        Assert.Equal(workspace.Port, workspace.Runtime.Configuration.Clients["agent"].Channels[0].ListenPort);
        Assert.Contains($"\"listenPort\":{workspace.Port}", File.ReadAllText(workspace.ClientPath).Replace(" ", ""));
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, workspace.Port);
        await workspace.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_before_the_unauthenticated_slot_disposes_the_socket()
    {
        var configuration = Configuration("agent", FreePort(), 2);
        var runtime = new ServerRuntime(configuration, new SessionRegistry());
        var service = new DataAcceptorService(runtime, new PendingConnectionRegistry(),
            new PeerRelayRegistry(runtime, new MetricsRegistry(configuration), new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<PeerRelayRegistry>.Instance),
            NullLogger<DataAcceptorService>.Instance);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await listener.AcceptTcpClientAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await service.HandleAcceptedAsync(accepted, cancelled.Token);
        Assert.Null(accepted.Client);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("timeout")]
    [InlineData("closed")]
    [InlineData("write")]
    public async Task Persisted_channel_reports_a_push_failure_without_closing_a_live_session(string failure)
    {
        using var workspace = new ConfigWorkspace();
        var client = workspace.Runtime.Configuration.Clients["agent"];
        Assert.True(workspace.Runtime.Sessions.TryRegister(client, out var session));
        session!.ProtocolVersion = ProtocolConstants.Version;
        session.MarkReady();
        Stream stream = failure switch
        {
            "timeout" => new HangingStream(),
            "write" => new ThrowingStream(),
            _ => Stream.Null
        };
        session.AttachControlWriter(new FrameWriter(stream, TimeSpan.FromMilliseconds(50)));
        if (failure == "queue")
        {
            var pending = ConfigurationSnapshotFactory.Create(client, workspace.Runtime.Configuration, session.ProtocolVersion).Snapshot;
            for (var index = 0; index < 32; index++)
                await session.SendConfigurationUpdateAsync(pending, "pending-" + index, CancellationToken.None);
        }
        if (failure == "closed") session.Close();

        var (saved, push) = await workspace.Editor.UpdateAsync("agent", "echo",
            new ChannelUpdateRequest("renamed-echo", true, "127.0.0.1", workspace.Port, "127.0.0.1", 9, 2, 5), CancellationToken.None);

        Assert.Equal("renamed-echo", saved.DisplayName);
        Assert.False(push.PushedToAgent);
        Assert.Equal(failure switch
        {
            "queue" => "configuration push queue is full",
            "timeout" => "control connection write timed out",
            "write" => "control connection write failed",
            _ => "control session is closed"
        }, push.FailureReason);
        Assert.Contains("renamed-echo", await File.ReadAllTextAsync(workspace.ClientPath));
        Assert.Equal(workspace.Port, workspace.Runtime.Configuration.Clients["agent"].Channels[0].ListenPort);
        if (failure == "queue") Assert.True(session.IsReady);
        else Assert.False(session.IsReady);
        await workspace.Service.StopAsync(CancellationToken.None);
    }

    private static ChannelUpdateRequest Update(int port) =>
        new("Echo", true, "127.0.0.1", port, "127.0.0.1", 9, 2, 5);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static LoadedConfiguration Configuration(string clientId, int port, int maxConnections)
    {
        var channel = new ChannelConfiguration("echo", "Echo", true, "127.0.0.1", port, "127.0.0.1", 9, 2, 5);
        var client = new ClientConfiguration(1, clientId, "Agent", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), maxConnections, maxConnections, [channel]);
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            "clients", new LimitsConfiguration(100, 20, 10, 10, 20, 120, 300), null);
        return new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration> { [client.ClientId] = client });
    }

    private sealed class ConfigWorkspace : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("relaylink-config-").FullName;
        public int Port { get; } = FreePort();
        public ServerRuntime Runtime { get; }
        public ProxyListenerService Service { get; }
        public ChannelConfigurationEditor Editor { get; }
        public string ClientsDirectory { get; }
        public string ClientPath { get; }

        public ConfigWorkspace()
        {
            ClientsDirectory = Path.Combine(directory, "clients");
            Directory.CreateDirectory(ClientsDirectory);
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            ClientPath = Path.Combine(ClientsDirectory, "agent.json");
            File.WriteAllText(ClientPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, clientId = "agent", displayName = "Agent", enabled = true, secret,
                maxConnections = 4, maxPendingConnections = 2,
                channels = new[] { new { channelId = "echo", displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = Port, targetHost = "127.0.0.1", targetPort = 9, maxConnections = 2, targetConnectTimeoutSeconds = 5 } }
            }));
            var serverPath = Path.Combine(directory, "server.json");
            File.WriteAllText(serverPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tunnel = new { listenAddress = "127.0.0.1", port = FreePort(), dataPort = FreePort(), tlsEnabled = false, certificatePemPath = "", privateKeyPemPath = "", handshakeTimeoutSeconds = 10, heartbeatIntervalSeconds = 15, heartbeatTimeoutSeconds = 45 },
                dashboard = new { listenAddress = "127.0.0.1", port = FreePort(), refreshSeconds = 5, admin = new { username = "admin", passwordHash = "PBKDF2-SHA256$100000$AA==$AA==", sessionLifetimeMinutes = 60 } },
                clientsDirectory = ClientsDirectory,
                limits = new { maxConnections = 20, maxPendingConnections = 10, maxUnauthenticatedConnections = 10, maxChannelsPerClient = 10, openTimeoutSeconds = 10, blockedWriteTimeoutSeconds = 120, halfCloseDrainTimeoutSeconds = 30 }
            }));
            var loaded = new ConfigurationLoader().Load(serverPath);
            Runtime = new ServerRuntime(loaded, new SessionRegistry());
            Service = new ProxyListenerService(Runtime, new PendingConnectionRegistry(), new MetricsRegistry(loaded),
                new AuditService(Runtime, NullLogger<AuditService>.Instance), NullLogger<ProxyListenerService>.Instance);
            Service.ApplyConfigurationAsync(loaded, CancellationToken.None).GetAwaiter().GetResult();
            Editor = new ChannelConfigurationEditor(serverPath, new ConfigurationLoader(), Runtime, Service,
                new PeerRelayRegistry(Runtime, new MetricsRegistry(loaded), new AuditService(Runtime, NullLogger<AuditService>.Instance), NullLogger<PeerRelayRegistry>.Instance),
                Runtime.Sessions, new ConfigurationWriteLock());
        }

        public void Dispose()
        {
            try { Service.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (ObjectDisposedException) { }
            Service.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("write failed");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            throw new IOException("write failed");
    }
}
