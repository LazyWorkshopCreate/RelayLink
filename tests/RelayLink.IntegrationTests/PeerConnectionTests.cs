using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayLink.Agent;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Transport;

namespace RelayLink.IntegrationTests;

public sealed class PeerConnectionTests
{
#if PEER_TLS_TESTS
    [Fact]
#else
    [Fact(Skip = "Opt in on a TLS-capable host with -p:EnablePeerTlsTests=true.")]
#endif
    public async Task Two_agents_relay_private_channel_with_end_to_end_tls_and_half_close()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.WaitOnlineAsync(deadline.Token);

        using var dashboardHttp = new HttpClient();
        var agentPage = await dashboardHttp.GetStringAsync($"http://127.0.0.1:{fixture.AgentDashboardPort}/", deadline.Token);
        Assert.Contains($"127.0.0.1:{fixture.LocalPort}", agentPage);
        Assert.Contains("to-visited", agentPage);
        Assert.DoesNotContain("accessSecret", agentPage, StringComparison.OrdinalIgnoreCase);

        using var caller = await fixture.ConnectLocalAsync(deadline.Token);
        try
        {
            var stream = caller.GetStream();
            var payload = RandomNumberGenerator.GetBytes(128 * 1024);
            await stream.WriteAsync(payload, deadline.Token);
            caller.Client.Shutdown(SocketShutdown.Send);
            var result = new byte[payload.Length];
            await stream.ReadExactlyAsync(result, deadline.Token);
            Assert.Equal(payload, result);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
            Assert.Equal(1, fixture.TargetConnections);
        }
        catch (Exception exception) { throw new InvalidOperationException(fixture.Logs(), exception); }

        using var forbidden = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await forbidden.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, deadline.Token));
    }

    [Fact]
    public async Task Private_channel_does_not_open_anonymous_proxy_or_expose_access_secret()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var forbidden = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<SocketException>(async () => await forbidden.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, deadline.Token));
        using var http = new HttpClient();
        var response = await http.GetStringAsync($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", deadline.Token);
        Assert.DoesNotContain("accessSecret", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorizedClientsOnly", response, StringComparison.Ordinal);
    }

#if PEER_TLS_TESTS
    [Fact]
    public async Task Two_private_channels_keep_concurrent_large_flows_isolated()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await fixture.WaitOnlineAsync(deadline.Token);
        var tasks = Enumerable.Range(0, 16).Select(async index =>
        {
            var second = index % 2 == 1;
            using var caller = await fixture.ConnectLocalAsync(second ? fixture.LocalPortB : fixture.LocalPort, deadline.Token);
            var stream = caller.GetStream();
            var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
            await stream.WriteAsync(payload, deadline.Token);
            caller.Client.Shutdown(SocketShutdown.Send);
            if (second)
            {
                var marker = new byte[1];
                await stream.ReadExactlyAsync(marker, deadline.Token);
                Assert.Equal((byte)'B', marker[0]);
            }
            var result = new byte[payload.Length];
            await stream.ReadExactlyAsync(result, deadline.Token);
            Assert.Equal(payload, result);
        });
        try { await Task.WhenAll(tasks); }
        catch (Exception exception) { throw new InvalidOperationException(fixture.Logs(), exception); }
        Assert.Equal(8, fixture.TargetConnections);
        Assert.Equal(8, fixture.TargetConnectionsB);
        using var http = new HttpClient();
        using var channelsJson = JsonDocument.Parse(await http.GetStringAsync($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", deadline.Token));
        var channels = channelsJson.RootElement.GetProperty("channels").EnumerateArray().ToArray();
        Assert.Equal(2, channels.Length);
        foreach (var channel in channels)
        {
            Assert.Equal(0, channel.GetProperty("bytesToTarget").GetInt64());
            Assert.Equal(0, channel.GetProperty("bytesToCaller").GetInt64());
            Assert.True(channel.GetProperty("peerCiphertextToTarget").GetInt64() >= 8L * 1024 * 1024);
            Assert.True(channel.GetProperty("peerCiphertextToCaller").GetInt64() >= 8L * 1024 * 1024);
        }
    }

    [Fact]
    public async Task Restarted_agent_rotates_occupied_port_and_reports_current_address()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await fixture.WaitOnlineAsync(deadline.Token);
        var oldPort = fixture.LocalPort;
        await fixture.StopCallerAsync();
        await fixture.WaitOnlineAsync(deadline.Token, expected: 1);
        using var occupied = new TcpListener(IPAddress.Loopback, oldPort);
        occupied.Start();
        fixture.StartCaller();
        await fixture.WaitOnlineAsync(deadline.Token);
        var newPort = await fixture.WaitChangedPortAsync("to-visited", oldPort, deadline.Token);
        Assert.NotEqual(oldPort, newPort);
        using var connection = await fixture.ConnectLocalAsync(newPort, deadline.Token);
        var stream = connection.GetStream();
        var payload = RandomNumberGenerator.GetBytes(4096);
        await stream.WriteAsync(payload, deadline.Token);
        connection.Client.Shutdown(SocketShutdown.Send);
        var echoed = new byte[payload.Length];
        await stream.ReadExactlyAsync(echoed, deadline.Token);
        Assert.Equal(payload, echoed);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session", new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        var endpoint = $"http://{fixture.DashboardAddress}/api/v1/admin/clients/caller/mappings";
        using var response = await http.GetAsync(endpoint, deadline.Token);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token);
        var mapping = report.GetProperty("mappings").EnumerateArray().Single(item => item.GetProperty("mappingId").GetString() == "to-visited");
        Assert.True(mapping.GetProperty("available").GetBoolean());
        Assert.Equal(newPort, mapping.GetProperty("localPort").GetInt32());
    }
