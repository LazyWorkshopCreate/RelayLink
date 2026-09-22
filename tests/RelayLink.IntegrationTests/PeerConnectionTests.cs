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
    public async Task Standard_private_channel_can_reach_server_dashboard_through_target_agent()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync(mapDashboard: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await fixture.WaitOnlineAsync(deadline.Token);
        var localPort = await fixture.WaitPortAsync("to-server-dashboard", deadline.Token);

        using var http = new HttpClient();
        using var overview = await http.GetFromJsonAsync<JsonDocument>(
            $"http://127.0.0.1:{localPort}/api/v1/overview", deadline.Token);

        Assert.Equal(2, overview!.RootElement.GetProperty("clientsOnline").GetInt32());
    }

    [Fact]
    public async Task Admin_can_list_and_disconnect_one_private_channel_connection()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        await fixture.WaitOnlineAsync(token);
        using var caller = await fixture.ConnectLocalAsync(token);
        await caller.GetStream().WriteAsync(new byte[] { 42 }, token);
        while (fixture.TargetConnections == 0) await Task.Delay(50, token);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        var listUrl = $"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels/private/connections";
        using var listed = await http.GetFromJsonAsync<JsonDocument>(listUrl, token);
        var connection = Assert.Single(listed!.RootElement.GetProperty("connections").EnumerateArray());
        Assert.Equal("end-to-end", connection.GetProperty("kind").GetString());
        Assert.Equal("caller", connection.GetProperty("source").GetString());
        var id = connection.GetProperty("connectionId").GetGuid();

        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"http://{fixture.DashboardAddress}/api/v1/admin/clients/visited/channels/private/connections/{id}");
        request.Headers.Add("X-RelayLink-CSRF", csrf);
        using var removed = await http.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await AssertClosedAsync(caller, token);
        using var after = await http.GetFromJsonAsync<JsonDocument>(listUrl, token);
        Assert.Empty(after!.RootElement.GetProperty("connections").EnumerateArray());
        var audited = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var log = await http.GetFromJsonAsync<JsonDocument>($"http://{fixture.DashboardAddress}/api/v1/admin/audit?clientId=visited", token);
            var events = log!.RootElement.GetProperty("events").EnumerateArray().ToArray();
            audited = events.Any(item => item.GetProperty("eventType").GetString() == "peer_connection_opened" && item.GetProperty("connectionId").GetGuid() == id) &&
                events.Any(item => item.GetProperty("eventType").GetString() == "peer_connection_closed" && item.GetProperty("connectionId").GetGuid() == id && item.GetProperty("reasonCode").GetString() == "admin_disconnected");
            if (audited) break;
            await Task.Delay(50, token);
        }
        Assert.True(audited, "Peer lifecycle should be recorded with the same connection ID.");
    }

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

#if PEER_TLS_TESTS
    [Fact]
    public async Task Private_channel_can_disable_inner_tls_and_copy_plaintext_with_half_close()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        await fixture.WaitOnlineAsync(token);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        using var update = new HttpRequestMessage(HttpMethod.Put,
            $"http://{fixture.DashboardAddress}/api/v1/admin/clients/visited/channels/private-b")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Private B", enabled = true, listenAddress = "127.0.0.1", listenPort = 19099,
                targetHost = "127.0.0.1", targetPort = fixture.TargetPortB, maxConnections = 10,
                targetConnectTimeoutSeconds = 5, authorizedClientsOnly = true, endToEndEncryptionEnabled = false
            })
        };
        update.Headers.Add("X-RelayLink-CSRF", csrf);
        using var updated = await http.SendAsync(update, token);
        updated.EnsureSuccessStatusCode();

        var payload = RandomNumberGenerator.GetBytes(4096);
        while (true)
        {
            using var caller = await fixture.ConnectLocalAsync(fixture.LocalPortB, token);
            try
            {
                var stream = caller.GetStream();
                await stream.WriteAsync(payload, token);
                caller.Client.Shutdown(SocketShutdown.Send);
                var marker = new byte[1];
                await stream.ReadExactlyAsync(marker, token);
                Assert.Equal((byte)'B', marker[0]);
                var echoed = new byte[payload.Length];
                await stream.ReadExactlyAsync(echoed, token);
                Assert.Equal(payload, echoed);
                Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100, token);
            }
        }

        using var channels = await http.GetFromJsonAsync<JsonDocument>(
            $"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", token);
        var channel = channels!.RootElement.GetProperty("channels").EnumerateArray()
            .Single(item => item.GetProperty("channelId").GetString() == "private-b");
        Assert.False(channel.GetProperty("endToEndEncryptionEnabled").GetBoolean());
        Assert.Equal(payload.Length + 32, channel.GetProperty("peerCiphertextToTarget").GetInt64());
        Assert.Equal(payload.Length + 34, channel.GetProperty("peerCiphertextToCaller").GetInt64());
    }
