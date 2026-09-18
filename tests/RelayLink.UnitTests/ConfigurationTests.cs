using System.Net;
using System.Net.Sockets;
using RelayLink.Protocol;
using RelayLink.Agent;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Agent_port_allocator_reuses_port_and_rotates_on_conflict()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-port-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "ports.json");
            var first = new AgentPortAllocator(path, 28000, 28100);
            using var initial = first.Bind("to-private");
            var chosen = ((IPEndPoint)initial.LocalEndpoint).Port;
            initial.Stop();
            var second = new AgentPortAllocator(path, 28000, 28100);
            using var reused = second.Bind("to-private");
            Assert.Equal(chosen, ((IPEndPoint)reused.LocalEndpoint).Port);
            reused.Stop();
            using var occupied = new TcpListener(IPAddress.Loopback, chosen);
            occupied.Start();
            var third = new AgentPortAllocator(path, 28000, 28100);
            using var rotated = third.Bind("to-private");
            Assert.NotEqual(chosen, ((IPEndPoint)rotated.LocalEndpoint).Port);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Server_never_downlinks_legacy_mapping_port()
    {
        var mapping = new OutboundMappingConfiguration("to-private", true, "127.0.0.1", "visited", "private", Convert.ToBase64String(new byte[32]), new string('A', 64))
        { LocalPort = 19010 };
        var caller = new ClientConfiguration(1, "caller", "Caller", true, Convert.ToBase64String(new byte[32]), 10, 5, [])
        { OutboundMappings = [mapping] };
        var (snapshot, _) = ConfigurationSnapshotFactory.Create(caller);
        Assert.DoesNotContain("localPort", System.Text.Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(snapshot)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_hash_is_independent_of_channel_input_order()
    {
        var first = new ClientConfigSnapshot("client", "节点", 10, 5,
        [new ChannelSnapshot("z", "Z", true, "target", 1, 1, 1), new ChannelSnapshot("a", "A", true, "target", 2, 1, 1)]);
        var second = first with { Channels = first.Channels.Reverse().ToArray() };

        Assert.Equal(ConfigurationSnapshotHasher.Compute(first), ConfigurationSnapshotHasher.Compute(second));
    }

    [Fact]
    public void Configuration_loader_rejects_duplicate_properties()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "clients"));
        File.WriteAllText(Path.Combine(directory.Path, "server.json"), """{"schemaVersion":1,"schemaVersion":1}""");

        var exception = Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().Load(Path.Combine(directory.Path, "server.json")));

        Assert.Contains("Duplicate JSON property", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_loader_rejects_missing_tunnel_certificate()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "server.json");
        File.WriteAllText(path, """{"schemaVersion":1,"tunnel":{"listenAddress":"127.0.0.1","port":7443,"tlsEnabled":true,"certificatePemPath":"missing-cert.pem","privateKeyPemPath":"missing-key.pem","handshakeTimeoutSeconds":10,"heartbeatIntervalSeconds":15,"heartbeatTimeoutSeconds":45},"dashboard":{"listenAddress":"127.0.0.1","port":18080,"refreshSeconds":5,"admin":{"username":"admin","passwordHash":"PBKDF2-SHA256$100000$AA==$AA==","sessionLifetimeMinutes":60}},"clientsDirectory":"clients","limits":{"maxConnections":10,"maxPendingConnections":5,"maxUnauthenticatedConnections":5,"maxChannelsPerClient":5,"openTimeoutSeconds":20,"blockedWriteTimeoutSeconds":120,"halfCloseDrainTimeoutSeconds":300}}""");

        var exception = Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().Load(path));

        Assert.Contains("tunnel certificate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pending_token_can_be_consumed_only_once()
    {
        using var caller = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var channel = new ChannelConfiguration("test", "Test", true, "127.0.0.1", 12345, "127.0.0.1", 1433, 1, 5);
        using var pending = new PendingConnection(Guid.NewGuid(), channel, caller, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.False(pending.TryConsumeToken(Convert.ToBase64String(new byte[32])));
        Assert.True(pending.TryConsumeToken(pending.Token));
        Assert.False(pending.TryConsumeToken(pending.Token));
    }

    [Fact]
    public void Peer_bind_tokens_are_role_and_session_scoped_and_single_use()
    {
        var caller = new Session(Guid.NewGuid(), "caller", DateTimeOffset.UtcNow);
        var target = new Session(Guid.NewGuid(), "target", DateTimeOffset.UtcNow);
        var relay = new PeerRelay(caller, target, "channel", TimeSpan.FromSeconds(5));
        using var wire = new MemoryStream();
        var tunnel = new DataTunnel(wire, new FrameReader(wire), new FrameWriter(wire));
        Assert.False(relay.TryBind(new PeerBindDataMessage(relay.ConnectionId, caller.SessionId, relay.TargetToken, "caller"), tunnel));
        Assert.False(relay.TryBind(new PeerBindDataMessage(relay.ConnectionId, target.SessionId, relay.CallerToken, "caller"), tunnel));
        Assert.True(relay.TryBind(new PeerBindDataMessage(relay.ConnectionId, caller.SessionId, relay.CallerToken, "caller"), tunnel));
        Assert.False(relay.TryBind(new PeerBindDataMessage(relay.ConnectionId, caller.SessionId, relay.CallerToken, "caller"), tunnel));
        Assert.True(relay.TryBind(new PeerBindDataMessage(relay.ConnectionId, target.SessionId, relay.TargetToken, "target"), tunnel));
    }

    [Fact]
    public void Agent_configuration_rejects_locally_defined_channels()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "agent.json");
        File.WriteAllText(path, """{"serverHost":"tunnel.example.com","serverPort":7443,"clientId":"test-agent","secret":"MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=","reconnect":{"initialDelaySeconds":1,"maxDelaySeconds":30,"permanentErrorDelaySeconds":60},"channels":[]}""");

        var exception = Assert.Throws<AgentConfigurationException>(() => AgentConfigurationLoader.Load(path));

        Assert.Contains("Invalid agent JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Authorized_channel_requires_strong_secret_and_certificate_fingerprint()
    {
        var loader = new ConfigurationLoader();
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 19001, "127.0.0.1", 19002, 5, 5)
        {
            AuthorizedClientsOnly = true,
            AccessSecret = Convert.ToBase64String(new byte[16]),
            E2eCertificateSha256 = new string('A', 64)
        };
        var client = new ClientConfiguration(1, "visited", "Visited", true, Convert.ToBase64String(new byte[32]), 10, 5, [channel]);
        var configuration = TestConfiguration(client);

        Assert.Contains("Access secret", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(configuration)).Message);
        var strong = client with { Channels = [channel with { AccessSecret = Convert.ToBase64String(new byte[32]), E2eCertificateSha256 = "bad" }] };
        Assert.Contains("fingerprint", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(TestConfiguration(strong))).Message);
    }

    [Fact]
    public void Outbound_mapping_rejects_non_loopback_or_wrong_target_secret()
    {
        var secret = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var fingerprint = new string('B', 64);
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 19001, "127.0.0.1", 19002, 5, 5)
        { AuthorizedClientsOnly = true, AccessSecret = secret, E2eCertificateSha256 = fingerprint };
        var visited = new ClientConfiguration(1, "visited", "Visited", true, Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), 10, 5, [channel]);
        var mapping = new OutboundMappingConfiguration("caller", true, "0.0.0.0", "visited", "private", secret, fingerprint);
        var caller = new ClientConfiguration(1, "caller", "Caller", true, Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()), 10, 5, []) { OutboundMappings = [mapping] };
        var loader = new ConfigurationLoader();

        Assert.Contains("Invalid outbound mapping", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(TestConfiguration(visited, caller))).Message);
        caller = caller with { OutboundMappings = [mapping with { LocalAddress = "127.0.0.1", AccessSecret = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray()) }] };
        Assert.Contains("not authorized", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(TestConfiguration(visited, caller))).Message);
    }

    [Fact]
    public void Private_channel_rejects_plaintext_control_transport()
    {
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 19001, "127.0.0.1", 19002, 5, 5)
        { AuthorizedClientsOnly = true, AccessSecret = Convert.ToBase64String(new byte[32]), E2eCertificateSha256 = new string('A', 64) };
        var client = new ClientConfiguration(1, "visited", "Visited", true, Convert.ToBase64String(new byte[32]), 10, 5, [channel]);
        var config = TestConfiguration(client);
        config = config with { Server = config.Server with { Tunnel = config.Server.Tunnel with { TlsEnabled = false } } };
        Assert.Contains("requires tunnel TLS", Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().ValidateClientUpdate(config)).Message);
    }

    private static LoadedConfiguration TestConfiguration(params ClientConfiguration[] clients) => new(
        new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, true, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            "clients", new LimitsConfiguration(100, 20, 10, 10, 20, 120, 300), null),
        clients.ToDictionary(client => client.ClientId));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"relaylink-tests-{Guid.NewGuid():N}");
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
