using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using RelayLink.Protocol;
using RelayLink.Testing;
using RelayLink.Transport;
using RelayLink.Agent;
using Microsoft.Extensions.Logging;
using Serilog;

namespace RelayLink.IntegrationTests;

public sealed class ServerTunnelTests
{
    [Fact]
    public void Fixture_ports_stay_distinct_and_bound_until_the_server_starts()
    {
        using var fixture = new TunnelFixture();
        var ports = new[] { fixture.TunnelPort, fixture.DataPort, fixture.ProxyPort, fixture.DashboardPort };
        Assert.Equal(ports.Length, ports.Distinct().Count());
        using var thief = new TcpListener(IPAddress.Loopback, fixture.DataPort);
        Assert.Throws<SocketException>(() => thief.Start());
    }

#if LEGACY_AGENT_TESTS
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_v3_binary_connects_and_only_relays_tcp(bool udp)
    {
        var legacy = Environment.GetEnvironmentVariable("RELAYLINK_LEGACY_AGENT_ASSEMBLY")
            ?? throw new InvalidOperationException("Set RELAYLINK_LEGACY_AGENT_ASSEMBLY to an unmodified v0.1.0-rc.8 Agent DLL.");
        using var fixture = new TunnelFixture();
        await fixture.StartAsync(transportProtocol: udp ? "udp" : "tcp");
        await fixture.StartAgentAsync(legacy);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (udp)
        {
            using var caller = new UdpClient();
            caller.Connect(IPAddress.Loopback, fixture.ProxyPort);
            await caller.SendAsync(new byte[] { 42 }, deadline.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller.ReceiveAsync(timeout.Token));
        }
        else
        {
            using var caller = new TcpClient();
            await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, deadline.Token);
            var payload = RandomNumberGenerator.GetBytes(4096);
            await caller.GetStream().WriteAsync(payload, deadline.Token);
            var response = new byte[payload.Length];
            await caller.GetStream().ReadExactlyAsync(response, deadline.Token);
            Assert.Equal(payload, response);
        }
    }