#endif

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
        var mappings = await http.GetStringAsync($"http://{fixture.DashboardAddress}/api/v1/clients/caller/mappings", deadline.Token);
        Assert.DoesNotContain("accessSecret", mappings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetCertificateSha256", mappings, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(mappings);
        Assert.Contains(document.RootElement.GetProperty("mappings").EnumerateArray(), item =>
            item.GetProperty("mappingId").GetString() == "to-visited" &&
            item.GetProperty("targetClientId").GetString() == "visited" &&
            item.GetProperty("targetChannelId").GetString() == "private");
        using var missing = await http.GetAsync($"http://{fixture.DashboardAddress}/api/v1/clients/missing/mappings", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

#if PEER_TLS_TESTS
    [Fact]
    public async Task Disabling_referenced_client_preserves_configuration_and_revokes_peer_connections()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        await fixture.WaitOnlineAsync(token);
        var localPort = fixture.LocalPort;
        using var existing = await fixture.ConnectLocalAsync(localPort, token);
        await existing.GetStream().WriteAsync(new byte[] { 42 }, token);
        while (fixture.TargetConnections == 0) await Task.Delay(50, token);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session",
            new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token)).GetProperty("csrfToken").GetString();
        var endpoint = $"http://{fixture.DashboardAddress}/api/v1/admin/clients/visited";
        async Task SaveAsync(bool enabled)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
            { Content = JsonContent.Create(new { displayName = "Visited", enabled, maxConnections = 30, maxPendingConnections = 20 }) };
            request.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }

        await SaveAsync(false);
        using (var persisted = JsonDocument.Parse(fixture.ClientConfigurationJson("visited")))
        {
            Assert.False(persisted.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(2, persisted.RootElement.GetProperty("channels").GetArrayLength());
        }
        using (var callerConfiguration = JsonDocument.Parse(fixture.ClientConfigurationJson("caller")))
            Assert.Equal(2, callerConfiguration.RootElement.GetProperty("outboundMappings").GetArrayLength());
        await AssertClosedAsync(existing, token);
        await fixture.WaitOnlineAsync(token, expected: 1);

        using var rejected = await fixture.ConnectLocalAsync(localPort, token);
        await rejected.GetStream().WriteAsync(new byte[] { 42 }, token);
        await AssertClosedAsync(rejected, token);
        Assert.Equal(1, fixture.TargetConnections);

        await SaveAsync(true);
        await fixture.WaitOnlineAsync(token);
        // A reconnected session is visible before its configuration acknowledgement; require eventual usability.
        while (true)
        {
            using var restored = await fixture.ConnectLocalAsync(localPort, token);
            try
            {
                var restoredStream = restored.GetStream();
                await restoredStream.WriteAsync(new byte[] { 43 }, token);
                restored.Client.Shutdown(SocketShutdown.Send);
                var returned = new byte[1];
                await restoredStream.ReadExactlyAsync(returned, token);
                Assert.Equal((byte)43, returned[0]);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100, token);
            }
        }
        Assert.Equal(2, fixture.TargetConnections);
    }

    private static async Task AssertClosedAsync(TcpClient client, CancellationToken token)
    {
        try { Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], token)); }
        catch (IOException) { }
    }

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
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var history = await http.GetFromJsonAsync<JsonDocument>(
                $"http://{fixture.DashboardAddress}/api/v1/history?clientId=visited&hours=24", deadline.Token);
            var samples = history!.RootElement.GetProperty("samples").EnumerateArray().ToArray();
            if (samples.Length == 2 && samples.All(sample => sample.GetProperty("peerCiphertextToTarget").GetInt64() >= 8L * 1024 * 1024))
            {
                Assert.All(samples, sample => Assert.Equal(0, sample.GetProperty("bytesToTarget").GetInt64() - sample.GetProperty("peerCiphertextToTarget").GetInt64()));
                break;
            }
            Assert.True(attempt < 49, "Peer ciphertext was not persisted for both channels.");
            await Task.Delay(200, deadline.Token);
        }
    }

    [Fact]
    public async Task Changed_private_channel_encryption_disconnects_only_its_existing_peer_connection()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.WaitOnlineAsync(deadline.Token);
        using var changed = await fixture.ConnectLocalAsync(deadline.Token);
        using var unaffected = await fixture.ConnectLocalAsync(fixture.LocalPortB, deadline.Token);
        var changedStream = changed.GetStream();
        var unaffectedStream = unaffected.GetStream();
        await changedStream.WriteAsync(new byte[] { 1 }, deadline.Token);
        await unaffectedStream.WriteAsync(new byte[] { 2 }, deadline.Token);
        while (fixture.TargetConnections < 1 || fixture.TargetConnectionsB < 1)
            await Task.Delay(50, deadline.Token);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session", new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token)).GetProperty("csrfToken").GetString();
        var endpoint = $"http://{fixture.DashboardAddress}/api/v1/admin/clients/visited/channels/private";
        async Task SaveAsync(bool endToEndEncryptionEnabled)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = JsonContent.Create(new { displayName = "Private", enabled = true, listenAddress = "127.0.0.1", listenPort = fixture.ProxyPort,
                    targetHost = "127.0.0.1", targetPort = fixture.TargetPort, maxConnections = 10, targetConnectTimeoutSeconds = 5,
                    authorizedClientsOnly = true, endToEndEncryptionEnabled })
            };
            request.Headers.Add("X-RelayLink-CSRF", csrf);
            using var response = await http.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
        }

        await SaveAsync(true);
        using (var unchanged = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await changedStream.ReadAtLeastAsync(new byte[1], 1, false, unchanged.Token));
        await SaveAsync(false);
        Assert.Equal(0, await changedStream.ReadAsync(new byte[1], deadline.Token));

        unaffected.Client.Shutdown(SocketShutdown.Send);
        var echoed = new byte[2];
        await unaffectedStream.ReadExactlyAsync(echoed, deadline.Token);
        Assert.Equal(new byte[] { (byte)'B', 2 }, echoed);
    }

    [Fact]
    public async Task Deleted_mapping_disconnects_only_its_existing_peer_connection()
    {
        using var fixture = new Fixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.WaitOnlineAsync(deadline.Token);
        using var changed = await fixture.ConnectLocalAsync(deadline.Token);
        using var unaffected = await fixture.ConnectLocalAsync(fixture.LocalPortB, deadline.Token);
        var changedStream = changed.GetStream();
        var unaffectedStream = unaffected.GetStream();
        await changedStream.WriteAsync(new byte[] { 1 }, deadline.Token);
        await unaffectedStream.WriteAsync(new byte[] { 2 }, deadline.Token);
        while (fixture.TargetConnections < 1 || fixture.TargetConnectionsB < 1)
            await Task.Delay(50, deadline.Token);

        using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session", new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token)).GetProperty("csrfToken").GetString();
        var endpoint = $"http://{fixture.DashboardAddress}/api/v1/admin/clients/caller/mappings/to-visited";
        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.Add("X-RelayLink-CSRF", csrf);
        using var response = await http.SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal(0, await changedStream.ReadAsync(new byte[1], deadline.Token));
        unaffected.Client.Shutdown(SocketShutdown.Send);
        var echoed = new byte[2];
        await unaffectedStream.ReadExactlyAsync(echoed, deadline.Token);
        Assert.Equal(new byte[] { (byte)'B', 2 }, echoed);
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
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("intruder", fixture.IntruderSecret, "test", new string('A', 64)))), deadline.Token);
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
        var mapping = new { targetClientId = "visited", targetChannelId = "private" };

        using var unauthorized = await http.PostAsJsonAsync(endpoint, mapping, deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session", new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token);
        var csrf = session.GetProperty("csrfToken").GetString();
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"{endpoint}/to-visited"))
        {
            delete.Headers.Add("X-RelayLink-CSRF", csrf);
            using var deleted = await http.SendAsync(delete, deadline.Token);
            deleted.EnsureSuccessStatusCode();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(mapping) };
        request.Headers.Add("X-RelayLink-CSRF", csrf);
        using var saved = await http.SendAsync(request, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var savedBody = await saved.Content.ReadAsStringAsync(deadline.Token);
        Assert.DoesNotContain("accessSecret", savedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("visited-private", JsonDocument.Parse(savedBody).RootElement.GetProperty("mappingId").GetString());
        using (var duplicate = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(mapping) })
        {
            duplicate.Headers.Add("X-RelayLink-CSRF", csrf);
            using var rejected = await http.SendAsync(duplicate, deadline.Token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using (var edit = new HttpRequestMessage(HttpMethod.Put, $"{endpoint}/visited-private")
        {
            Content = JsonContent.Create(new { enabled = false, targetClientId = "visited", targetChannelId = "private-b" })
        })
        {
            edit.Headers.Add("X-RelayLink-CSRF", csrf);
            using var rejected = await http.SendAsync(edit, deadline.Token);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, rejected.StatusCode);
        }
        var assigned = await fixture.WaitPortAsync("visited-private", deadline.Token);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var reported = await http.GetAsync(endpoint, deadline.Token);
            var document = await reported.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token);
            var current = document.GetProperty("mappings").EnumerateArray().Single(item => item.GetProperty("mappingId").GetString() == "visited-private");
            Assert.Equal("private", current.GetProperty("targetChannelId").GetString());
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wrong_end_to_end_access_proof_never_connects_target(bool encryptionEnabled)
    {
        using var fixture = new Fixture();
        await fixture.StartAsync(startCallerAgent: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.WaitOnlineAsync(deadline.Token, expected: 1);
        if (!encryptionEnabled)
        {
            using var http = new HttpClient(new HttpClientHandler { UseCookies = true });
            using var login = await http.PostAsJsonAsync($"http://{fixture.DashboardAddress}/api/v1/admin/session",
                new { username = "admin", password = "test-password" }, deadline.Token);
            login.EnsureSuccessStatusCode();
            var csrf = (await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token)).GetProperty("csrfToken").GetString();
            using var update = new HttpRequestMessage(HttpMethod.Put,
                $"http://{fixture.DashboardAddress}/api/v1/admin/clients/visited/channels/private-b")
            {
                Content = JsonContent.Create(new
                {
                    displayName = "Private B", enabled = true, listenAddress = "127.0.0.1", listenPort = 19099,
                    targetHost = "127.0.0.1", targetPort = fixture.TargetPortB, maxConnections = 10,
                    targetConnectTimeoutSeconds = 5, authorizedClientsOnly = true, endToEndEncryptionEnabled = false
                })
            };
            update.Headers.Add("X-RelayLink-CSRF", csrf);
            using var updated = await http.SendAsync(update, deadline.Token);
            updated.EnsureSuccessStatusCode();
        }
        using var controlClient = new TcpClient();
        await controlClient.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, deadline.Token);
        await using var outer = await fixture.AuthenticateOuterAsync(controlClient, deadline.Token);
        var reader = new FrameReader(outer);
        var writer = new FrameWriter(outer);
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("caller", fixture.CallerSecret, "negative-test", new string('B', 64)))), deadline.Token);
        var registrationFrame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token);
        Assert.Equal(FrameType.RegisterAccepted, registrationFrame?.Type);
        var registration = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(registrationFrame!.Payload.Span);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registration.SessionId, registration.ConfigVersion))), deadline.Token);
        Assert.Equal(FrameType.Ready, (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token))?.Type);
        var requestId = Guid.NewGuid();
        await writer.WriteAsync(new Frame(FrameType.PeerOpenRequest, JsonProtocolSerializer.Serialize(new PeerOpenRequestMessage(requestId, encryptionEnabled ? "to-visited" : "to-visited-b"))), deadline.Token);
        var grantFrame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, deadline.Token);
        Assert.Equal(FrameType.PeerOpenGranted, grantFrame?.Type);
        var grant = JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>(grantFrame!.Payload.Span);
        Assert.Equal(encryptionEnabled, grant.EndToEndEncryptionEnabled);
        using var dataClient = new TcpClient();
        await dataClient.ConnectAsync(IPAddress.Loopback, fixture.DataPort, deadline.Token);
        await using var dataOuter = dataClient.GetStream();
        var dataReader = new FrameReader(dataOuter);
        var dataWriter = new FrameWriter(dataOuter);
        await dataWriter.WriteAsync(new Frame(FrameType.PeerBindData, JsonProtocolSerializer.Serialize(new PeerBindDataMessage(grant.ConnectionId, grant.SessionId, grant.Token, "caller"))), deadline.Token);
        Assert.Equal(FrameType.PeerBindAccepted, (await dataReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, deadline.Token))?.Type);
        var framed = new FramedDuplexStream(dataReader, dataWriter);
        using var inner = encryptionEnabled ? new SslStream(framed, leaveInnerStreamOpen: true,
            (_, certificate, _, _) => certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(fixture.TargetFingerprint))) : null;
        Stream proofStream = framed;
        if (inner is not null)
        {
            await inner.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "visited", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
            proofStream = inner;
        }
        var challenge = new byte[32];
        await proofStream.ReadExactlyAsync(challenge, deadline.Token);
        await proofStream.WriteAsync(new byte[32], deadline.Token);
        try { Assert.Equal(0, await proofStream.ReadAsync(new byte[1], deadline.Token)); }
        catch (IOException) { }
        Assert.Equal(0, encryptionEnabled ? fixture.TargetConnections : fixture.TargetConnectionsB);
    }
