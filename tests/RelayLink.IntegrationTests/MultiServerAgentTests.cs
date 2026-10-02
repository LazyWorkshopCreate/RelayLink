using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using RelayLink.Agent;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Transport;
using Serilog;

namespace RelayLink.IntegrationTests;

public sealed class MultiServerAgentTests
{
    [Fact]
    public async Task Local_configuration_save_failure_uses_shared_error_file_without_credentials()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("relaylink-save-log-");
        try
        {
            var configPath = Path.Combine(directory.FullName, "agent.json");
            var profile = new AgentServerProfile
            {
                ProfileId = "east", ClientId = "test-agent", Enabled = false,
                ServerHost = "127.0.0.1", ServerPort = 7443, DataPort = 7444,
                Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Reconnect = new ReconnectConfiguration(1, 2, 3)
            };
            var config = new AgentProcessConfiguration { DashboardPort = FreePort(), Servers = [profile] };
            var original = JsonSerializer.Serialize(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await File.WriteAllTextAsync(configPath, original, token);
            using var sink = AgentFileLogging.Create(configPath);
            using var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, dispose: false));
            var runtime = new AgentProcessRuntime(config, new AgentConfigurationPath(configPath), factory)
            {
                BeforeConfigurationCommit = () => throw new UnauthorizedAccessException("test-save-denied")
            };
            using var dashboard = new AgentLocalDashboard(config, runtime, loggerFactory: factory);
            try
            {
                await runtime.StartAsync(token);
                await dashboard.StartAsync(token);
                using var http = new HttpClient(new HttpClientHandler { UseCookies = true })
                { BaseAddress = new Uri($"http://127.0.0.1:{config.DashboardPort}") };
                JsonElement session;
                while (true)
                {
                    try { session = await http.GetFromJsonAsync<JsonElement>("/api/v2/admin/session", token); break; }
                    catch (HttpRequestException) { await Task.Delay(50, token); }
                }
                using var response = await WriteAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east/enabled",
                    http.BaseAddress.GetLeftPart(UriPartial.Authority), session.GetProperty("csrfToken").GetString()!,
                    new { version = session.GetProperty("version").GetString(), enabled = true }, token);
                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(token);
                Assert.Contains("Unable to save Agent configuration.", body);
                Assert.DoesNotContain("test-save-denied", body);
                Assert.Equal(original, await File.ReadAllTextAsync(configPath, token));
                Assert.False(Assert.Single(runtime.Snapshot).Enabled);
            }
            finally
            {
                await dashboard.StopAsync(CancellationToken.None);
                await runtime.StopAsync(CancellationToken.None);
            }
            static string ReadActiveLog(string path)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            var logs = string.Concat(Directory.GetFiles(Path.Combine(directory.FullName, "logs"), "*.jsonl").Select(ReadActiveLog));
            var errors = string.Concat(Directory.GetFiles(Path.Combine(directory.FullName, "logs"), "error-*.jsonl").Select(ReadActiveLog));
            Assert.Contains("test-save-denied", errors);
            Assert.Contains("east", errors);
            Assert.Contains("UnauthorizedAccessException", errors);
            Assert.DoesNotContain(profile.Secret, logs);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static readonly object PortGate = new();
    private static readonly HashSet<int> AssignedPorts = [];

    [Fact]
    public async Task Agent_cli_rejects_legacy_json_until_standalone_migrator_converts_it()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-cli-conversion-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var oldPath = Path.Combine(directory.FullName, "old.json");
            var newPath = Path.Combine(directory.FullName, "new.json");
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllTextAsync(oldPath, JsonSerializer.Serialize(new
            {
                serverHost = "127.0.0.1", serverPort = 7443, clientId = "same-agent", secret, useTls = false,
                reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 2, permanentErrorDelaySeconds = 3 }
            }), deadline.Token);
            var oldBytes = await File.ReadAllBytesAsync(oldPath, deadline.Token);
            var agentAssembly = typeof(AgentProcessConfigurationLoader).Assembly.Location;
            var migratorAssembly = Path.Combine(AppContext.BaseDirectory, "RelayLink.Agent.ConfigMigrator.dll");
            Assert.True(File.Exists(migratorAssembly));

            var rejected = await RunToolAsync(agentAssembly, ["--config", oldPath, "--check-config"], deadline.Token);
            Assert.Equal(2, rejected.ExitCode);
            Assert.Contains("config migrator", rejected.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, rejected.Stderr, StringComparison.Ordinal);

            var converted = await RunToolAsync(migratorAssembly, ["--input", oldPath, "--output", newPath], deadline.Token);
            Assert.Equal(0, converted.ExitCode);
            Assert.Equal("primary", Assert.Single(AgentProcessConfigurationLoader.Load(newPath).Servers).ProfileId);
            Assert.Equal(secret, Assert.Single(AgentProcessConfigurationLoader.Load(newPath).Servers).Secret);
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldPath, deadline.Token));
            var accepted = await RunToolAsync(agentAssembly, ["--config", newPath, "--check-config"], deadline.Token);
            Assert.Equal(0, accepted.ExitCode);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Invalid_snapshot_update_on_one_server_does_not_interrupt_other_server()
    {
        using var west = new ServerFixture(0x57);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await west.StartAsync(token);
        using var fake = new TcpListener(IPAddress.Loopback, 0);
        fake.Start();
        var fakePort = ((IPEndPoint)fake.LocalEndpoint).Port;
        var inject = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeTask = Task.Run(async () =>
        {
            using var client = await fake.AcceptTcpClientAsync(token);
            var reader = new FrameReader(client.GetStream());
            var writer = new FrameWriter(client.GetStream());
            Assert.Equal(FrameType.Register, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
            var session = Guid.NewGuid();
            var snapshot = new ClientConfigSnapshot("same-agent", "Faulty", 10, 5, []);
            var version = ConfigurationSnapshotHasher.Compute(snapshot);
            await writer.WriteAsync(new Frame(FrameType.RegisterAccepted, JsonProtocolSerializer.Serialize(
                new RegisterAcceptedMessage(session, version, snapshot, 1, 8))), token);
            Assert.Equal(FrameType.ConfigAck, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
            await writer.WriteAsync(new Frame(FrameType.Ready, JsonProtocolSerializer.Serialize(new ReadyMessage(session))), token);
            await inject.Task.WaitAsync(token);
            await writer.WriteAsync(new Frame(FrameType.ConfigUpdate, JsonProtocolSerializer.Serialize(
                new ConfigUpdateMessage(session, "incorrect-version", snapshot))), token);
            Assert.Null(await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token));
        }, token);
        var directory = Directory.CreateTempSubdirectory("relaylink-invalid-snapshot-");
        var path = Path.Combine(directory.FullName, "agent.json");
        var fakeDataPort = FreePort();
        var badProfile = west.Profile("east") with
        {
            ServerPort = fakePort, DataPort = fakeDataPort == fakePort ? FreePort() : fakeDataPort,
            UseTls = false, TrustedCaPemBase64 = null
        };
        var config = new AgentProcessConfiguration { DashboardPort = 0, Servers = [badProfile, west.Profile("west")] };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
        var runtime = new AgentProcessRuntime(config, new AgentConfigurationPath(path), NullLoggerFactory.Instance);
        try
        {
            await runtime.StartAsync(token);
            await WaitUntilAsync(() => runtime.Snapshot.Count == 2 && runtime.Snapshot.All(item => item.Status.Online), token);
            inject.SetResult();
            await fakeTask;
            await WaitUntilAsync(() => !runtime.Snapshot.Single(item => item.ProfileId == "east").Status.Online, token);
            Assert.True(runtime.Snapshot.Single(item => item.ProfileId == "west").Status.Online);
            Assert.Equal((byte)0x57, (await RoundTripAsync(west.ProxyPort, new byte[] { 1, 2, 3 }, token))[0]);
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Same_mapping_id_on_two_servers_rotates_conflicting_loopback_port_and_relays_to_own_target()
    {
        using var east = new ServerFixture(0x45);
        using var west = new ServerFixture(0x57);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var targetDirectory = Directory.CreateTempSubdirectory("relaylink-multi-target-");
        var callerDirectory = Directory.CreateTempSubdirectory("relaylink-multi-caller-");
        var targetPath = Path.Combine(targetDirectory.FullName, "agent.json");
        var callerPath = Path.Combine(callerDirectory.FullName, "agent.json");
        AgentProcessRuntime? targets = null;
        AgentProcessRuntime? callers = null;
        try
        {
            var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var profile in new[] { "east", "west" })
            {
                var state = Directory.CreateDirectory(Path.Combine(targetDirectory.FullName, "state", profile));
                using var identity = AgentIdentity.LoadOrCreate(Path.Combine(state.FullName, "identity.pfx"), "target");
                fingerprints.Add(profile, identity.Fingerprint);
            }
            await east.StartAsync(token, fingerprints["east"]);
            await west.StartAsync(token, fingerprints["west"]);
            var targetConfig = new AgentProcessConfiguration
            { DashboardPort = 0, Servers = [east.TargetProfile("east"), west.TargetProfile("west")] };
            await File.WriteAllTextAsync(targetPath, JsonSerializer.Serialize(targetConfig, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
            targets = new AgentProcessRuntime(targetConfig, new AgentConfigurationPath(targetPath), NullLoggerFactory.Instance);
            await targets.StartAsync(token);
            await WaitUntilAsync(() => targets.Snapshot.Count == 2 && targets.Snapshot.All(item => item.Status.Online), token);

            var firstPort = FindAdjacentFreePorts();
            foreach (var profile in new[] { "east", "west" })
            {
                var state = Directory.CreateDirectory(Path.Combine(callerDirectory.FullName, "state", profile));
                await File.WriteAllTextAsync(Path.Combine(state.FullName, "ports.json"),
                    JsonSerializer.Serialize(new Dictionary<string, int> { ["to-target"] = firstPort }), token);
            }
            var callerConfig = new AgentProcessConfiguration
            {
                DashboardPort = 0, OutboundPortRangeStart = firstPort, OutboundPortRangeEnd = firstPort + 1,
                Servers = [east.Profile("east"), west.Profile("west")]
            };
            await File.WriteAllTextAsync(callerPath, JsonSerializer.Serialize(callerConfig, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
            callers = new AgentProcessRuntime(callerConfig, new AgentConfigurationPath(callerPath), NullLoggerFactory.Instance);
            await callers.StartAsync(token);
            await WaitUntilAsync(() => callers.Snapshot.Count == 2 && callers.Snapshot.All(item =>
                item.Status.Online && item.Status.OutboundMappings.Count == 1 && item.Status.OutboundMappings[0].LocalAddress is not null), token);
            var addresses = callers.Snapshot.ToDictionary(item => item.ProfileId,
                item => int.Parse(item.Status.OutboundMappings[0].LocalAddress!.Split(':')[1]), StringComparer.Ordinal);
            Assert.Equal(2, addresses.Values.Distinct().Count());
            Assert.Contains(firstPort, addresses.Values);
            Assert.Contains(firstPort + 1, addresses.Values);
            Assert.Equal((byte)0x45, (await RoundTripAsync(addresses["east"], new byte[] { 1, 2, 3 }, token))[0]);
            Assert.Equal((byte)0x57, (await RoundTripAsync(addresses["west"], new byte[] { 4, 5, 6 }, token))[0]);

            await callers.StopAsync(CancellationToken.None);
            callers = null;
            using var occupied = new TcpListener(IPAddress.Loopback, firstPort);
            occupied.Start();
            callers = new AgentProcessRuntime(callerConfig, new AgentConfigurationPath(callerPath), NullLoggerFactory.Instance);
            await callers.StartAsync(token);
            await WaitUntilAsync(() => callers.Snapshot.Count == 2 && callers.Snapshot.All(item => item.Status.Online), token);
            var available = callers.Snapshot.Where(item => item.Status.OutboundMappings[0].LocalAddress is not null).ToArray();
            var surviving = Assert.Single(available);
            Assert.Null(Assert.Single(callers.Snapshot, item => item.ProfileId != surviving.ProfileId)
                .Status.OutboundMappings[0].LocalAddress);
            var survivingPort = int.Parse(surviving.Status.OutboundMappings[0].LocalAddress!.Split(':')[1]);
            Assert.Equal(firstPort + 1, survivingPort);
            Assert.Equal(surviving.ProfileId == "east" ? (byte)0x45 : (byte)0x57,
                (await RoundTripAsync(survivingPort, new byte[] { 7, 8, 9 }, token))[0]);

            await callers.StopAsync(CancellationToken.None);
            callers = null;
            occupied.Stop();
            var eastState = Path.Combine(callerDirectory.FullName, "state", "east");
            var originalIdentity = File.ReadAllBytes(Path.Combine(eastState, "identity.pfx"));
            var savedPort = JsonDocument.Parse(File.ReadAllText(Path.Combine(eastState, "ports.json")))
                .RootElement.GetProperty("to-target").GetInt32();
            File.Copy(Path.Combine(eastState, "identity.pfx"), Path.Combine(callerDirectory.FullName, "same-agent.e2e.pfx"));
            File.Copy(Path.Combine(eastState, "ports.json"), Path.Combine(callerDirectory.FullName, "same-agent.ports.json"));
            await File.WriteAllTextAsync(callerPath, JsonSerializer.Serialize(new
            {
                serverHost = "127.0.0.1", serverPort = east.TunnelPort, dataPort = east.DataPort,
                clientId = "same-agent", secret = east.Secret, useTls = true, trustedCaPemBase64 = east.CaPemBase64,
                dashboardPort = 0, outboundPortRangeStart = firstPort, outboundPortRangeEnd = firstPort + 1,
                reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 2, permanentErrorDelaySeconds = 3 }
            }), token);
            AgentConfigurationMigrator.MigrateInstalled(callerPath);
            var migratedConfig = AgentProcessConfigurationLoader.Load(callerPath);
            Assert.Equal("primary", Assert.Single(migratedConfig.Servers).ProfileId);
            Assert.Equal(originalIdentity, File.ReadAllBytes(Path.Combine(callerDirectory.FullName, "state", "primary", "identity.pfx")));
            callers = new AgentProcessRuntime(migratedConfig, new AgentConfigurationPath(callerPath), NullLoggerFactory.Instance);
            await callers.StartAsync(token);
            await WaitUntilAsync(() => callers.Snapshot.Count == 1 && callers.Snapshot[0].Status.Online &&
                callers.Snapshot[0].Status.OutboundMappings[0].LocalAddress is not null, token);
            var reusedPort = int.Parse(callers.Snapshot[0].Status.OutboundMappings[0].LocalAddress!.Split(':')[1]);
            Assert.Equal(savedPort, reusedPort);
            Assert.Equal((byte)0x45, (await RoundTripAsync(reusedPort, new byte[] { 10, 11, 12 }, token))[0]);
        }
        finally
        {
            if (callers is not null) await callers.StopAsync(CancellationToken.None);
            if (targets is not null) await targets.StopAsync(CancellationToken.None);
            callerDirectory.Delete(recursive: true);
            targetDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_upgrade_then_add_second_server_and_restart_preserves_identity_and_both_connections()
    {
        using var east = new ServerFixture(0x45);
        using var west = new ServerFixture(0x57);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        await east.StartAsync(token);
        await west.StartAsync(token);
        var directory = Directory.CreateTempSubdirectory("relaylink-upgrade-live-");
        var configPath = Path.Combine(directory.FullName, "agent.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            serverHost = "127.0.0.1", serverPort = east.TunnelPort, dataPort = east.DataPort,
            clientId = "same-agent", secret = east.Secret, useTls = true, trustedCaPemBase64 = east.CaPemBase64,
            dashboardPort = 0,
            reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 2, permanentErrorDelaySeconds = 3 }
        }), token);
        var legacyIdentity = Path.Combine(directory.FullName, "same-agent.e2e.pfx");
        using (var identity = AgentIdentity.LoadOrCreate(legacyIdentity, "same-agent"))
            Assert.NotEmpty(identity.Fingerprint);
        var originalIdentity = File.ReadAllBytes(legacyIdentity);
        File.WriteAllText(Path.Combine(directory.FullName, "same-agent.ports.json"), """{"mapping":20123}""");
        try
        {
            AgentConfigurationMigrator.MigrateInstalled(configPath);
            AgentConfigurationMigrator.MigrateInstalled(configPath);
            Assert.Equal(originalIdentity, File.ReadAllBytes(Path.Combine(directory.FullName, "state", "primary", "identity.pfx")));
            var config = AgentProcessConfigurationLoader.Load(configPath);
            var runtime = new AgentProcessRuntime(config, new AgentConfigurationPath(configPath), NullLoggerFactory.Instance);
            await runtime.StartAsync(token);
            try
            {
                await WaitUntilAsync(() => runtime.Snapshot.Count == 1 && runtime.Snapshot[0].Status.Online, token);
                Assert.Equal((byte)0x45, (await RoundTripAsync(east.ProxyPort, new byte[] { 1 }, token))[0]);
                await runtime.AddAsync(west.Profile("west"), runtime.ConfigurationVersion, false, token);
                await WaitUntilAsync(() => runtime.Snapshot.Count == 2 && runtime.Snapshot.All(item => item.Status.Online), token);
                Assert.Equal((byte)0x57, (await RoundTripAsync(west.ProxyPort, new byte[] { 2 }, token))[0]);
            }
            finally { await runtime.StopAsync(CancellationToken.None); }

            config = AgentProcessConfigurationLoader.Load(configPath);
            var restarted = new AgentProcessRuntime(config, new AgentConfigurationPath(configPath), NullLoggerFactory.Instance);
            await restarted.StartAsync(token);
            try
            {
                await WaitUntilAsync(() => restarted.Snapshot.Count == 2 && restarted.Snapshot.All(item => item.Status.Online), token);
                Assert.Equal((byte)0x45, (await RoundTripAsync(east.ProxyPort, new byte[] { 3 }, token))[0]);
                Assert.Equal((byte)0x57, (await RoundTripAsync(west.ProxyPort, new byte[] { 4 }, token))[0]);
                Assert.Equal(originalIdentity, File.ReadAllBytes(Path.Combine(directory.FullName, "state", "primary", "identity.pfx")));
                Assert.Equal("""{"mapping":20123}""", File.ReadAllText(Path.Combine(directory.FullName, "state", "primary", "ports.json")));
            }
            finally { await restarted.StopAsync(CancellationToken.None); }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Local_page_adds_and_removes_live_profiles_without_restarting_agent()
    {
        using var east = new ServerFixture(0x45);
        using var west = new ServerFixture(0x57);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        await east.StartAsync(token);
        await west.StartAsync(token);
        var directory = Directory.CreateTempSubdirectory("relaylink-live-edit-");
        var configPath = Path.Combine(directory.FullName, "agent.json");
        var dashboardPort = FreePort();
        var config = new AgentProcessConfiguration { DashboardPort = dashboardPort, Servers = [east.Profile("east")] };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
        var runtime = new AgentProcessRuntime(config, new AgentConfigurationPath(configPath), NullLoggerFactory.Instance);
        using var dashboard = new AgentLocalDashboard(config, runtime);
        try
        {
            await runtime.StartAsync(token);
            await dashboard.StartAsync(token);
            using var http = new HttpClient(new HttpClientHandler { UseCookies = true })
            { BaseAddress = new Uri($"http://127.0.0.1:{dashboardPort}") };
            await WaitUntilAsync(() => runtime.Snapshot.Count == 1 && runtime.Snapshot[0].Status.Online, token);
            var session = await http.GetFromJsonAsync<JsonElement>("/api/v2/admin/session", token);
            var csrf = session.GetProperty("csrfToken").GetString()!;
            var version = session.GetProperty("version").GetString()!;
            var origin = http.BaseAddress!.GetLeftPart(UriPartial.Authority);

            using (var added = await WriteAsync(http, HttpMethod.Post, "/api/v2/admin/servers", origin, csrf,
                new { version, server = west.Profile("west"), confirmExistingState = false }, token))
            {
                added.EnsureSuccessStatusCode();
                version = (await added.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("version").GetString()!;
            }
            await WaitUntilAsync(() => runtime.Snapshot.Count == 2 && runtime.Snapshot.All(item => item.Status.Online), token);
            Assert.Equal((byte)0x57, (await RoundTripAsync(west.ProxyPort, new byte[] { 1, 2, 3 }, token))[0]);

            using var eastActive = new TcpClient();
            await eastActive.ConnectAsync(IPAddress.Loopback, east.ProxyPort, token);
            var eastStream = eastActive.GetStream();
            await eastStream.WriteAsync(new byte[] { 0x31 }, token);
            await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").ActiveConnections == 1, token);

            var identityPath = Path.Combine(directory.FullName, "state", "east", "identity.pfx");
            var identity = File.ReadAllBytes(identityPath);
            using (var disabled = await WriteAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east/enabled", origin, csrf,
                new { version, enabled = false }, token))
            {
                disabled.EnsureSuccessStatusCode();
                version = (await disabled.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("version").GetString()!;
            }
            Assert.False(runtime.Snapshot.Single(item => item.ProfileId == "east").Enabled);
            Assert.False(runtime.Snapshot.Single(item => item.ProfileId == "east").Status.Online);
            Assert.False(AgentProcessConfigurationLoader.Load(configPath).Servers.Single(item => item.ProfileId == "east").Enabled);
            try { Assert.Equal(0, await eastStream.ReadAsync(new byte[1], token)); }
            catch (IOException) { /* a reset also proves the disabled stream ended */ }
            Assert.True(runtime.Snapshot.Single(item => item.ProfileId == "west").Status.Online);
            Assert.Equal((byte)0x57, (await RoundTripAsync(west.ProxyPort, new byte[] { 4 }, token))[0]);

            using (var enabled = await WriteAsync(http, HttpMethod.Put, "/api/v2/admin/servers/east/enabled", origin, csrf,
                new { version, enabled = true }, token))
            {
                enabled.EnsureSuccessStatusCode();
                version = (await enabled.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("version").GetString()!;
            }
            await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").Status.Online, token);
            Assert.Equal(identity, File.ReadAllBytes(identityPath));
            Assert.Equal((byte)0x45, (await RoundTripAsync(east.ProxyPort, new byte[] { 5 }, token))[0]);

            using var eastActiveAgain = new TcpClient();
            await eastActiveAgain.ConnectAsync(IPAddress.Loopback, east.ProxyPort, token);
            var eastStreamAgain = eastActiveAgain.GetStream();
            await eastStreamAgain.WriteAsync(new byte[] { 0x32 }, token);
            await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").ActiveConnections == 1, token);

            using (var removed = await WriteAsync(http, HttpMethod.Delete, "/api/v2/admin/servers/east", origin, csrf, new { version }, token))
            {
                removed.EnsureSuccessStatusCode();
                version = (await removed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("version").GetString()!;
            }
            Assert.Equal("west", Assert.Single(runtime.Snapshot).ProfileId);
            Assert.True(runtime.Snapshot[0].Status.Online);
            Assert.True(File.Exists(east.ClientPath));
            try { Assert.Equal(0, await eastStreamAgain.ReadAsync(new byte[1], token)); }
            catch (IOException) { /* a reset also proves the removed stream ended */ }
            Assert.Equal((byte)0x57, (await RoundTripAsync(west.ProxyPort, new byte[] { 4, 5, 6 }, token))[0]);

            using (var removedLast = await WriteAsync(http, HttpMethod.Delete, "/api/v2/admin/servers/west", origin, csrf, new { version }, token))
            {
                removedLast.EnsureSuccessStatusCode();
                version = (await removedLast.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("version").GetString()!;
            }
            Assert.Empty(runtime.Snapshot);
            using var status = await http.GetFromJsonAsync<JsonDocument>("/api/v2/status", token);
            Assert.Empty(status!.RootElement.GetProperty("servers").EnumerateArray());
            Assert.Empty(AgentProcessConfigurationLoader.Load(configPath).Servers);

            var absentControlPort = FreePort();
            int absentDataPort;
            do { absentDataPort = FreePort(); } while (absentDataPort == absentControlPort);
            var unreachable = east.Profile("north") with { ServerPort = absentControlPort, DataPort = absentDataPort };
            using (var addedOffline = await WriteAsync(http, HttpMethod.Post, "/api/v2/admin/servers", origin, csrf,
                new { version, server = unreachable, confirmExistingState = false }, token))
                addedOffline.EnsureSuccessStatusCode();
            Assert.Equal("north", Assert.Single(runtime.Snapshot).ProfileId);
            Assert.False(runtime.Snapshot[0].Status.Online);
            Assert.Equal("north", Assert.Single(AgentProcessConfigurationLoader.Load(configPath).Servers).ProfileId);
        }
        finally
        {
            await dashboard.StopAsync(CancellationToken.None);
            await runtime.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task One_agent_keeps_two_same_id_servers_isolated_during_parallel_traffic_and_one_failure()
    {
        using var east = new ServerFixture(0x45);
        using var west = new ServerFixture(0x57);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        await east.StartAsync(token);
        await west.StartAsync(token);
        var agentDirectory = Directory.CreateTempSubdirectory("relaylink-multi-agent-");
        var configPath = Path.Combine(agentDirectory.FullName, "agent.json");
        var config = new AgentProcessConfiguration
        {
            DashboardPort = 0,
            Servers = [east.Profile("east") with { MaxConnections = 1, MaxPendingConnections = 1 }, west.Profile("west")]
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
        var runtime = new AgentProcessRuntime(config, new AgentConfigurationPath(configPath), NullLoggerFactory.Instance);
        try
        {
            await runtime.StartAsync(token);
            await WaitUntilAsync(() => runtime.Snapshot.Count == 2 && runtime.Snapshot.All(item => item.Status.Online), token);
            Assert.Equal(["east", "west"], runtime.Snapshot.Select(item => item.ProfileId));
            Assert.All(runtime.Snapshot, item => Assert.Equal("same-agent", item.Status.ClientId));
            Assert.All(runtime.Snapshot, item => Assert.Equal("echo", Assert.Single(item.Status.Channels).ChannelId));

            var payloadA = RandomNumberGenerator.GetBytes(128 * 1024);
            var payloadB = RandomNumberGenerator.GetBytes(128 * 1024);
            var results = await Task.WhenAll(
                RoundTripAsync(east.ProxyPort, payloadA, token),
                RoundTripAsync(west.ProxyPort, payloadB, token));
            Assert.Equal((byte)0x45, results[0][0]);
            Assert.Equal(payloadA, results[0][1..]);
            Assert.Equal((byte)0x57, results[1][0]);
            Assert.Equal(payloadB, results[1][1..]);

            var identities = new[] { "east", "west" }.Select(profile =>
                File.ReadAllBytes(Path.Combine(agentDirectory.FullName, "state", profile, "identity.pfx"))).ToArray();
            Assert.NotEqual(identities[0], identities[1]);
            Assert.NotEqual(east.CaPemBase64, west.CaPemBase64);
            await RejectWrongSecretAsync(west.TunnelPort, east.Secret, token);
            await RejectCrossServerDataTokenAsync(east, west, token);

            using (var saturated = new TcpClient())
            {
                await saturated.ConnectAsync(IPAddress.Loopback, east.ProxyPort, token);
                await saturated.GetStream().WriteAsync(new byte[] { 0x21 }, token);
                await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").ActiveConnections == 1, token);
                using var rejected = new TcpClient();
                await rejected.ConnectAsync(IPAddress.Loopback, east.ProxyPort, token);
                await rejected.GetStream().WriteAsync(new byte[] { 0x22 }, token);
                await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").CapacityRejected > 0, token);
                var independent = await RoundTripAsync(west.ProxyPort, RandomNumberGenerator.GetBytes(1024), token);
                Assert.Equal((byte)0x57, independent[0]);
            }
            await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").ActiveConnections == 0, token);
            Assert.Equal((byte)0x45, (await RoundTripAsync(east.ProxyPort, new byte[] { 0x23 }, token))[0]);
            await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").ActiveConnections == 0, token);

            foreach (var profile in new[] { "east", "west" })
            {
                var diagnostics = Path.Combine(agentDirectory.FullName, "state", profile, "relaylink-diagnostics.jsonl");
                await WaitUntilAsync(() => File.Exists(diagnostics), token);
                var recorded = await File.ReadAllTextAsync(diagnostics, token);
                Assert.DoesNotContain(east.Secret, recorded, StringComparison.Ordinal);
                Assert.DoesNotContain(west.Secret, recorded, StringComparison.Ordinal);
                Assert.DoesNotContain(east.CaPemBase64, recorded, StringComparison.Ordinal);
                Assert.DoesNotContain(west.CaPemBase64, recorded, StringComparison.Ordinal);
            }

            using var surviving = new TcpClient();
            await surviving.ConnectAsync(IPAddress.Loopback, west.ProxyPort, token);
            var survivingStream = surviving.GetStream();
            var longPayload = RandomNumberGenerator.GetBytes(64 * 1024);
            await survivingStream.WriteAsync(longPayload.AsMemory(0, longPayload.Length / 2), token);
            east.Stop();
            await WaitUntilAsync(() => runtime.Snapshot.Single(item => item.ProfileId == "east").Status.Online == false, token);
            Assert.True(runtime.Snapshot.Single(item => item.ProfileId == "west").Status.Online);
            await survivingStream.WriteAsync(longPayload.AsMemory(longPayload.Length / 2), token);
            surviving.Client.Shutdown(SocketShutdown.Send);
            var longResult = new byte[longPayload.Length + 1];
            await survivingStream.ReadExactlyAsync(longResult, token);
            Assert.Equal((byte)0x57, longResult[0]);
            Assert.Equal(longPayload, longResult[1..]);
            var afterFailure = await RoundTripAsync(west.ProxyPort, RandomNumberGenerator.GetBytes(4096), token);
            Assert.Equal((byte)0x57, afterFailure[0]);
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
            agentDirectory.Delete(recursive: true);
        }
    }

    private static async Task<byte[]> RoundTripAsync(int port, byte[] payload, CancellationToken token)
    {
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, port, token);
        var stream = caller.GetStream();
        await stream.WriteAsync(payload, token);
        caller.Client.Shutdown(SocketShutdown.Send);
        var result = new byte[payload.Length + 1];
        await stream.ReadExactlyAsync(result, token);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
        return result;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToolAsync(string assembly, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(assembly);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static Task<HttpResponseMessage> WriteAsync(HttpClient http, HttpMethod method, string path, string origin, string csrf, object body, CancellationToken token)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("X-RelayLink-CSRF", csrf);
        return http.SendAsync(request, token);
    }

    private static async Task RejectWrongSecretAsync(int port, string secret, CancellationToken token)
    {
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, port, token);
        using var tls = new SslStream(connection.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1" }, token);
        var writer = new FrameWriter(tls);
        var reader = new FrameReader(tls);
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(
            new RegisterMessage("same-agent", secret, "integration-test", new string('A', 64)))), token);
        var response = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.True(response is null || response.Type == FrameType.Error);
    }

    private static async Task RejectCrossServerDataTokenAsync(ServerFixture east, ServerFixture west, CancellationToken token)
    {
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, east.TunnelPort, token);
        using var tls = new SslStream(control.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1" }, token);
        var reader = new FrameReader(tls);
        var writer = new FrameWriter(tls);
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(
            new RegisterMessage("token-probe", east.TokenProbeSecret, "integration-test", new string('A', 64)))), token);
        var acceptedFrame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.RegisterAccepted, acceptedFrame?.Type);
        var accepted = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(acceptedFrame!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(
            new ConfigAckMessage(accepted.SessionId, accepted.ConfigVersion))), token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, east.TokenProbePort, token);
        var openFrame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.Open, openFrame?.Type);
        var open = JsonProtocolSerializer.Deserialize<OpenMessage>(openFrame!.Payload.Span);

        using var wrong = new TcpClient();
        await wrong.ConnectAsync(IPAddress.Loopback, west.DataPort, token);
        await new FrameWriter(wrong.GetStream()).WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(
            new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), token);
        var wrongResponse = await new FrameReader(wrong.GetStream()).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token);
        Assert.True(wrongResponse is null || wrongResponse.Type == FrameType.Error);

        using var correct = new TcpClient();
        await correct.ConnectAsync(IPAddress.Loopback, east.DataPort, token);
        await new FrameWriter(correct.GetStream()).WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(
            new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), token);
        Assert.Equal(FrameType.BindAccepted, (await new FrameReader(correct.GetStream()).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(50, token);
    }

    private sealed class ServerFixture(byte marker) : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"relaylink-multi-server-{Guid.NewGuid():N}");
        private readonly TcpListener target = new(IPAddress.Loopback, 0);
        private Process? process;
        private Task? targetLoop;
        public int TunnelPort { get; } = FreePort();
        public int DataPort { get; } = FreePort();
        public int ProxyPort { get; } = FreePort();
        public int DashboardPort { get; } = FreePort();
        public int TargetPort => ((IPEndPoint)target.LocalEndpoint).Port;
        public string Secret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string ClientPath => Path.Combine(directory, "clients", "same-agent.json");
        public string TargetSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string AccessSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string TokenProbeSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public int TokenProbePort { get; } = FreePort();
        public string CaPemBase64 { get; private set; } = string.Empty;

        public AgentServerProfile Profile(string id) => new()
        {
            ProfileId = id, ServerHost = "127.0.0.1", ServerPort = TunnelPort, DataPort = DataPort,
            ClientId = "same-agent", Secret = Secret, UseTls = true, TrustedCaPemBase64 = CaPemBase64,
            MaxConnections = 20, MaxPendingConnections = 10,
            Reconnect = new ReconnectConfiguration(1, 2, 3)
        };

        public AgentServerProfile TargetProfile(string id) => Profile(id) with { ClientId = "target", Secret = TargetSecret };

        public async Task StartAsync(CancellationToken token, string? targetFingerprint = null)
        {
            Directory.CreateDirectory(Path.Combine(directory, "clients"));
            target.Start();
            targetLoop = AcceptTargetsAsync();
            var certificatePath = Path.Combine(directory, "server-cert.pem");
            var keyPath = Path.Combine(directory, "server-key.pem");
            CreateOuterCertificate(certificatePath, keyPath);
            var echo = new ChannelConfiguration("echo", "Echo", true, "127.0.0.1", ProxyPort,
                "127.0.0.1", TargetPort, 20, 5);
            var caller = new ClientConfiguration(1, "same-agent", "Same Agent", true, Secret, 20, 10, [echo]);
            if (targetFingerprint is not null)
            {
                caller = caller with { OutboundMappings =
                [new OutboundMappingConfiguration("to-target", true, "127.0.0.1", "target", "private", AccessSecret, targetFingerprint)] };
                var targetChannel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", FreePort(),
                    "127.0.0.1", TargetPort, 20, 5)
                { AuthorizedClientsOnly = true, EndToEndEncryptionEnabled = false, AccessSecret = AccessSecret };
                var targetClient = new ClientConfiguration(1, "target", "Target", true, TargetSecret, 20, 10, [targetChannel])
                { E2eCertificateSha256 = targetFingerprint };
                await File.WriteAllTextAsync(Path.Combine(directory, "clients", "target.json"),
                    JsonSerializer.Serialize(targetClient, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "clients", "same-agent.json"),
                JsonSerializer.Serialize(caller, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
            var probe = new ClientConfiguration(1, "token-probe", "Token Probe", true, TokenProbeSecret, 5, 2,
            [new ChannelConfiguration("probe", "Probe", true, "127.0.0.1", TokenProbePort,
                "127.0.0.1", TargetPort, 5, 5)]);
            await File.WriteAllTextAsync(Path.Combine(directory, "clients", "token-probe.json"),
                JsonSerializer.Serialize(probe, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
            var configPath = Path.Combine(directory, "server.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tunnel = new { listenAddress = "127.0.0.1", port = TunnelPort, dataPort = DataPort,
                    tlsEnabled = true, certificatePemPath = certificatePath, privateKeyPemPath = keyPath,
                    handshakeTimeoutSeconds = 10, heartbeatIntervalSeconds = 2, heartbeatTimeoutSeconds = 8 },
                dashboard = new { listenAddress = "127.0.0.1", port = DashboardPort, refreshSeconds = 5,
                    admin = new { username = "admin", passwordHash = PasswordHash(), sessionLifetimeMinutes = 60 } },
                clientsDirectory = Path.Combine(directory, "clients"),
                limits = new { maxConnections = 40, maxPendingConnections = 20, maxUnauthenticatedConnections = 10,
                    maxChannelsPerClient = 10, openTimeoutSeconds = 10, blockedWriteTimeoutSeconds = 120,
                    halfCloseDrainTimeoutSeconds = 300 }
            }), token);
            process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { typeof(RelayLink.Server.Configuration.ConfigurationLoader).Assembly.Location, "--config", configPath },
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
            })!;
            using var http = new HttpClient();
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException($"Server exited: {await process.StandardError.ReadToEndAsync(token)}");
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{DashboardPort}/health/ready", token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(50, token);
            }
        }

        private async Task AcceptTargetsAsync()
        {
            try
            {
                while (true)
                {
                    var client = await target.AcceptTcpClientAsync();
                    _ = EchoAsync(client);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task EchoAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var payload = new MemoryStream();
                await stream.CopyToAsync(payload);
                await stream.WriteAsync(new[] { marker });
                await stream.WriteAsync(payload.ToArray());
            }
        }

        public void Stop()
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.WaitForExit();
        }

        public void Dispose()
        {
            Stop();
            process?.Dispose();
            target.Stop();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }

        private static string PasswordHash()
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 100_000, HashAlgorithmName.SHA256, 32);
            return $"PBKDF2-SHA256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        private void CreateOuterCertificate(string certificatePath, string keyPath)
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=RelayLink Test Root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            CaPemBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(root.ExportCertificatePem()));
            using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(san.Build());
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
            using var certificate = serverRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2), RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            File.WriteAllText(keyPath, serverKey.ExportPkcs8PrivateKeyPem());
        }
    }

    private static int FreePort()
    {
        lock (PortGate)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (AssignedPorts.Add(port)) return port;
            }
            throw new InvalidOperationException("Unable to allocate a distinct integration-test port.");
        }
    }

    private static int FindAdjacentFreePorts()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var port = FreePort();
            if (port == 65535) continue;
            try
            {
                using var first = new TcpListener(IPAddress.Loopback, port);
                using var second = new TcpListener(IPAddress.Loopback, port + 1);
                first.Start();
                second.Start();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("Could not find two free adjacent loopback ports.");
    }
}