#endif

#if PEER_TLS_TESTS
    [Fact]
    public async Task Registered_client_without_mapping_cannot_request_private_channel()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var intruder = new TcpClient();
        await intruder.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, deadline.Token);
        await using var outerTls = new SslStream(intruder.GetStream(), false, (_, _, _, _) => true);
        await outerTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, deadline.Token);
        var reader = new FrameReader(outerTls);
        var writer = new FrameWriter(outerTls);
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("intruder", fixture.IntruderSecret, "test"))), deadline.Token);
        var accepted = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token);
        Assert.Equal(FrameType.RegisterAccepted, accepted?.Type);
        var registration = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registration.SessionId, registration.ConfigVersion))), deadline.Token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token))?.Type);
        var requestId = Guid.NewGuid();
        await writer.WriteAsync(new Frame(FrameType.PeerOpenRequest, JsonProtocolSerializer.Serialize(new PeerOpenRequestMessage(requestId, "to-visited"))), deadline.Token);
        var rejected = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token);
        Assert.Equal(FrameType.PeerOpenRejected, rejected?.Type);
        Assert.Equal(requestId, JsonProtocolSerializer.Deserialize<PeerOpenRejectedMessage>(rejected!.Payload.Span).RequestId);
        Assert.Equal(0, fixture.TargetConnections);
    }
#endif

