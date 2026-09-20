using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayLink.Protocol;
using RelayLink.Agent;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Dashboard_client_creation_reports_invalid_uppercase_id_clearly()
    {
        var client = new ClientConfiguration(1, "795S7", "Test", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 10, 5, []);

        var exception = Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().ValidateClientUpdate(TestConfiguration(client)));

        Assert.Contains("Client ID must be", exception.Message);
        new ConfigurationLoader().ValidateClientUpdate(TestConfiguration(client with { ClientId = "795s7" }));
    }

    [Fact]
    public void Server_download_embeds_ca_and_agent_accepts_it()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=RelayLink Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var caPath = Path.Combine(directory.Path, "root-ca.pem");
        File.WriteAllText(caPath, root.ExportCertificatePem());
        var server = TestConfiguration().Server;
        server = server with { Tunnel = server.Tunnel with { AgentServerHost = "tunnel.example.com", TrustedCaPemPath = caPath } };
        var client = new ClientConfiguration(1, "test-agent", "Test", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 10, 5, []);
        var agentPath = Path.Combine(directory.Path, "agent.json");
        File.WriteAllBytes(agentPath, AgentConfigurationFactory.Create(server, client));

        using var document = JsonDocument.Parse(File.ReadAllBytes(agentPath));
        Assert.True(document.RootElement.TryGetProperty("trustedCaPemBase64", out _));
        Assert.False(document.RootElement.TryGetProperty("trustedCaPemPath", out _));
        Assert.Equal("tunnel.example.com", AgentConfigurationLoader.Load(agentPath).ServerHost);
    }

    [Fact]
    public void Agent_accepts_embedded_ca_without_a_sidecar_file()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=RelayLink Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var caBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(root.ExportCertificatePem()));
        var path = Path.Combine(directory.Path, "agent.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            serverHost = "tunnel.example.com", serverPort = 7443, clientId = "test-agent", useTls = true,
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), trustedCaPemBase64 = caBase64,
            reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 30, permanentErrorDelaySeconds = 60 }
        }));

        var agent = AgentConfigurationLoader.Load(path);
        Assert.Null(agent.TrustedCaPemPath);
        Assert.Equal(caBase64, agent.TrustedCaPemBase64);

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["trustedCaPemBase64"] = "bad-base64";
        File.WriteAllText(path, json.ToJsonString());
        Assert.Contains("trustedCaPemBase64", Assert.Throws<AgentConfigurationException>(() => AgentConfigurationLoader.Load(path)).Message);

        File.WriteAllText(Path.Combine(directory.Path, "root-ca.pem"), root.ExportCertificatePem());
        json.Remove("trustedCaPemBase64");
        json["trustedCaPemPath"] = "root-ca.pem";
        File.WriteAllText(path, json.ToJsonString());
        Assert.EndsWith("root-ca.pem", AgentConfigurationLoader.Load(path).TrustedCaPemPath);

        json["trustedCaPemBase64"] = caBase64;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Contains("Exactly one", Assert.Throws<AgentConfigurationException>(() => AgentConfigurationLoader.Load(path)).Message);
    }

    [Fact]
    public void Agent_default_host_uses_advertised_address_instead_of_wildcard_listener()
    {
        var tunnel = new TunnelConfiguration("0.0.0.0", 7443, true, "", "", 10, 15, 45)
        { AgentServerHost = "tunnel.example.com" };
        Assert.Equal("tunnel.example.com", tunnel.DefaultAgentServerHost);
        Assert.Null((tunnel with { AgentServerHost = null }).DefaultAgentServerHost);
        Assert.Equal("127.0.0.1", (tunnel with { AgentServerHost = null, ListenAddress = "127.0.0.1" }).DefaultAgentServerHost);
    }

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
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var tunnel = new DataTunnel(wire, new FrameReader(wire), new FrameWriter(wire), socket);
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
    public void Authorized_channel_requires_strong_secret_but_fingerprint_belongs_to_client()
    {
        var loader = new ConfigurationLoader();
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 19001, "127.0.0.1", 19002, 5, 5)
        {
            AuthorizedClientsOnly = true,
            AccessSecret = Convert.ToBase64String(new byte[16])
        };
        var client = new ClientConfiguration(1, "visited", "Visited", true, Convert.ToBase64String(new byte[32]), 10, 5, [channel]);
        var configuration = TestConfiguration(client);

        Assert.Contains("Access secret", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(configuration)).Message);
        var strong = client with { Channels = [channel with { AccessSecret = Convert.ToBase64String(new byte[32]) }], E2eCertificateSha256 = "bad" };
        Assert.Contains("fingerprint", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(TestConfiguration(strong))).Message);
        loader.ValidateClientUpdate(TestConfiguration(strong with { E2eCertificateSha256 = null }));
    }

    [Fact]
    public void Channel_level_fingerprint_is_rejected_instead_of_migrated()
    {
        using var directory = new TemporaryDirectory();
        var clientsPath = Path.Combine(directory.Path, "clients");
        Directory.CreateDirectory(clientsPath);
        var pin = new string('A', 64);
        var channel = new ChannelConfiguration("echo", "Echo", true, "127.0.0.1", 19000, "127.0.0.1", 19001, 5, 5);
        var client = new ClientConfiguration(1, "visited", "Visited", true, Convert.ToBase64String(new byte[32]), 10, 5, [channel]);
        var server = TestConfiguration(client).Server with
        {
            ClientsDirectory = clientsPath,
            Tunnel = TestConfiguration(client).Server.Tunnel with { TlsEnabled = false }
        };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var serverPath = Path.Combine(directory.Path, "server.json");
        var clientPath = Path.Combine(clientsPath, "visited.json");
        File.WriteAllText(serverPath, JsonSerializer.Serialize(server, options));
        File.WriteAllText(clientPath, JsonSerializer.Serialize(client, options));

        var oldFormat = JsonNode.Parse(File.ReadAllText(clientPath))!;
        oldFormat["channels"]![0]!["e2eCertificateSha256"] = pin;
        File.WriteAllText(clientPath, oldFormat.ToJsonString());
        Assert.Contains("e2eCertificateSha256", Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().Load(serverPath)).Message);

        File.WriteAllText(clientPath, JsonSerializer.Serialize(client with { E2eCertificateSha256 = pin }, options));
        Assert.Equal(pin, new ConfigurationLoader().Load(serverPath).Clients["visited"].E2eCertificateSha256);
    }

    [Fact]
    public void Outbound_mapping_rejects_non_loopback_or_wrong_target_secret()
    {
        var secret = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var fingerprint = new string('B', 64);
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 19001, "127.0.0.1", 19002, 5, 5)
        { AuthorizedClientsOnly = true, AccessSecret = secret };
        var visited = new ClientConfiguration(1, "visited", "Visited", true, Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), 10, 5, [channel]) { E2eCertificateSha256 = fingerprint };
        var mapping = new OutboundMappingConfiguration("caller", true, "0.0.0.0", "visited", "private", secret, fingerprint);
        var caller = new ClientConfiguration(1, "caller", "Caller", true, Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()), 10, 5, []) { OutboundMappings = [mapping] };
        var loader = new ConfigurationLoader();

        Assert.Contains("Invalid outbound mapping", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(TestConfiguration(visited, caller))).Message);
        caller = caller with { OutboundMappings = [mapping with { LocalAddress = "127.0.0.1", AccessSecret = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray()) }] };
        Assert.Contains("not authorized", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(TestConfiguration(visited, caller))).Message);
    }

    [Fact]
    public void Referenced_target_client_can_be_disabled_without_deleting_its_configuration()
    {
        var accessSecret = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        var fingerprint = new string('B', 64);
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 19001, "127.0.0.1", 19002, 5, 5)
        { AuthorizedClientsOnly = true, AccessSecret = accessSecret };
        var visited = new ClientConfiguration(1, "visited", "Visited", false, Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), 10, 5, [channel]) { E2eCertificateSha256 = fingerprint };
        var caller = new ClientConfiguration(1, "caller", "Caller", true, Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray()), 10, 5, [])
        { OutboundMappings = [new OutboundMappingConfiguration("to-visited", true, "127.0.0.1", "visited", "private", accessSecret, fingerprint)] };
        var loader = new ConfigurationLoader();

        loader.ValidateClientUpdate(TestConfiguration(visited, caller));
        Assert.Contains("not authorized", Assert.Throws<ConfigurationException>(() => loader.ValidateClientUpdate(TestConfiguration(visited with
        { Channels = [channel with { AccessSecret = Convert.ToBase64String(Enumerable.Repeat((byte)8, 32).ToArray()) }] }, caller))).Message);
    }

    [Fact]
    public void Private_channel_rejects_plaintext_control_transport()
    {
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 19001, "127.0.0.1", 19002, 5, 5)
        { AuthorizedClientsOnly = true, AccessSecret = Convert.ToBase64String(new byte[32]) };
        var client = new ClientConfiguration(1, "visited", "Visited", true, Convert.ToBase64String(new byte[32]), 10, 5, [channel]);
        var config = TestConfiguration(client);
        config = config with { Server = config.Server with { Tunnel = config.Server.Tunnel with { TlsEnabled = false } } };
        Assert.Contains("requires tunnel TLS", Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().ValidateClientUpdate(config)).Message);
    }

    [Fact]
    public void Channel_listener_cannot_overlap_the_separate_data_port()
    {
        var channel = new ChannelConfiguration("echo", "Echo", true, "127.0.0.1", 7444, "127.0.0.1", 19002, 5, 5);
        var client = new ClientConfiguration(1, "agent", "Agent", true, Convert.ToBase64String(new byte[32]), 10, 5, [channel]);
        Assert.Contains("conflicts with a server endpoint", Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().ValidateClientUpdate(TestConfiguration(client))).Message);
    }

    [Fact]
    public void Security_group_matches_only_configured_source_ranges_and_rejects_invalid_references()
    {
        var channel = new ChannelConfiguration("echo", "Echo", true, "127.0.0.1", 19000, "127.0.0.1", 19001, 5, 5)
        { SecurityGroupId = "office" };
        var client = new ClientConfiguration(1, "agent", "Agent", true, Convert.ToBase64String(new byte[32]), 10, 5, [channel]);
        var config = TestConfiguration(client) with
        {
            Server = TestConfiguration(client).Server with
            {
                Tunnel = TestConfiguration(client).Server.Tunnel with { TlsEnabled = false },
                SecurityGroups = [new SecurityGroupConfiguration("office", "Office", ["127.0.0.1", "192.0.2.0/24", "2001:db8::/32"])]
            }
        };
        var loader = new ConfigurationLoader();
        loader.ValidateServerUpdate(config);
        Assert.True(SecurityGroupMatcher.IsAllowed(channel, config.Server, IPAddress.Loopback));
        Assert.True(SecurityGroupMatcher.IsAllowed(channel, config.Server, IPAddress.Parse("192.0.2.42")));
        Assert.True(SecurityGroupMatcher.IsAllowed(channel, config.Server, IPAddress.Parse("2001:db8::1")));
        Assert.False(SecurityGroupMatcher.IsAllowed(channel, config.Server, IPAddress.Parse("192.0.3.1")));
        Assert.False(SecurityGroupMatcher.IsAllowed(channel, config.Server, IPAddress.Parse("127.0.0.2")));
        Assert.False(SecurityGroupMatcher.IsValidEntry("127.1"));
        Assert.True(SecurityGroupMatcher.IsAllowed(channel with { SecurityGroupId = null }, config.Server, IPAddress.Parse("192.0.3.1")));
        Assert.Contains("Invalid security group", Assert.Throws<ConfigurationException>(() => loader.ValidateServerUpdate(config with
        { Server = config.Server with { SecurityGroups = [new SecurityGroupConfiguration("office", "Office", ["192.0.2.0/33"])] } })).Message);
        Assert.Contains("Invalid security group for", Assert.Throws<ConfigurationException>(() => loader.ValidateServerUpdate(config with
        { Server = config.Server with { SecurityGroups = [] } })).Message);
        Assert.Contains("Invalid security group for", Assert.Throws<ConfigurationException>(() => loader.ValidateServerUpdate(config with
        { Clients = new Dictionary<string, ClientConfiguration> { ["agent"] = client with { Channels = [channel with { AuthorizedClientsOnly = true, AccessSecret = Convert.ToBase64String(new byte[32]) }] } } })).Message);
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
