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
using RelayLink.Transport;

namespace RelayLink.IntegrationTests;

public sealed class ServerTunnelTests
{
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
        using (var listed = await http.GetAsync(publicUrl, token))
        {
            listed.EnsureSuccessStatusCode();
            var body = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            var connection = Assert.Single(body.GetProperty("connections").EnumerateArray());
            Assert.Equal(open.ConnectionId, connection.GetProperty("connectionId").GetGuid());
            Assert.Equal("connecting", connection.GetProperty("state").GetString());
            Assert.Contains("127.0.0.1", connection.GetProperty("source").GetString());
        }
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

    [Fact]
    public async Task Server_forwards_bytes_after_authenticated_data_tunnel_binds()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cancellation.Token;

        using var controlClient = new TcpClient();
        await controlClient.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        var controlStream = controlClient.GetStream();
        var controlReader = new FrameReader(controlStream);
        var controlWriter = new FrameWriter(controlStream);
        await controlWriter.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "integration-test", new string('A', 64)))), token);
        var accepted = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.RegisterAccepted, accepted?.Type);
        var registered = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted!.Payload.Span);
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
        var dataWriter = new FrameWriter(dataStream);
        await dataWriter.WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), token);
        Assert.Equal(FrameType.BindAccepted, (await dataReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);

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
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"relaylink-integration-{Guid.NewGuid():N}");
        private readonly TcpListener targetListener = new(IPAddress.Loopback, 0);
        private Process? server;
        private readonly StringBuilder serverOutput = new();
        private readonly StringBuilder serverError = new();
        private Task? echoTask;
        public int TunnelPort { get; } = GetFreePort();
        public int DataPort { get; } = GetFreePort();
        public int ProxyPort { get; } = GetFreePort();
        public int DashboardPort { get; } = GetFreePort();
        public int TargetPort => ((IPEndPoint)targetListener.LocalEndpoint).Port;
        public string Secret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string ClientPath => Path.Combine(directory, "clients", "test-agent.json");
        public string ServerPath => Path.Combine(directory, "server.json");
        public string AuditPath => Path.Combine(directory, "audit.db");

        public async Task StartAsync()
        {
            Directory.CreateDirectory(Path.Combine(directory, "clients"));
            targetListener.Start();
            echoTask = EchoOnceAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "clients", "test-agent.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, clientId = "test-agent", displayName = "Test Agent", enabled = true, secret = Secret, maxConnections = 10, maxPendingConnections = 5,
                channels = new[] { new { channelId = "echo", displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = ProxyPort, targetHost = "127.0.0.1", targetPort = TargetPort, maxConnections = 5, targetConnectTimeoutSeconds = 5 } }
            }));
            var configPath = Path.Combine(directory, "server.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tunnel = new { listenAddress = "127.0.0.1", port = TunnelPort, dataPort = DataPort, tlsEnabled = false, certificatePemPath = "", privateKeyPemPath = "", handshakeTimeoutSeconds = 10, heartbeatIntervalSeconds = 15, heartbeatTimeoutSeconds = 45 },
                dashboard = new { listenAddress = "127.0.0.1", port = DashboardPort, refreshSeconds = 5, admin = new { username = "admin", passwordHash = PasswordHash(), sessionLifetimeMinutes = 60 } },
                clientsDirectory = Path.Combine(directory, "clients"),
                limits = new { maxConnections = 20, maxPendingConnections = 10, maxUnauthenticatedConnections = 10, maxChannelsPerClient = 10, openTimeoutSeconds = 10, blockedWriteTimeoutSeconds = 120, halfCloseDrainTimeoutSeconds = 300 }
            }));
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
            targetListener.Stop();
            if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
            server?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
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