#endif
    [Fact]
    public async Task V3_configuration_updates_keep_legacy_hash_and_remove_udp_channels()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var reader = new FrameReader(control.GetStream());
        var writer = new FrameWriter(control.GetStream(), protocolVersion: 3);
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(
            new RegisterMessage("test-agent", fixture.Secret, "v3-test", new string('A', 64)))), token);
        var accepted = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>((await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(accepted.SessionId, accepted.ConfigVersion))), token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        foreach (var protocol in new[] { "udp", "tcp" })
        {
            using var update = new HttpRequestMessage(HttpMethod.Put,
                $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent/channels/echo")
            { Content = JsonContent.Create(new { displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = fixture.ProxyPort,
                targetHost = "127.0.0.1", targetPort = fixture.TargetPort, maxConnections = 5, targetConnectTimeoutSeconds = 5, transportProtocol = protocol }) };
            update.Headers.Add("X-RelayLink-CSRF", csrf);
            using var saved = await http.SendAsync(update, token);
            saved.EnsureSuccessStatusCode();
            var frame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
            Assert.Equal(FrameType.ConfigUpdate, frame?.Type);
            var message = JsonProtocolSerializer.Deserialize<ConfigUpdateMessage>(frame!.Payload.Span);
            using var document = JsonDocument.Parse(frame.Payload);
            // The old binary hashes its own deserialized v3 shape; this wire fixture has exactly that shape.
            var rawConfig = document.RootElement.GetProperty("config").GetRawText();
            Assert.DoesNotContain("transportProtocol", rawConfig);
            Assert.DoesNotContain("udpIdleTimeoutSeconds", rawConfig);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawConfig))), message.ConfigVersion);
            Assert.Equal(protocol == "tcp" ? 1 : 0, message.Config.Channels.Count);
            await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(message.SessionId, message.ConfigVersion))), token);
        }
        var ping = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.Ping, ping?.Type);
        var sequence = JsonProtocolSerializer.Deserialize<PingMessage>(ping!.Payload.Span).Sequence;
        await writer.WriteAsync(new Frame(FrameType.Pong, JsonProtocolSerializer.Serialize(new PongMessage(sequence))), token);
    }

    [Fact]
    public async Task Udp_proxy_preserves_datagrams_isolates_sources_and_reclaims_idle_sessions()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync(transportProtocol: "udp");
        await fixture.StartAgentAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        first.Client.SendBufferSize = 128 * 1024;
        first.Connect(IPAddress.Loopback, fixture.ProxyPort);
        second.Connect(IPAddress.Loopback, fixture.ProxyPort);
        foreach (var size in new[] { 0, 1, 32769, 65507 })
        {
            var a = RandomNumberGenerator.GetBytes(size);
            var b = RandomNumberGenerator.GetBytes(19);
            await first.SendAsync(a, deadline.Token);
            await second.SendAsync(b, deadline.Token);
            Assert.Equal(a, (await first.ReceiveAsync(deadline.Token)).Buffer);
            Assert.Equal(b, (await second.ReceiveAsync(deadline.Token)).Buffer);
        }
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/clients/test-agent/channels/echo/connections";
        JsonElement status;
        do
        {
            await Task.Delay(100, deadline.Token);
            status = await http.GetFromJsonAsync<JsonElement>(url, deadline.Token);
        } while (status.GetProperty("connections").GetArrayLength() != 0);
        await first.SendAsync(new byte[] { 8, 9 }, deadline.Token);
        Assert.Equal(new byte[] { 8, 9 }, (await first.ReceiveAsync(deadline.Token)).Buffer);
        using var logs = await http.GetFromJsonAsync<JsonDocument>($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/clients/test-agent/channels", deadline.Token);
        Assert.Contains("udp", logs!.RootElement.ToString());
    }

    [Fact]
    public async Task Udp_security_group_denies_unlisted_source_without_opening_a_session()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync(transportProtocol: "udp", restrictSource: true);
        await fixture.StartAgentAsync();
        using var denied = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        denied.Connect(IPAddress.Loopback, fixture.ProxyPort);
        await denied.SendAsync(new byte[] { 42 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await denied.ReceiveAsync(timeout.Token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token)).GetProperty("csrfToken").GetString();
        using var update = new HttpRequestMessage(HttpMethod.Put,
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/security-groups/local")
        { Content = JsonContent.Create(new { name = "Local", entries = new[] { "127.0.0.1" } }) };
        update.Headers.Add("X-RelayLink-CSRF", csrf);
        using var saved = await http.SendAsync(update, deadline.Token);
        saved.EnsureSuccessStatusCode();
        using var allowed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        allowed.Connect(IPAddress.Loopback, fixture.ProxyPort);
        await allowed.SendAsync(new byte[] { 7 }, deadline.Token);
        Assert.Equal(new byte[] { 7 }, (await allowed.ReceiveAsync(deadline.Token)).Buffer);
    }

    [Theory]
    [InlineData(ControlRejectionReason.ClientNotFound)]
    [InlineData(ControlRejectionReason.ClientDisabled)]
    [InlineData(ControlRejectionReason.InvalidSecret)]
    [InlineData(ControlRejectionReason.IdentityInvalid)]
    [InlineData(ControlRejectionReason.IdentityMismatch)]
    public async Task Registration_rejection_returns_exact_reason_and_writes_safe_server_log(ControlRejectionReason reason)
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync(reason == ControlRejectionReason.IdentityMismatch ? new string('B', 64) : null,
            enabled: reason != ControlRejectionReason.ClientDisabled);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var writer = new FrameWriter(connection.GetStream());
        var reader = new FrameReader(connection.GetStream());
        var submittedSecret = reason == ControlRejectionReason.InvalidSecret ? "test-invalid-secret-marker" : fixture.Secret;
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage(
            reason == ControlRejectionReason.ClientNotFound ? "unknown-client" : "test-agent", submittedSecret,
            "integration-test", reason == ControlRejectionReason.IdentityInvalid ? null : new string('A', 64)))), token);
        var frame = await reader.ReadAsync(ProtocolConstants.MaxErrorPayloadLength, token);
        Assert.Equal(FrameType.Error, frame?.Type);
        var error = JsonProtocolSerializer.Deserialize<ErrorMessage>(frame!.Payload.Span);
        Assert.Equal(ErrorCode.AuthFailed, error.Code);
        Assert.Equal(reason.ToUnauthenticatedWireReason(), error.Reason);
        var agentFailure = AgentPermanentException.FromServerError(frame, "registration");
        Assert.Contains(reason.ToUnauthenticatedWireReason().Describe(), agentFailure.Message);
        if (reason.ToUnauthenticatedWireReason() == ControlRejectionReason.AuthenticationFailed)
            Assert.DoesNotContain(reason.Describe(), agentFailure.Message);
        var logs = fixture.ReadLogs();
        Assert.Contains(reason.ToString(), logs);
        Assert.DoesNotContain(fixture.Secret, logs);
        Assert.DoesNotContain("test-invalid-secret-marker", logs);
    }

    [Fact]
    public async Task Admin_plane_sets_cookie_headers_limits_login_and_warns_for_non_loopback()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var handler = new HttpClientHandler { UseCookies = false };
        using var http = new HttpClient(handler);
        using var live = await http.GetAsync($"http://127.0.0.1:{fixture.DashboardPort}/health/live", token);
        live.EnsureSuccessStatusCode();
        AssertSecurityHeaders(live);
        using var asset = await http.GetAsync($"http://127.0.0.1:{fixture.DashboardPort}/admin-security.txt", token);
        asset.EnsureSuccessStatusCode();
        Assert.Equal("relaylink", (await asset.Content.ReadAsStringAsync(token)).Trim());
        AssertSecurityHeaders(asset);
        using var page = await http.GetAsync($"http://127.0.0.1:{fixture.DashboardPort}/", token);
        AssertSecurityHeaders(page);
        Assert.DoesNotContain("is not a loopback address", fixture.ReadLogs());

        using var oversized = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = new string('x', 1025) }, token);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        using var wrongName = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "not-admin", password = "test-password" }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongName.StatusCode);

        using var first = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        first.EnsureSuccessStatusCode();
        var setCookie = first.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("relaylink_admin_session=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        var firstCookie = setCookie.Split(';', 2)[0];

        using var second = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        second.EnsureSuccessStatusCode();
        using var stale = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session");
        stale.Headers.Add("Cookie", firstCookie);
        using var staleResponse = await http.SendAsync(stale, token);
        Assert.False((await staleResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("authenticated").GetBoolean());

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
                new { username = "admin", password = "wrong-password" }, token);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }
        using var locked = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "wrong-password" }, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(locked.Headers.RetryAfter?.ToString()));
        using var otherName = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "other", password = "wrong-password" }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, otherName.StatusCode);

        using var exposed = new TunnelFixture();
        await exposed.StartAsync(dashboardListenAddress: "0.0.0.0");
        Assert.Contains("SECURITY:", exposed.ReadLogs());
        Assert.Contains("0.0.0.0", exposed.ReadLogs());
    }

    [Fact]
    public async Task Duplicate_session_and_invalid_acknowledgement_return_distinct_reasons()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        async Task<(TcpClient Client, FrameReader Reader, FrameWriter Writer, RegisterAcceptedMessage Accepted)> RegisterAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
            var reader = new FrameReader(client.GetStream());
            var writer = new FrameWriter(client.GetStream());
            await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "test", new string('A', 64)))), token);
            var accepted = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
            Assert.Equal(FrameType.RegisterAccepted, accepted?.Type);
            return (client, reader, writer, JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted!.Payload.Span));
        }
        var first = await RegisterAsync();
        using var firstClient = first.Client;
        using var duplicate = new TcpClient();
        await duplicate.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        await new FrameWriter(duplicate.GetStream()).WriteAsync(new Frame(FrameType.Register,
            JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "test", new string('A', 64)))), token);
        var duplicateFrame = await new FrameReader(duplicate.GetStream()).ReadAsync(ProtocolConstants.MaxErrorPayloadLength, token);
        Assert.Equal(FrameType.Error, duplicateFrame?.Type);
        var duplicateError = JsonProtocolSerializer.Deserialize<ErrorMessage>(duplicateFrame!.Payload.Span);
        Assert.Equal(ErrorCode.DuplicateSession, duplicateError.Code);
        Assert.Equal(ControlRejectionReason.DuplicateSession, duplicateError.Reason);
        await first.Writer.WriteAsync(new Frame(FrameType.ConfigAck,
            JsonProtocolSerializer.Serialize(new ConfigAckMessage(first.Accepted.SessionId, "invalid-hash"))), token);
        var ackFrame = await first.Reader.ReadAsync(ProtocolConstants.MaxErrorPayloadLength, token);
        Assert.Equal(FrameType.Error, ackFrame?.Type);
        Assert.Equal(ControlRejectionReason.ConfigurationMismatch, JsonProtocolSerializer.Deserialize<ErrorMessage>(ackFrame!.Payload.Span).Reason);
        Assert.Contains("DuplicateSession", fixture.ReadLogs());
        Assert.Contains("ConfigurationMismatch", fixture.ReadLogs());
    }

    [Fact]
    public async Task Lowering_the_client_limit_rejects_a_second_open_proxy_connection()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        await fixture.StartAgentAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        using var channelUpdate = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent/channels/echo")
        {
            Content = JsonContent.Create(new { displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = fixture.ProxyPort,
                targetHost = "127.0.0.1", targetPort = fixture.TargetPort, maxConnections = 1, targetConnectTimeoutSeconds = 5 })
        };
        channelUpdate.Headers.Add("X-RelayLink-CSRF", csrf);
        using var channelSaved = await http.SendAsync(channelUpdate, token);
        Assert.True(channelSaved.IsSuccessStatusCode, await channelSaved.Content.ReadAsStringAsync(token));

        using var secondTarget = new TcpListener(IPAddress.Loopback, 0);
        secondTarget.Start();
        var secondTargetPort = ((IPEndPoint)secondTarget.LocalEndpoint).Port;
        using var secondListenHolder = new TcpListener(IPAddress.Loopback, 0);
        secondListenHolder.Start();
        var secondListenPort = ((IPEndPoint)secondListenHolder.LocalEndpoint).Port;
        secondListenHolder.Stop();
        using var create = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent/channels")
        {
            Content = JsonContent.Create(new { channelId = "echo-b", displayName = "Echo B", enabled = true, listenAddress = "127.0.0.1", listenPort = secondListenPort,
                targetHost = "127.0.0.1", targetPort = secondTargetPort, maxConnections = 1, targetConnectTimeoutSeconds = 5 })
        };
        create.Headers.Add("X-RelayLink-CSRF", csrf);
        using var created = await http.SendAsync(create, token);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(token));
        var secondAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                using var accepted = await secondTarget.AcceptTcpClientAsync(token);
                secondAccepted.TrySetResult();
                var stream = accepted.GetStream();
                var buffer = new byte[8];
                var read = await stream.ReadAsync(buffer, token);
                if (read > 0) await stream.WriteAsync(buffer.AsMemory(0, read), token);
            }
            catch (Exception exception) { secondAccepted.TrySetException(exception); }
        }, token);

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        var relaying = false;
        for (var attempt = 0; attempt < 100 && !relaying; attempt++)
        {
            using var listed = await http.GetFromJsonAsync<JsonDocument>(
                $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/clients/test-agent/channels/echo/connections", token);
            relaying = listed!.RootElement.GetProperty("connections").EnumerateArray()
                .Any(connection => connection.GetProperty("state").GetString() == "relaying");
            if (!relaying) await Task.Delay(100, token);
        }
        Assert.True(relaying, fixture.DiagnosticOutput());
        using var clientUpdate = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent")
        {
            Content = JsonContent.Create(new { displayName = "Test Agent", enabled = true, maxConnections = 1, maxPendingConnections = 1 })
        };
        clientUpdate.Headers.Add("X-RelayLink-CSRF", csrf);
        using var saved = await http.SendAsync(clientUpdate, token);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(token));

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, secondListenPort, token);
        using var rejectWindow = CancellationTokenSource.CreateLinkedTokenSource(token);
        rejectWindow.CancelAfter(TimeSpan.FromSeconds(3));
        var rejected = false;
        try
        {
            rejected = await second.GetStream().ReadAsync(new byte[1], rejectWindow.Token) == 0;
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            rejected = true;
        }
        Assert.True(rejected, "The second channel should be rejected by the client limit while the first channel stays open.");
        Assert.False(secondAccepted.Task.IsCompletedSuccessfully, "The second target should not be contacted when the client limit is already held.");

        await first.GetStream().WriteAsync(new byte[] { 9 }, token);
        var reply = new byte[1];
        await first.GetStream().ReadExactlyAsync(reply, token);
        Assert.Equal(9, reply[0]);
    }

    [Fact]
    public async Task Agent_records_server_rejection_code_reason_and_profile_in_file()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("relaylink-agent-rejection-");
        try
        {
            var configPath = Path.Combine(directory.FullName, "agent.json");
            var profile = new AgentServerProfile
            {
                ProfileId = "rejected", ClientId = "test-agent", ServerHost = "127.0.0.1",
                ServerPort = fixture.TunnelPort, DataPort = fixture.DataPort,
                Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Reconnect = new ReconnectConfiguration(1, 2, 3)
            };
            var config = new AgentProcessConfiguration { DashboardPort = 0, Servers = [profile] };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
            using var sink = AgentFileLogging.Create(configPath);
            using var factory = LoggerFactory.Create(builder => builder.AddSerilog(sink, dispose: false));
            var runtime = new AgentProcessRuntime(config, new AgentConfigurationPath(configPath), factory);
            await runtime.StartAsync(token);
            string logs = string.Empty;
            try
            {
                while (!logs.Contains("AuthenticationFailed", StringComparison.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    logs = string.Concat(Directory.GetFiles(Path.Combine(directory.FullName, "logs"), "agent-*.jsonl").Select(ReadActiveLog));
                    await Task.Delay(50, token);
                }
            }
            finally { await runtime.StopAsync(CancellationToken.None); }
            Assert.Contains("AuthFailed", logs);
            Assert.Contains("AuthenticationFailed", logs);
            Assert.DoesNotContain("InvalidSecret", logs);
            Assert.Contains("rejected", logs);
            Assert.Contains("test-agent", logs);
            Assert.DoesNotContain(profile.Secret, logs);
            Assert.DoesNotContain(fixture.Secret, logs);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static string ReadActiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Admin_can_update_tags_and_delete_an_unreferenced_client()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();

        using (var update = new HttpRequestMessage(HttpMethod.Put,
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent")
        {
            Content = JsonContent.Create(new { displayName = "Test Agent", tags = new[] { "production", "shanghai" },
                enabled = true, maxConnections = 10, maxPendingConnections = 5 })
        })
        {
            update.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(update, token);
            response.EnsureSuccessStatusCode();
        }
        using (var snapshot = await http.GetFromJsonAsync<JsonDocument>(
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/dashboard/snapshot?page=1&pageSize=100", token))
        {
            var tags = snapshot!.RootElement.GetProperty("clients")[0].GetProperty("tags").EnumerateArray()
                .Select(item => item.GetString()).ToArray();
            Assert.Equal(new[] { "production", "shanghai" }, tags);
        }

        using (var delete = new HttpRequestMessage(HttpMethod.Delete,
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent"))
        {
            delete.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(delete, token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.False(File.Exists(fixture.ClientPath));
        using var after = await http.GetFromJsonAsync<JsonDocument>(
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/dashboard/snapshot?page=1&pageSize=100", token);
        Assert.Equal(0, after!.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Dashboard_snapshot_batches_clients_channels_and_mappings_without_secrets()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new HttpClient();
        var endpoint = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/dashboard/snapshot?page=1&pageSize=100";

        using var response = await http.GetAsync(endpoint, deadline.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(deadline.Token);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(1, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(100, body.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("overview").GetProperty("clientsTotal").GetInt32());
        var client = Assert.Single(body.RootElement.GetProperty("clients").EnumerateArray());
        Assert.Equal("test-agent", client.GetProperty("clientId").GetString());
        Assert.Equal("echo", Assert.Single(client.GetProperty("channels").EnumerateArray()).GetProperty("channelId").GetString());
        Assert.Empty(client.GetProperty("mappings").EnumerateArray());
        Assert.DoesNotContain(fixture.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("accessSecret", text, StringComparison.OrdinalIgnoreCase);

        using var invalid = await http.GetAsync(
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/dashboard/snapshot?pageSize=101", deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Audit_write_failure_prevents_successful_admin_login()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var blocker = new SqliteConnection($"Data Source={fixture.AuditPath}");
        await blocker.OpenAsync(token);
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = "BEGIN IMMEDIATE";
            await command.ExecuteNonQueryAsync(token);
        }
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        var endpoint = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session";
        using (var rejected = await http.PostAsJsonAsync(endpoint, new { username = "admin", password = "test-password" }, token))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = "ROLLBACK";
            await command.ExecuteNonQueryAsync(token);
        }
        using (var succeeded = await http.PostAsJsonAsync(endpoint, new { username = "admin", password = "test-password" }, token))
            succeeded.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Audit_write_failure_prevents_proxy_start()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var reader = new FrameReader(control.GetStream());
        var writer = new FrameWriter(control.GetStream());
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(
            new RegisterMessage("test-agent", fixture.Secret, "integration-test", new string('A', 64)))), token);
        var accepted = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>((await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(accepted.SessionId, accepted.ConfigVersion))), token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        var open = JsonProtocolSerializer.Deserialize<OpenMessage>((await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))!.Payload.Span);
        using var data = new TcpClient();
        await data.ConnectAsync(IPAddress.Loopback, fixture.DataPort, token);
        await new FrameWriter(data.GetStream()).WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(
            new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), token);
        Assert.Equal(FrameType.BindAccepted, (await new FrameReader(data.GetStream()).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
        await using var blocker = new SqliteConnection($"Data Source={fixture.AuditPath}");
        await blocker.OpenAsync(token);
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = "BEGIN IMMEDIATE";
            await command.ExecuteNonQueryAsync(token);
        }
        await writer.WriteAsync(new Frame(FrameType.TargetReady, JsonProtocolSerializer.Serialize(new TargetReadyMessage(open.ConnectionId, 1))), token);
        Assert.Equal(0, await caller.GetStream().ReadAsync(new byte[1], token));
        await using (var command = blocker.CreateCommand())
        {
            command.CommandText = "ROLLBACK";
            await command.ExecuteNonQueryAsync(token);
        }
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        using var listed = await http.GetFromJsonAsync<JsonDocument>($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/audit?clientId=test-agent", token);
        Assert.DoesNotContain(listed!.RootElement.GetProperty("events").EnumerateArray(), item =>
            item.GetProperty("eventType").GetString() == "proxy_connection_opened" && item.GetProperty("connectionId").GetGuid() == open.ConnectionId);
    }

    [Fact]
    public async Task Audit_api_requires_admin_and_persists_login_events_without_secrets()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        var endpoint = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/audit";
        using (var anonymous = await http.GetAsync(endpoint, token)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var failed = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session", new { username = "admin", password = "wrong-password" }, token))
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        using (var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session", new { username = "admin", password = "test-password" }, token))
            login.EnsureSuccessStatusCode();
        using (var invalid = await http.GetAsync($"{endpoint}?hours=2161", token)) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var listed = await http.GetAsync($"{endpoint}?hours=24&pageSize=1", token))
        {
            listed.EnsureSuccessStatusCode();
            var body = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            Assert.Equal(2, body.GetProperty("total").GetInt64());
            Assert.Single(body.GetProperty("events").EnumerateArray());
            Assert.Equal("admin_login_success", body.GetProperty("events")[0].GetProperty("eventType").GetString());
        }
        using (var filtered = await http.GetAsync($"{endpoint}?eventType=admin_login_failure", token))
        {
            var body = await filtered.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            Assert.Equal("admin_login_failure", Assert.Single(body.GetProperty("events").EnumerateArray()).GetProperty("eventType").GetString());
            var text = await filtered.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain("wrong-password", text, StringComparison.Ordinal);
            Assert.DoesNotContain("test-password", text, StringComparison.Ordinal);
            Assert.DoesNotContain("csrfToken", text, StringComparison.Ordinal);
        }
        using (var logout = await http.DeleteAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session", token))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using (var after = await http.GetAsync(endpoint, token)) Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Connection_list_is_readable_and_admin_disconnect_is_scoped_and_csrf_protected()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var reader = new FrameReader(control.GetStream());
        var writer = new FrameWriter(control.GetStream());
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(
            new RegisterMessage("test-agent", fixture.Secret, "integration-test", new string('A', 64)))), token);
        var accepted = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>((await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(accepted.SessionId, accepted.ConfigVersion))), token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        var open = JsonProtocolSerializer.Deserialize<OpenMessage>((await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))!.Payload.Span);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        var publicUrl = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/clients/test-agent/channels/echo/connections";
        JsonElement connection = default;
        var listedConnection = false;
        for (var attempt = 0; attempt < 50 && !listedConnection; attempt++)
        {
            using var listed = await http.GetAsync(publicUrl, token);
            listed.EnsureSuccessStatusCode();
            var body = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            var connections = body.GetProperty("connections").EnumerateArray().ToArray();
            if (connections.Length == 0)
            {
                await Task.Delay(50, token);
                continue;
            }
            connection = Assert.Single(connections);
            listedConnection = true;
        }
        Assert.True(listedConnection, fixture.DiagnosticOutput());
        Assert.Equal(open.ConnectionId, connection.GetProperty("connectionId").GetGuid());
        Assert.Equal("connecting", connection.GetProperty("state").GetString());
        Assert.Contains("127.0.0.1", connection.GetProperty("source").GetString());
        var deleteUrl = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent/channels/echo/connections/{open.ConnectionId}";
        using (var unauthorized = await http.DeleteAsync(deleteUrl, token)) Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session", new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        using (var missingCsrf = await http.DeleteAsync(deleteUrl, token)) Assert.Equal(HttpStatusCode.Unauthorized, missingCsrf.StatusCode);
        using (var wrongChannel = new HttpRequestMessage(HttpMethod.Delete, deleteUrl.Replace("/echo/", "/other/")))
        {
            wrongChannel.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(wrongChannel, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl))
        {
            request.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Equal(0, await caller.GetStream().ReadAsync(new byte[1], token));
        using (var listed = await http.GetAsync(publicUrl, token))
        {
            var body = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            Assert.Empty(body.GetProperty("connections").EnumerateArray());
        }
    }

    [Fact]
    public async Task Security_group_management_filters_sources_and_revokes_existing_connections()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        var baseUrl = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin";
        using (var anonymous = await http.GetAsync($"{baseUrl}/security-groups", token))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var login = await http.PostAsJsonAsync($"{baseUrl}/session", new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool includeCsrf = true)
        {
            using var request = new HttpRequestMessage(method, $"{baseUrl}{path}");
            if (body is not null) request.Content = JsonContent.Create(body);
            if (includeCsrf) request.Headers.Add("X-RelayLink-CSRF", csrf);
            return await http.SendAsync(request, token);
        }
        using (var missingCsrf = await SendAsync(HttpMethod.Post, "/security-groups", new { id = "office", name = "Office", entries = new[] { "127.0.0.1" } }, false))
            Assert.Equal(HttpStatusCode.Unauthorized, missingCsrf.StatusCode);
        using (var invalid = await SendAsync(HttpMethod.Post, "/security-groups", new { id = "bad", name = "Bad", entries = new[] { "192.0.2.0/33" } }))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var created = await SendAsync(HttpMethod.Post, "/security-groups", new { id = "office", name = "Office", entries = new[] { "127.0.0.1/32" } }))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ServerPath, token)))
            Assert.Equal("office", persisted.RootElement.GetProperty("securityGroups")[0].GetProperty("id").GetString());
        using (var saved = await SendAsync(HttpMethod.Put, "/clients/test-agent/channels/echo", new
        {
            displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = fixture.ProxyPort,
            targetHost = "127.0.0.1", targetPort = fixture.TargetPort, maxConnections = 5,
            targetConnectTimeoutSeconds = 5, securityGroupId = "office"
        })) saved.EnsureSuccessStatusCode();
        using (var referenced = await SendAsync(HttpMethod.Delete, "/security-groups/office"))
            Assert.Equal(HttpStatusCode.BadRequest, referenced.StatusCode);

        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var reader = new FrameReader(control.GetStream());
        var writer = new FrameWriter(control.GetStream());
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(
            new RegisterMessage("test-agent", fixture.Secret, "integration-test", new string('A', 64)))), token);
        var accepted = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>((await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(accepted.SessionId, accepted.ConfigVersion))), token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        Assert.Equal(FrameType.Open, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);

        using (var narrowed = await SendAsync(HttpMethod.Put, "/security-groups/office", new { name = "Office", entries = new[] { "192.0.2.0/24" } }))
            narrowed.EnsureSuccessStatusCode();
        Assert.Equal(0, await caller.GetStream().ReadAsync(new byte[1], token));
        using var denied = new TcpClient();
        await denied.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        var deniedStream = denied.GetStream();
        Assert.Equal(0, await deniedStream.ReadAsync(new byte[1], token));
        using (var listed = await http.GetAsync($"{baseUrl}/security-groups", token))
        {
            var body = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            Assert.Equal("192.0.2.0/24", body.GetProperty("securityGroups")[0].GetProperty("entries")[0].GetString());
        }
        using (var unbound = await SendAsync(HttpMethod.Put, "/clients/test-agent/channels/echo", new
        {
            displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = fixture.ProxyPort,
            targetHost = "127.0.0.1", targetPort = fixture.TargetPort, maxConnections = 5,
            targetConnectTimeoutSeconds = 5, securityGroupId = ""
        })) unbound.EnsureSuccessStatusCode();
        using (var removed = await SendAsync(HttpMethod.Delete, "/security-groups/office"))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using (var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ServerPath, token)))
            Assert.Empty(persisted.RootElement.GetProperty("securityGroups").EnumerateArray());
    }

    [Fact]
    public async Task Admin_port_suggestion_skips_a_newly_occupied_port_and_save_rechecks_binding()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var endpoint = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/next-channel-port";
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var unauthorized = await http.GetAsync(endpoint, deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token)).GetProperty("csrfToken").GetString();
        var first = (await http.GetFromJsonAsync<JsonElement>(endpoint, deadline.Token)).GetProperty("listenPort").GetInt32();
        Assert.True(first >= 19000);
        Assert.NotEqual(fixture.ProxyPort, first);

        using var occupied = new TcpListener(IPAddress.Any, first);
        occupied.Start();
        var next = (await http.GetFromJsonAsync<JsonElement>(endpoint, deadline.Token)).GetProperty("listenPort").GetInt32();
        Assert.NotEqual(first, next);

        using var create = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent/channels")
        {
            Content = JsonContent.Create(new { channelId = "attempt", displayName = "Attempt", enabled = true,
                listenAddress = "0.0.0.0", listenPort = first, targetHost = "127.0.0.1",
                targetPort = fixture.TargetPort, maxConnections = 5, targetConnectTimeoutSeconds = 5 })
        };
        create.Headers.Add("X-RelayLink-CSRF", csrf);
        using var rejected = await http.SendAsync(create, deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var channels = await http.GetFromJsonAsync<JsonDocument>(
            $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/clients/test-agent/channels", deadline.Token);
        Assert.DoesNotContain(channels!.RootElement.GetProperty("channels").EnumerateArray(),
            channel => channel.GetProperty("channelId").GetString() == "attempt");
    }

    [Fact]
    public async Task Separate_data_port_rejects_bind_without_authenticated_control_session()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = cancellation.Token;
        var bogus = new BindDataMessage(Guid.NewGuid(), Guid.NewGuid(), "echo", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        using (var data = new TcpClient())
        {
            await data.ConnectAsync(IPAddress.Loopback, fixture.DataPort, token);
            var stream = data.GetStream();
            await new FrameWriter(stream).WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(bogus)), token);
            Assert.Equal(FrameType.Error, (await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
        }

        using (var control = new TcpClient())
        {
            await control.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
            var stream = control.GetStream();
            await new FrameWriter(stream).WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(bogus)), token);
            Assert.Equal(FrameType.Error, (await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
        }

        using (var data = new TcpClient())
        {
            await data.ConnectAsync(IPAddress.Loopback, fixture.DataPort, token);
            var stream = data.GetStream();
            await new FrameWriter(stream).WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "negative-test"))), token);
            Assert.Equal(FrameType.Error, (await new FrameReader(stream).ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
        }
    }

    [Fact]
    public async Task Authenticated_agent_pins_client_certificate_and_rejects_changed_identity()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        var firstPin = new string('A', 64);
        using (var unconfirmed = new TcpClient())
        {
            await unconfirmed.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
            await new FrameWriter(unconfirmed.GetStream()).WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "test", firstPin))), token);
            Assert.Equal(FrameType.RegisterAccepted, (await new FrameReader(unconfirmed.GetStream()).ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
        }
        using (var unchanged = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ClientPath, token)))
            Assert.False(unchanged.RootElement.TryGetProperty("e2eCertificateSha256", out _));

        using (var wait = new HttpClient())
        {
            var offline = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                using var response = await wait.GetAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/clients", token);
                using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!status.RootElement.GetProperty("clients")[0].GetProperty("online").GetBoolean()) { offline = true; break; }
                await Task.Delay(50, token);
            }
            Assert.True(offline, "Unconfirmed session was not released.");
        }
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
            var reader = new FrameReader(client.GetStream());
            var writer = new FrameWriter(client.GetStream());
            await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "test", firstPin))), token);
            var accepted = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
            Assert.Equal(FrameType.RegisterAccepted, accepted?.Type);
            var registration = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted!.Payload.Span);
            await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registration.SessionId, registration.ConfigVersion))), token);
            Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ClientPath, token));
            Assert.Equal(firstPin, saved.RootElement.GetProperty("e2eCertificateSha256").GetString());
            Assert.False(saved.RootElement.GetProperty("channels")[0].TryGetProperty("e2eCertificateSha256", out _));
        }

        using var changed = new TcpClient();
        await changed.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var changedReader = new FrameReader(changed.GetStream());
        await new FrameWriter(changed.GetStream()).WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "test", new string('B', 64)))), token);
        Assert.Equal(FrameType.Error, (await changedReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.ClientPath, token));
        Assert.Equal(firstPin, persisted.RootElement.GetProperty("e2eCertificateSha256").GetString());
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public async Task Server_forwards_bytes_after_authenticated_data_tunnel_binds(byte protocolVersion)
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cancellation.Token;

        using var controlClient = new TcpClient();
        await controlClient.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var controlStream = controlClient.GetStream();
        var controlReader = new FrameReader(controlStream);
        var controlWriter = new FrameWriter(controlStream, protocolVersion: protocolVersion);
        await controlWriter.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "integration-test", new string('A', 64)))), token);
        var accepted = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.RegisterAccepted, accepted?.Type);
        Assert.Equal(protocolVersion, controlReader.ProtocolVersion);
        var registered = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted!.Payload.Span);
        Assert.Equal(ConfigurationSnapshotHasher.Compute(registered.Config, protocolVersion), registered.ConfigVersion);
        if (protocolVersion == 3) Assert.DoesNotContain("transportProtocol", Encoding.UTF8.GetString(accepted.Payload.Span));
        await controlWriter.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registered.SessionId, registered.ConfigVersion))), token);
        var ready = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.Ready, ready?.Type);

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        var callerStream = caller.GetStream();
        var openFrame = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.Open, openFrame?.Type);
        var open = JsonProtocolSerializer.Deserialize<OpenMessage>(openFrame!.Payload.Span);

        using var dataClient = new TcpClient();
        await dataClient.ConnectAsync(IPAddress.Loopback, fixture.DataPort, token);
        var dataStream = dataClient.GetStream();
        var dataReader = new FrameReader(dataStream);
        var dataWriter = new FrameWriter(dataStream, protocolVersion: protocolVersion);
        await dataWriter.WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), token);
        Assert.Equal(FrameType.BindAccepted, (await dataReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
        Assert.Equal(protocolVersion, dataReader.ProtocolVersion);

        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, fixture.TargetPort, token);
        await controlWriter.WriteAsync(new Frame(FrameType.TargetReady, JsonProtocolSerializer.Serialize(new TargetReadyMessage(open.ConnectionId, 1))), token);
        Assert.Equal(FrameType.Start, (await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);

        var input = RandomNumberGenerator.GetBytes(4096);
        await callerStream.WriteAsync(input, token);
        var toTarget = new byte[input.Length];
        await ReadExactlyAsync(dataStream, toTarget, token);
        Assert.Equal(input, toTarget);
        await target.GetStream().WriteAsync(toTarget, token);
        var echoed = new byte[input.Length];
        await ReadExactlyAsync(target.GetStream(), echoed, token);
        await dataStream.WriteAsync(echoed, token);
        var received = new byte[input.Length];
        await ReadExactlyAsync(callerStream, received, token);
        Assert.Equal(input, received);
        caller.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await dataStream.ReadAsync(new byte[1], token));
        dataClient.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await callerStream.ReadAsync(new byte[1], token));
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using (var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session", new { username = "admin", password = "test-password" }, token))
            login.EnsureSuccessStatusCode();
        var auditUrl = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/audit?clientId=test-agent";
        var matched = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var listed = await http.GetFromJsonAsync<JsonDocument>(auditUrl, token);
            var events = listed!.RootElement.GetProperty("events").EnumerateArray().ToArray();
            matched = events.Any(item => item.GetProperty("eventType").GetString() == "proxy_connection_opened" && item.GetProperty("connectionId").GetGuid() == open.ConnectionId) &&
                events.Any(item => item.GetProperty("eventType").GetString() == "proxy_connection_closed" && item.GetProperty("connectionId").GetGuid() == open.ConnectionId && item.GetProperty("bytesToTarget").GetInt64() == input.Length);
            if (matched) break;
            await Task.Delay(50, token);
        }
        Assert.True(matched, "Proxy open and close must be queryable with the same connection ID and transferred bytes.");
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("client-disable")]
    public async Task Changed_channel_disconnects_existing_proxy_but_identical_save_does_not(string change)
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var controlReader = new FrameReader(control.GetStream());
        var controlWriter = new FrameWriter(control.GetStream());
        await controlWriter.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "integration-test", new string('A', 64)))), token);
        var accepted = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        var registration = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted!.Payload.Span);
        await controlWriter.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registration.SessionId, registration.ConfigVersion))), token);
        Assert.Equal(FrameType.Ready, (await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        var open = JsonProtocolSerializer.Deserialize<OpenMessage>((await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))!.Payload.Span);
        using var data = new TcpClient();
        await data.ConnectAsync(IPAddress.Loopback, fixture.DataPort, token);
        var dataReader = new FrameReader(data.GetStream());
        await new FrameWriter(data.GetStream()).WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), token);
        Assert.Equal(FrameType.BindAccepted, (await dataReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);
        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, fixture.TargetPort, token);
        await controlWriter.WriteAsync(new Frame(FrameType.TargetReady, JsonProtocolSerializer.Serialize(new TargetReadyMessage(open.ConnectionId, 1))), token);
        Assert.Equal(FrameType.Start, (await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/session", new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        var endpoint = $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent/channels/echo";
        async Task SaveAsync(string displayName, bool enabled = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = JsonContent.Create(new { displayName, enabled, listenAddress = "127.0.0.1", listenPort = fixture.ProxyPort,
                    targetHost = "127.0.0.1", targetPort = fixture.TargetPort, maxConnections = 5, targetConnectTimeoutSeconds = 5 })
            };
            request.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }

        await SaveAsync("Echo");
        using (var unchanged = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await caller.GetStream().ReadAtLeastAsync(new byte[1], 1, false, unchanged.Token));

        if (change == "client-disable")
        {
            using var request = new HttpRequestMessage(HttpMethod.Put,
                $"http://127.0.0.1:{fixture.DashboardPort}/api/v1/admin/clients/test-agent")
            {
                Content = JsonContent.Create(new { displayName = "Test Agent", enabled = false,
                    maxConnections = 10, maxPendingConnections = 5 })
            };
            request.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }
        else if (change == "delete")
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            request.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }
        else await SaveAsync(change == "rename" ? "Echo renamed" : "Echo", enabled: change != "disable");
        Assert.Equal(0, await caller.GetStream().ReadAsync(new byte[1], token));
        if (change == "client-disable")
        {
            using var rejected = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () => await rejected.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token));
            using var reconnect = new TcpClient();
            await reconnect.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
            var reconnectReader = new FrameReader(reconnect.GetStream());
            var reconnectWriter = new FrameWriter(reconnect.GetStream());
            await reconnectWriter.WriteAsync(new Frame(FrameType.Register,
                JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "integration-test", new string('A', 64)))), token);
            var error = await reconnectReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token);
            Assert.Equal(FrameType.Error, error?.Type);
        }
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("default-src 'none'", policy);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new IOException("Unexpected EOF.");
            offset += read;
        }
    }

    private sealed class TunnelFixture : IDisposable
    {
        private readonly TestDirectory storage = new("relaylink-integration-");
        private string directory => storage.Path;
        private readonly TcpListener targetListener = new(IPAddress.Loopback, 0);
        private Process? server;
        private Process? udpAgent;
        private UdpClient? udpTarget;
        private readonly StringBuilder serverOutput = new();
        private readonly StringBuilder serverError = new();
        private readonly HashSet<int> reservedPorts = [];
        private readonly List<TcpListener> heldPorts = [];
        private Task? echoTask;
        public int TunnelPort { get; }
        public int DataPort { get; }
        public int ProxyPort { get; }
        public int DashboardPort { get; }

        public TunnelFixture()
        {
            TunnelPort = ReservePort();
            DataPort = ReservePort();
            ProxyPort = ReservePort();
            DashboardPort = ReservePort();
        }
        public int TargetPort => udpTarget is not null ? ((IPEndPoint)udpTarget.Client.LocalEndPoint!).Port : ((IPEndPoint)targetListener.LocalEndpoint).Port;
        public string Secret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string ClientPath => Path.Combine(directory, "clients", "test-agent.json");
        public string ServerPath => Path.Combine(directory, "server.json");
        public string AuditPath => Path.Combine(directory, "audit.db");

        public string ReadLogs() => string.Concat(Directory.GetFiles(Path.Combine(directory, "logs"), "*.jsonl").Select(ReadActiveLog));

        public async Task StartAsync(string? pinnedIdentity = null, bool enabled = true, string transportProtocol = "tcp", bool restrictSource = false, string dashboardListenAddress = "127.0.0.1")
        {
            Directory.CreateDirectory(Path.Combine(directory, "clients"));
            targetListener.Start();
            if (transportProtocol == "udp")
            {
                udpTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                udpTarget.Client.SendBufferSize = 128 * 1024;
                echoTask = EchoUdpAsync(udpTarget);
            }
            else echoTask = EchoOnceAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "clients", "test-agent.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, clientId = "test-agent", displayName = "Test Agent", enabled, secret = Secret, e2eCertificateSha256 = pinnedIdentity, maxConnections = 10, maxPendingConnections = 5,
                channels = new[] { new { channelId = "echo", displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = ProxyPort, targetHost = "127.0.0.1", targetPort = TargetPort, maxConnections = 5, targetConnectTimeoutSeconds = 5, transportProtocol, udpIdleTimeoutSeconds = 1, securityGroupId = restrictSource ? "local" : null } }
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            var configPath = Path.Combine(directory, "server.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tunnel = new { listenAddress = "127.0.0.1", port = TunnelPort, dataPort = DataPort, tlsEnabled = false, certificatePemPath = "", privateKeyPemPath = "", handshakeTimeoutSeconds = 10, heartbeatIntervalSeconds = 15, heartbeatTimeoutSeconds = 45 },
                dashboard = new { listenAddress = dashboardListenAddress, port = DashboardPort, refreshSeconds = 5, admin = new { username = "admin", passwordHash = PasswordHash(), sessionLifetimeMinutes = 60 } },
                securityGroups = restrictSource ? new[] { new { id = "local", name = "Local", entries = new[] { "192.0.2.1" } } } : [],
                clientsDirectory = Path.Combine(directory, "clients"),
                limits = new { maxConnections = 20, maxPendingConnections = 10, maxUnauthenticatedConnections = 10, maxChannelsPerClient = 10, openTimeoutSeconds = 10, blockedWriteTimeoutSeconds = 120, halfCloseDrainTimeoutSeconds = 300 }
            }));
            ReleaseHeldPorts();
            var assembly = typeof(RelayLink.Server.Configuration.ConfigurationLoader).Assembly.Location;
            server = Process.Start(new ProcessStartInfo("dotnet", $"\"{assembly}\" --config \"{configPath}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true })!;
            server.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (serverOutput) serverOutput.AppendLine(eventArgs.Data); };
            server.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (serverError) serverError.AppendLine(eventArgs.Data); };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var http = new HttpClient();
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));
                    if ((await http.GetAsync($"http://127.0.0.1:{DashboardPort}/health/ready", requestTimeout.Token)).IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException)
                {
                    if (timeout.IsCancellationRequested) break;
                }
                if (server.HasExited) throw new InvalidOperationException(DiagnosticOutput());
                try { await Task.Delay(100, timeout.Token); }
                catch (OperationCanceledException) { break; }
            }
            throw new TimeoutException($"Server did not become ready. {DiagnosticOutput()}");
        }

        public async Task StartAgentAsync(string? assembly = null)
        {
            var path = Path.Combine(directory, "agent.json");
            var localDashboardPort = GetFreePort();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                dashboardPort = localDashboardPort,
                servers = new[] { new { profileId = "udp", serverHost = "127.0.0.1", serverPort = TunnelPort,
                    dataPort = DataPort, clientId = "test-agent", secret = Secret, useTls = false,
                    reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 2, permanentErrorDelaySeconds = 2 } } }
            }));
            udpAgent = Process.Start(new ProcessStartInfo("dotnet", $"\"{assembly ?? typeof(ControlSessionWorker).Assembly.Location}\" --config \"{path}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            udpAgent.BeginOutputReadLine(); udpAgent.BeginErrorReadLine();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var http = new HttpClient();
            while (true)
            {
                try
                {
                    var status = await http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{localDashboardPort}/api/v2/status", deadline.Token);
                    if (status.GetProperty("servers")[0].GetProperty("online").GetBoolean()) return;
                }
                catch (HttpRequestException) { }
                if (udpAgent.HasExited) throw new InvalidOperationException("UDP Agent exited.");
                await Task.Delay(50, deadline.Token);
            }
        }

        private static async Task EchoUdpAsync(UdpClient socket)
        {
            try
            {
                while (true)
                {
                    var packet = await socket.ReceiveAsync();
                    await socket.SendAsync(packet.Buffer, packet.RemoteEndPoint);
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException) { }
        }

        private async Task EchoOnceAsync()
        {
            using var client = await targetListener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer);
            if (read > 0) await stream.WriteAsync(buffer.AsMemory(0, read));
        }

        public void Dispose()
        {
            ReleaseHeldPorts();
            targetListener.Stop();
            udpTarget?.Dispose();
            if (udpAgent is { HasExited: false }) udpAgent.Kill(entireProcessTree: true);
            udpAgent?.Dispose();
            if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
            server?.Dispose();
            storage.Dispose();
        }

        public string DiagnosticOutput()
        {
            if (server is null) return "Server process was not created.";
            lock (serverOutput)
            lock (serverError)
            {
                return $"Server running: {!server.HasExited}; exit code: {(server.HasExited ? server.ExitCode : -1)}; stdout: {serverOutput}; stderr: {serverError}";
            }
        }

        private int ReservePort()
        {
            while (true)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (reservedPorts.Add(port))
                {
                    heldPorts.Add(listener);
                    return port;
                }

                listener.Stop();
                listener.Dispose();
            }
        }

        private void ReleaseHeldPorts()
        {
            foreach (var listener in heldPorts)
            {
                listener.Stop();
                listener.Dispose();
            }

            heldPorts.Clear();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static string PasswordHash()
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 100_000, HashAlgorithmName.SHA256, 32);
            return $"PBKDF2-SHA256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }
    }
}