#endif

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"relaylink-peer-{Guid.NewGuid():N}");
        private readonly TcpListener target = new(IPAddress.Loopback, 0);
        private readonly TcpListener targetB = new(IPAddress.Loopback, 0);
        private readonly List<Process> processes = [];
        private readonly List<StringBuilder> processLogs = [];
        private readonly HashSet<int> allocatedPorts = [];
        private int targetConnections;
        private int targetConnectionsB;
        public int TargetConnections => Volatile.Read(ref targetConnections);
        public int TargetConnectionsB => Volatile.Read(ref targetConnectionsB);
        public int LocalPort => ReadPort("to-visited");
        public int LocalPortB => ReadPort("to-visited-b");
        public int AgentDashboardPort { get; }
        public int ProxyPort { get; }
        public int TargetPort { get; private set; }
        public int TargetPortB { get; private set; }
        public int TunnelPort { get; }
        public int DataPort { get; }
        private int DashboardPort { get; }
        private string caPath = string.Empty;
        public string DashboardAddress => $"127.0.0.1:{DashboardPort}";
        public string ClientConfigurationJson(string clientId) => File.ReadAllText(Path.Combine(directory, "clients", $"{clientId}.json"));
        public string IntruderSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string CallerSecret { get; private set; } = string.Empty;
        public string TargetFingerprint { get; private set; } = string.Empty;

        public Fixture()
        {
            AgentDashboardPort = AllocatePort();
            ProxyPort = AllocatePort();
            TunnelPort = AllocatePort();
            DataPort = AllocatePort();
            DashboardPort = AllocatePort();
        }

        public async Task StartAsync(bool startCallerAgent = true, bool mapDashboard = false)
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
            TargetPort = targetPort;
            var targetPortB = ((IPEndPoint)targetB.LocalEndpoint).Port;
            TargetPortB = targetPortB;
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var accessSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var accessSecretB = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var dashboardAccessSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var fingerprint = CreateIdentity(Path.Combine(visitedDirectory, "visited.e2e.pfx"));
            TargetFingerprint = fingerprint;
            var visitedChannels = new List<ChannelConfiguration>
            { new ChannelConfiguration("private", "Private", true, "127.0.0.1", ProxyPort, "127.0.0.1", targetPort, 10, 5)
            { AuthorizedClientsOnly = true, AccessSecret = accessSecret },
             new ChannelConfiguration("private-b", "Private B", true, "127.0.0.1", AllocatePort(), "127.0.0.1", targetPortB, 10, 5)
            { AuthorizedClientsOnly = true, AccessSecret = accessSecretB } };
            if (mapDashboard)
                visitedChannels.Add(new ChannelConfiguration("server-dashboard", "Server Dashboard", true, "127.0.0.1", AllocatePort(), "127.0.0.1", DashboardPort, 10, 5)
                { AuthorizedClientsOnly = true, AccessSecret = dashboardAccessSecret });
            var visited = new ClientConfiguration(1, "visited", "Visited", true, secret, 30, 20, visitedChannels) { E2eCertificateSha256 = fingerprint };
            var callerSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            CallerSecret = callerSecret;
            var callerMappings = new List<OutboundMappingConfiguration>
            { new OutboundMappingConfiguration("to-visited", true, "127.0.0.1", "visited", "private", accessSecret, fingerprint),
              new OutboundMappingConfiguration("to-visited-b", true, "127.0.0.1", "visited", "private-b", accessSecretB, fingerprint) };
            if (mapDashboard)
                callerMappings.Add(new OutboundMappingConfiguration("to-server-dashboard", true, "127.0.0.1", "visited", "server-dashboard", dashboardAccessSecret, fingerprint));
            var caller = new ClientConfiguration(1, "caller", "Caller", true, callerSecret, 30, 20, [])
            { OutboundMappings = callerMappings };
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
                clients, new LimitsConfiguration(50, 20, 50, 10, 20, 120, 300),
                new HistoryConfiguration(true, Path.Combine(directory, "traffic.db")));
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

        private int AllocatePort()
        {
            while (true)
            {
                var port = FreePort();
                if (allocatedPorts.Add(port)) return port;
            }
        }

        private bool TryReadPort(string mappingId, out int port)
        {
            port = 0;
            var path = Path.Combine(directory, "caller", "caller.ports.json");
            if (!File.Exists(path)) return false;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (!json.RootElement.TryGetProperty(mappingId, out var value)) return false;
                port = value.GetInt32();
                return port > 0;
            }
            catch (IOException) { return false; } // Agent may be replacing its port-state file.
            catch (JsonException) { return false; }
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