#if PEER_TLS_TESTS
    [Fact]
    public async Task Admin_mapping_save_is_authorized_and_pushed_to_online_agent()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.WaitOnlineAsync(deadline.Token);
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        var endpoint = $"http://{fixture.DashboardAddress}/api/v1/admin/clients/caller/mappings";
        var mapping = new { mappingId = "dynamic", enabled = true, targetClientId = "visited", targetChannelId = "private" };

        using var unauthorized = await http.PostAsJsonAsync(endpoint, mapping, deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session", new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token);
        var csrf = session.GetProperty("csrfToken").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(mapping) };
        request.Headers.Add("X-RelayLink-CSRF", csrf);
        using var saved = await http.SendAsync(request, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.DoesNotContain("accessSecret", await saved.Content.ReadAsStringAsync(deadline.Token), StringComparison.OrdinalIgnoreCase);
        var assigned = await fixture.WaitPortAsync("dynamic", deadline.Token);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var reported = await http.GetAsync(endpoint, deadline.Token);
            var document = await reported.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token);
            var current = document.GetProperty("mappings").EnumerateArray().Single(item => item.GetProperty("mappingId").GetString() == "dynamic");
            if (current.GetProperty("available").GetBoolean())
            {
                Assert.Equal(assigned, current.GetProperty("localPort").GetInt32());
                break;
            }
            Assert.True(attempt < 49, "Agent did not report its selected port to the server.");
            await Task.Delay(100, deadline.Token);
        }
        using var caller = await fixture.ConnectLocalAsync(assigned, deadline.Token);
        var stream = caller.GetStream();
        var payload = RandomNumberGenerator.GetBytes(32 * 1024);
        await stream.WriteAsync(payload, deadline.Token);
        caller.Client.Shutdown(SocketShutdown.Send);
        var result = new byte[payload.Length];
        await stream.ReadExactlyAsync(result, deadline.Token);
        Assert.Equal(payload, result);
        Assert.Equal(1, fixture.TargetConnections);
    }

    [Fact]
    public async Task Wrong_end_to_end_access_proof_never_connects_target()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync(startCallerAgent: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.WaitOnlineAsync(deadline.Token, expected: 1);
        using var controlClient = new TcpClient();
        await controlClient.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, deadline.Token);
        await using var outer = await fixture.AuthenticateOuterAsync(controlClient, deadline.Token);
        var reader = new FrameReader(outer);
        var writer = new FrameWriter(outer);
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("caller", fixture.CallerSecret, "negative-test"))), deadline.Token);
        var registrationFrame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token);
        Assert.Equal(FrameType.RegisterAccepted, registrationFrame?.Type);
        var registration = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(registrationFrame!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registration.SessionId, registration.ConfigVersion))), deadline.Token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token))?.Type);
        var requestId = Guid.NewGuid();
        await writer.WriteAsync(new Frame(FrameType.PeerOpenRequest, JsonProtocolSerializer.Serialize(new PeerOpenRequestMessage(requestId, "to-visited"))), deadline.Token);
        var grantFrame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token);
        Assert.Equal(FrameType.PeerOpenGranted, grantFrame?.Type);
        var grant = JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>(grantFrame!.Payload.Span);
        using var dataClient = new TcpClient();
        await dataClient.ConnectAsync(IPAddress.Loopback, fixture.DataPort, deadline.Token);
        await using var dataOuter = dataClient.GetStream();
        var dataReader = new FrameReader(dataOuter);
        var dataWriter = new FrameWriter(dataOuter);
        await dataWriter.WriteAsync(new Frame(FrameType.PeerBindData, JsonProtocolSerializer.Serialize(new PeerBindDataMessage(grant.ConnectionId, grant.SessionId, grant.Token, "caller"))), deadline.Token);
        Assert.Equal(FrameType.PeerBindAccepted, (await dataReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, deadline.Token))?.Type);
        using var inner = new SslStream(new FramedDuplexStream(dataReader, dataWriter), leaveInnerStreamOpen: true,
            (_, certificate, _, _) => certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(fixture.TargetFingerprint)));
        await inner.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "visited", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
        var challenge = new byte[32];
        await inner.ReadExactlyAsync(challenge, deadline.Token);
        await inner.WriteAsync(new byte[32], deadline.Token);
        try { Assert.Equal(0, await inner.ReadAsync(new byte[1], deadline.Token)); }
        catch (IOException) { }
        Assert.Equal(0, fixture.TargetConnections);
    }
