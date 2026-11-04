using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class InspectionConfigurationPersistenceTests
{
    [Fact]
    public async Task Ssh_policy_loads_from_disk_survives_unrelated_edit_and_rejects_forwarding()
    {
        using var fixture = new Fixture();
        var original = fixture.Runtime.Configuration.Clients["target"];
        var policy = new InspectionPolicy
        {
            ApprovalTimeoutSeconds = 5,
            Ssh = new() { TargetHostKeySha256 = "SHA256:" + new string('A', 43), AllowShell = false, AllowExec = false,
                AllowSubsystem = true, RequireApproval = true, DeniedCommandPrefixes = ["shutdown"] }
        };
        var ssh = original.Channels[0] with { Tags = ["app:ssh"], Inspection = policy };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(fixture.ClientPath, JsonSerializer.Serialize(original with { Channels = [ssh] }, options));
        fixture.Runtime.ReplaceConfiguration(new ConfigurationLoader().Load(fixture.ServerPath));
        await fixture.Channels.UpdateAsync("target", "web", Update(ssh), default);
        var saved = new ConfigurationLoader().Load(fixture.ServerPath).Clients["target"].Channels[0];
        Assert.True(InspectionPolicy.Same(policy, saved.Inspection));
        Assert.False(saved.Inspection!.Ssh!.AllowShell);
        Assert.False(saved.Inspection.Ssh.AllowExec);
        Assert.True(saved.Inspection.Ssh.AllowSubsystem);
        File.WriteAllText(fixture.ClientPath, JsonSerializer.Serialize(original with
        { Channels = [ssh with { Inspection = policy with { Ssh = policy.Ssh with { AllowTcpForwarding = true } } }] }, options));
        Assert.Throws<ConfigurationException>(() => new ConfigurationLoader().Load(fixture.ServerPath));
    }

    [Fact]
    public async Task All_client_rewriters_preserve_relative_tls_paths_and_a_moved_configuration_resolves_again()
    {
        using var fixture = new Fixture();
        var loaded = fixture.Runtime.Configuration.Clients["target"];
        var original = loaded.Channels[0];
        Assert.Equal("cert.pem", original.Inspection!.Https!.CertificatePemPath);
        Assert.Equal(Path.Combine(fixture.Directory, "clients", "cert.pem"), original.ResolvedInspection!.Https!.CertificatePemPath);
        await fixture.Channels.UpdateAsync("target", "web", Update(original), default);
        AssertPaths(fixture.ClientPath);
        await fixture.Clients.UpdateAsync("target", new("Renamed client", true, 4, 4), default);
        AssertPaths(fixture.ClientPath);
        await fixture.Clients.BindIdentityAsync(fixture.Runtime.Configuration.Clients["target"], new string('A', 64), default);
        AssertPaths(fixture.ClientPath);

        var copy = Path.Combine(fixture.Directory, "moved");
        System.IO.Directory.CreateDirectory(Path.Combine(copy, "clients"));
        File.Copy(fixture.ServerPath, Path.Combine(copy, "server.json"));
        foreach (var file in System.IO.Directory.GetFiles(Path.Combine(fixture.Directory, "clients")))
            File.Copy(file, Path.Combine(copy, "clients", Path.GetFileName(file)));
        var moved = new ConfigurationLoader().Load(Path.Combine(copy, "server.json")).Clients["target"].Channels[0];
        Assert.Equal(Path.Combine(copy, "clients", "cert.pem"), moved.ResolvedInspection!.Https!.CertificatePemPath);
        Assert.Equal("cert.pem", moved.Inspection!.Https!.CertificatePemPath);
    }

    [Fact]
    public async Task Unavailable_creation_and_reenable_do_not_change_files_and_existing_https_remains_editable()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllText(fixture.ClientPath);
        foreach (var application in new[] { "rdp", "https" })
        {
            await Assert.ThrowsAsync<ChannelUpdateException>(() => fixture.Channels.CreateAsync("target",
                new("new", "New", false, "127.0.0.1", 19002, "127.0.0.1", 19003, 4, 5)
                { InspectionMode = "server-jump", Application = application }, default));
            Assert.Equal(before, File.ReadAllText(fixture.ClientPath));
        }
        var rdp = fixture.Runtime.Configuration.Clients["target"].Channels[1];
        await Assert.ThrowsAsync<ChannelUpdateException>(() => fixture.Channels.UpdateAsync("target", "rdp", Update(rdp) with { Enabled = true }, default));
        Assert.Equal(before, File.ReadAllText(fixture.ClientPath));
        await fixture.Channels.UpdateAsync("target", "web", Update(fixture.Runtime.Configuration.Clients["target"].Channels[0]), default);
        AssertPaths(fixture.ClientPath);
        await fixture.Channels.DeleteAsync("target", "rdp", default);
        Assert.DoesNotContain(fixture.Runtime.Configuration.Clients["target"].Channels, item => item.ChannelId == "rdp");
    }

    [Fact]
    public async Task Jump_and_peer_inspection_channels_cannot_be_saved_when_control_tls_is_disabled()
    {
        using var fixture = new Fixture(controlTlsEnabled: false);
        var before = File.ReadAllText(fixture.ClientPath);
        foreach (var mode in new[] { "server-jump", "server-peer" })
        {
            var exception = await Assert.ThrowsAsync<ChannelUpdateException>(() => fixture.Channels.CreateAsync("target",
                new("new-" + mode, "New", false, "127.0.0.1", mode == "server-jump" ? 19010 : 19020, "127.0.0.1",
                    mode == "server-jump" ? 19011 : 19021, 4, 5)
                {
                    InspectionMode = mode,
                    Application = "ssh",
                    AuthorizedClientsOnly = mode == "server-peer",
                    AccessSecret = mode == "server-peer" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null
                }, default));
            Assert.Contains("control TLS", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllText(fixture.ClientPath));
        }

        // The editor reloads the file before validating an update, so a TLS-disabled file that
        // contains an inspection channel is rejected without any partial write.
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var tampered = System.Text.Json.Nodes.JsonNode.Parse(before)!;
        var jump = new ChannelConfiguration("jump", "Jump", false, "127.0.0.1", 19030, "127.0.0.1", 19031, 4, 5)
            { InspectionMode = "server-jump", Tags = ["app:ssh"], Inspection = new InspectionPolicy { Ssh = new() } };
        tampered["channels"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(jump, options)));
        var tamperedText = tampered.ToJsonString();
        File.WriteAllText(fixture.ClientPath, tamperedText);
        var web = fixture.Runtime.Configuration.Clients["target"].Channels[0];
        var updateException = await Assert.ThrowsAsync<ChannelUpdateException>(() =>
            fixture.Channels.UpdateAsync("target", "web", Update(web), default));
        Assert.Contains("control TLS", updateException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tamperedText, File.ReadAllText(fixture.ClientPath));
    }

    [Fact]
    public async Task Enabled_rdp_inspection_channel_can_be_disabled_but_not_reenabled()
    {
        using var fixture = new Fixture(rdpEnabled: true);
        var before = File.ReadAllText(fixture.ClientPath);
        var rdp = fixture.Runtime.Configuration.Clients["target"].Channels.Single(channel => channel.ChannelId == "rdp");
        Assert.True(rdp.Enabled);
        Assert.Equal("rdp", rdp.Application);

        var (disabled, _) = await fixture.Channels.UpdateAsync("target", "rdp", Update(rdp) with { Enabled = false }, default);
        Assert.False(disabled.Enabled);
        var persisted = File.ReadAllText(fixture.ClientPath);
        Assert.NotEqual(before, persisted);
        var saved = new ConfigurationLoader().Load(fixture.ServerPath).Clients["target"].Channels
            .Single(channel => channel.ChannelId == "rdp");
        Assert.False(saved.Enabled);
        Assert.Equal("rdp", saved.Application);
        using (var json = JsonDocument.Parse(persisted))
        {
            var persistedRdp = json.RootElement.GetProperty("channels").EnumerateArray()
                .Single(item => item.GetProperty("channelId").GetString() == "rdp");
            Assert.False(persistedRdp.GetProperty("enabled").GetBoolean());
        }

        var rejected = await Assert.ThrowsAsync<ChannelUpdateException>(() =>
            fixture.Channels.UpdateAsync("target", "rdp", Update(saved) with { Enabled = true }, default));
        Assert.Contains("RDP inspection is not implemented", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(persisted, File.ReadAllText(fixture.ClientPath));
    }

    private static ChannelUpdateRequest Update(ChannelConfiguration channel) => new("Renamed", channel.Enabled, channel.ListenAddress, channel.ListenPort, channel.TargetHost,
        channel.TargetPort, channel.MaxConnections, channel.TargetConnectTimeoutSeconds) { Application = channel.Application };

    private static void AssertPaths(string clientPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(clientPath));
        var material = json.RootElement.GetProperty("channels")[0].GetProperty("inspection").GetProperty("https");
        Assert.Equal("cert.pem", material.GetProperty("certificatePemPath").GetString());
        Assert.Equal("key.pem", material.GetProperty("privateKeyPemPath").GetString());
        Assert.Equal("cert.pem", material.GetProperty("upstreamCaPemPath").GetString());
        Assert.False(json.RootElement.GetProperty("channels")[0].TryGetProperty("configurationDirectory", out _));
        Assert.False(json.RootElement.GetProperty("channels")[0].TryGetProperty("resolvedInspection", out _));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("relaylink-inspection-config-").FullName;
        public string ServerPath => Path.Combine(Directory, "server.json");
        public string ClientPath => Path.Combine(Directory, "clients", "target.json");
        public ServerRuntime Runtime { get; }
        public ChannelConfigurationEditor Channels { get; }
        public ClientConfigurationEditor Clients { get; }
        private readonly ProxyListenerService listeners;

        public Fixture(bool controlTlsEnabled = true, bool rdpEnabled = false)
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "clients"));
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(Path.Combine(Directory, "clients", "cert.pem"), certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(Directory, "clients", "key.pem"), key.ExportPkcs8PrivateKeyPem());
            var channel = controlTlsEnabled
                ? new ChannelConfiguration("web", "Web", false, "127.0.0.1", 19000, "127.0.0.1", 19001, 4, 5)
                {
                    InspectionMode = "server-jump", Tags = ["app:https"],
                    Inspection = new() { Https = new() { CertificatePemPath = "cert.pem", PrivateKeyPemPath = "key.pem", UpstreamCaPemPath = "cert.pem" } }
                }
                : new ChannelConfiguration("web", "Web", false, "127.0.0.1", 19000, "127.0.0.1", 19001, 4, 5)
                {
                    Tags = ["app:https"]
                };
            var client = new ClientConfiguration(1, "target", "Target", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 4, 4,
                [channel, channel with { ChannelId = "rdp", ListenPort = 19002, Tags = ["app:rdp"], Inspection = null, Enabled = rdpEnabled }]);
            var server = new ServerConfiguration(1, new("127.0.0.1", 7443, controlTlsEnabled,
                    controlTlsEnabled ? "clients/cert.pem" : "", controlTlsEnabled ? "clients/key.pem" : "", 5, 5, 15),
                new("127.0.0.1", 18080, 5, new("admin", "PBKDF2-SHA256$100000$AA==$AA==", 60)), "clients", new(10, 10, 10, 10, 5, 10, 10), null);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(ServerPath, JsonSerializer.Serialize(server, options));
            File.WriteAllText(ClientPath, JsonSerializer.Serialize(client, options));
            var loaded = new ConfigurationLoader().Load(ServerPath);
            Runtime = new(loaded, new SessionRegistry());
            var audit = new AuditService(Runtime, NullLogger<AuditService>.Instance);
            listeners = new(Runtime, new PendingConnectionRegistry(), new MetricsRegistry(loaded), audit, NullLogger<ProxyListenerService>.Instance);
            var peers = new PeerRelayRegistry(Runtime, new MetricsRegistry(loaded), audit, NullLogger<PeerRelayRegistry>.Instance);
            var configLock = new ConfigurationWriteLock();
            var credentials = CredentialStore.Open(ServerPath);
            Channels = new(ServerPath, new ConfigurationLoader(), Runtime, listeners, peers, Runtime.Sessions, configLock, credentials);
            Clients = new(ServerPath, new ConfigurationLoader(), Runtime, listeners, peers, Runtime.Sessions, configLock, credentials);
        }
        public void Dispose()
        {
            listeners.StopAsync(default).GetAwaiter().GetResult();
            listeners.Dispose();
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
        }
    }
}