#endif

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"relaylink-peer-{Guid.NewGuid():N}");
        private readonly TcpListener target = new(IPAddress.Loopback, 0);
        private readonly TcpListener targetB = new(IPAddress.Loopback, 0);
        private readonly List<Process> processes = [];
        private readonly List<StringBuilder> processLogs = [];
        private int targetConnections;
        private int targetConnectionsB;
        public int TargetConnections => Volatile.Read(ref targetConnections);
        public int TargetConnectionsB => Volatile.Read(ref targetConnectionsB);
        public int LocalPort => ReadPort("to-visited");
        public int LocalPortB => ReadPort("to-visited-b");
        public int AgentDashboardPort { get; } = FreePort();
        public int ProxyPort { get; } = FreePort();
        public int TunnelPort { get; } = FreePort();
        public int DataPort { get; } = FreePort();
        private int DashboardPort { get; } = FreePort();
        private string caPath = string.Empty;
        public string DashboardAddress => $"127.0.0.1:{DashboardPort}";
        public string IntruderSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string CallerSecret { get; private set; } = string.Empty;
        public string TargetFingerprint { get; private set; } = string.Empty;

        public async Task StartAsync(bool startCallerAgent = true)
        {
            var clients = Path.Combine(directory, "clients");
            var visitedDirectory = Path.Combine(directory, "visited");
            var callerDirectory = Path.Combine(directory, "caller");
            Directory.CreateDirectory(clients);
            Directory.CreateDirectory(visitedDirectory);
            Directory.CreateDirectory(callerDirectory);
            target.Start();
            targetB.Start();
            _ = EchoAsync(target, false);
            _ = EchoAsync(targetB, true);
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            var targetPortB = ((IPEndPoint)targetB.LocalEndpoint).Port;
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var accessSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var accessSecretB = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var fingerprint = CreateIdentity(Path.Combine(visitedDirectory, "visited.e2e.pfx"));
            TargetFingerprint = fingerprint;
            var visited = new ClientConfiguration(1, "visited", "Visited", true, secret, 30, 20,
            [new ChannelConfiguration("private", "Private", true, "127.0.0.1", ProxyPort, "127.0.0.1", targetPort, 10, 5)
            { AuthorizedClientsOnly = true, AccessSecret = accessSecret, E2eCertificateSha256 = fingerprint },
             new ChannelConfiguration("private-b", "Private B", true, "127.0.0.1", FreePort(), "127.0.0.1", targetPortB, 10, 5)
            { AuthorizedClientsOnly = true, AccessSecret = accessSecretB, E2eCertificateSha256 = fingerprint }]);
            var callerSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            CallerSecret = callerSecret;
            var caller = new ClientConfiguration(1, "caller", "Caller", true, callerSecret, 30, 20, [])
            { OutboundMappings = [new OutboundMappingConfiguration("to-visited", true, "127.0.0.1", "visited", "private", accessSecret, fingerprint),
                                  new OutboundMappingConfiguration("to-visited-b", true, "127.0.0.1", "visited", "private-b", accessSecretB, fingerprint)] };
            var intruder = new ClientConfiguration(1, "intruder", "Intruder", true, IntruderSecret, 5, 2, []);
            var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            await File.WriteAllTextAsync(Path.Combine(clients, "visited.json"), JsonSerializer.Serialize(visited, json));
            await File.WriteAllTextAsync(Path.Combine(clients, "caller.json"), JsonSerializer.Serialize(caller, json));
            await File.WriteAllTextAsync(Path.Combine(clients, "intruder.json"), JsonSerializer.Serialize(intruder, json));
            caPath = Path.Combine(directory, "root-ca.pem");
            var certificatePath = Path.Combine(directory, "server-cert.pem");
            var keyPath = Path.Combine(directory, "server-key.pem");
            CreateOuterCertificate(caPath, certificatePath, keyPath);
            var serverConfig = new ServerConfiguration(1,
                new TunnelConfiguration("127.0.0.1", TunnelPort, true, certificatePath, keyPath, 10, 15, 45) { DataPort = DataPort },
                new DashboardConfiguration("127.0.0.1", DashboardPort, 5, new DashboardAdminConfiguration("admin", PasswordHash(), 60)),
                clients, new LimitsConfiguration(50, 20, 50, 10, 20, 120, 300), null);
            var serverPath = Path.Combine(directory, "server.json");
            await File.WriteAllTextAsync(serverPath, JsonSerializer.Serialize(serverConfig, json));
            StartProcess(typeof(ConfigurationLoader).Assembly.Location, serverPath);
            await WaitReadyAsync();

            await File.WriteAllTextAsync(Path.Combine(visitedDirectory, "agent.json"), AgentJson("visited", secret));
            await File.WriteAllTextAsync(Path.Combine(callerDirectory, "agent.json"), AgentJson("caller", callerSecret));
            StartProcess(typeof(ControlSessionWorker).Assembly.Location, Path.Combine(visitedDirectory, "agent.json"));
            if (startCallerAgent) StartProcess(typeof(ControlSessionWorker).Assembly.Location, Path.Combine(callerDirectory, "agent.json"));
        }

        public async Task WaitOnlineAsync(CancellationToken token, int expected = 2)
        {
            using var http = new HttpClient();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{DashboardPort}/api/v1/overview", token);
                    var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                    if (json.GetProperty("clientsOnline").GetInt32() == expected && (expected == 1 || (TryReadPort("to-visited", out _) && TryReadPort("to-visited-b", out _)))) return;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"Agents did not become online. {Logs()}");
                }
                if (processes.Any(process => process.HasExited)) throw new InvalidOperationException(Logs());
                try { await Task.Delay(100, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"Agents did not become online. {Logs()}");
                }
            }
        }

        public async Task<SslStream> AuthenticateOuterAsync(TcpClient client, CancellationToken token)
        {
            var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, token);
            return tls;
        }

        public Task<TcpClient> ConnectLocalAsync(CancellationToken token) => ConnectLocalAsync(LocalPort, token);

        public async Task<int> WaitPortAsync(string mappingId, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (TryReadPort(mappingId, out var port)) return port;
                await Task.Delay(100, token);
            }
            throw new OperationCanceledException(token);
        }

        public async Task<int> WaitChangedPortAsync(string mappingId, int previous, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (TryReadPort(mappingId, out var port) && port != previous) return port;
                await Task.Delay(100, token);
            }
            throw new OperationCanceledException(token);
        }

        public async Task StopCallerAsync()
        {
            var process = processes[2];
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            processes.RemoveAt(2);
            processLogs.RemoveAt(2);
        }

        public void StartCaller() => StartProcess(typeof(ControlSessionWorker).Assembly.Location, Path.Combine(directory, "caller", "agent.json"));

        private int ReadPort(string mappingId) => TryReadPort(mappingId, out var port) ? port : throw new InvalidOperationException($"Port not reported for {mappingId}.");

        private bool TryReadPort(string mappingId, out int port)
        {
            port = 0;
            var path = Path.Combine(directory, "caller", "caller.ports.json");
            if (!File.Exists(path)) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty(mappingId, out var value)) return false;
            port = value.GetInt32();
            return port > 0;
        }

        public async Task<TcpClient> ConnectLocalAsync(int port, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var client = new TcpClient();
                try { await client.ConnectAsync(IPAddress.Loopback, port, token); return client; }
                catch (SocketException) { client.Dispose(); await Task.Delay(100, token); }
            }
            throw new OperationCanceledException(token);
        }

        private async Task WaitReadyAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var http = new HttpClient();
            while (!deadline.IsCancellationRequested)
            {
                try { if ((await http.GetAsync($"http://127.0.0.1:{DashboardPort}/health/ready", deadline.Token)).IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                if (processes.Any(process => process.HasExited)) throw new InvalidOperationException(Logs());
                await Task.Delay(100, deadline.Token);
            }
        }

        private async Task EchoAsync(TcpListener listener, bool second)
        {
            try
            {
                while (true)
                {
                    var connection = await listener.AcceptTcpClientAsync();
                    if (second) Interlocked.Increment(ref targetConnectionsB); else Interlocked.Increment(ref targetConnections);
                    _ = Task.Run(async () =>
                    {
                        using (connection)
                        using (var memory = new MemoryStream())
                        {
                            await connection.GetStream().CopyToAsync(memory);
                            if (second) await connection.GetStream().WriteAsync(new byte[] { (byte)'B' });
                            await connection.GetStream().WriteAsync(memory.ToArray());
                            connection.Client.Shutdown(SocketShutdown.Send);
                        }
                    });
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException) { }
        }

        private string AgentJson(string clientId, string secret) => JsonSerializer.Serialize(new
        {
            serverHost = "127.0.0.1", serverPort = TunnelPort, dataPort = DataPort, clientId, secret, useTls = true, trustedCaPemBase64 = Convert.ToBase64String(File.ReadAllBytes(caPath)),
            dashboardPort = clientId == "caller" ? AgentDashboardPort : 0,
            reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 2, permanentErrorDelaySeconds = 2 }
        });

        private void StartProcess(string assembly, string configurationPath)
        {
            var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{assembly}\" --config \"{configurationPath}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = new StringBuilder();
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            processes.Add(process);
            processLogs.Add(output);
        }

        private static string CreateIdentity(string path)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=RelayLink-visited", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
            return Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }

        private static void CreateOuterCertificate(string caPath, string certificatePath, string keyPath)
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=RelayLink Test Root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(caPath, root.ExportCertificatePem());
            using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(san.Build());
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], true));
            using var server = serverRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2), RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(certificatePath, server.ExportCertificatePem());
            File.WriteAllText(keyPath, serverKey.ExportPkcs8PrivateKeyPem());
        }

        private static string PasswordHash()
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 100_000, HashAlgorithmName.SHA256, 32);
            return $"PBKDF2-SHA256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public string Logs() => string.Join("\n", processLogs.Select((output, index) => $"process {index}: " + string.Join("\n", output.ToString().Split('\n').Where(line => line.Contains("RelayLink.") || line.Contains("Exception") || line.Contains("failed", StringComparison.OrdinalIgnoreCase)))));

        public void Dispose()
        {
            target.Stop();
            targetB.Stop();
            foreach (var process in processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
