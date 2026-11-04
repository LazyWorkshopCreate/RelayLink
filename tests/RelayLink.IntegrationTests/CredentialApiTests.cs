using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using RelayLink.Server.Runtime;

namespace RelayLink.IntegrationTests;

public sealed partial class ServerTunnelTests
{
    [Theory]
    [InlineData("missing-key")]
    [InlineData("invalid-key")]
    [InlineData("wrong-key")]
    [InlineData("corrupt-store")]
    [InlineData("missing-store")]
    public async Task Unavailable_credentials_keep_management_and_none_tcp_running(string fault)
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync(configureBeforeStart: path =>
        {
            var store = CredentialStore.Open(path);
            store.SaveAsync("test-agent", "echo", "login", "user", "target", "password", "private-test-material"u8.ToArray(), default).GetAwaiter().GetResult();
            var directory = CredentialStore.DirectoryFor(path);
            if (fault == "missing-key") File.Delete(Path.Combine(directory, "key"));
            if (fault == "invalid-key") File.WriteAllText(Path.Combine(directory, "key"), "not base64");
            if (fault == "wrong-key") File.WriteAllText(Path.Combine(directory, "key"), Convert.ToBase64String(new byte[32]));
            if (fault == "corrupt-store") File.WriteAllText(Path.Combine(directory, "store"), "corrupted");
            if (fault == "missing-store") File.Delete(Path.Combine(directory, "store"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = await CredentialAdminAsync(fixture, deadline.Token);
        var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/credentials/status", deadline.Token);
        Assert.False(status.GetProperty("available").GetBoolean());
        using var unavailable = await http.GetAsync("/api/v1/admin/clients/test-agent/channels/echo/credentials", deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        await fixture.StartAgentAsync();
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, deadline.Token);
        await caller.GetStream().WriteAsync("none-still-works"u8.ToArray(), deadline.Token);
        var response = new byte[16];
        await caller.GetStream().ReadExactlyAsync(response, deadline.Token);
        Assert.Equal("none-still-works"u8.ToArray(), response);
        Assert.DoesNotContain("private-test-material", fixture.ReadLogs());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_api_retries_and_scope_recreation_do_not_reuse_old_secrets(bool wholeClient)
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = await CredentialAdminAsync(fixture, deadline.Token);
        const string endpoint = "/api/v1/admin/clients/test-agent/channels/echo/credentials/login";
        var id = Guid.NewGuid().ToString();
        var body = new { username = "user", purpose = "target", type = "password", secret = "private-test-material" };
        using var first = await CredentialPutAsync(http, endpoint, id, body, deadline.Token);
        first.EnsureSuccessStatusCode();
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.True(firstJson.GetProperty("applied").GetBoolean());
        Assert.Equal("recorded", firstJson.GetProperty("auditStatus").GetString());
        Assert.DoesNotContain("private-test-material", firstJson.ToString());
        using var retry = await CredentialPutAsync(http, endpoint, id, body, deadline.Token);
        retry.EnsureSuccessStatusCode();
        var retriedJson = await retry.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        Assert.Equal(firstJson.GetProperty("updatedUtc").GetString(), retriedJson.GetProperty("updatedUtc").GetString());
        using var conflict = await CredentialPutAsync(http, endpoint, id, new { username = "other", purpose = "target", type = "password", secret = "different" }, deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);

        var deletePath = wholeClient ? "/api/v1/admin/clients/test-agent" : "/api/v1/admin/clients/test-agent/channels/echo";
        using var removed = await http.DeleteAsync(deletePath, deadline.Token);
        removed.EnsureSuccessStatusCode();
        if (wholeClient)
        {
            using var created = await http.PostAsJsonAsync("/api/v1/admin/clients", new { clientId = "test-agent", displayName = "New", enabled = true, maxConnections = 10, maxPendingConnections = 5, agentServerHost = "127.0.0.1" }, deadline.Token);
            created.EnsureSuccessStatusCode();
        }
        using var channel = await http.PostAsJsonAsync("/api/v1/admin/clients/test-agent/channels", new
        {
            channelId = "echo", displayName = "New Echo", enabled = false, listenAddress = "127.0.0.1", listenPort = fixture.ProxyPort,
            targetHost = "127.0.0.1", targetPort = fixture.TargetPort, maxConnections = 5, targetConnectTimeoutSeconds = 5
        }, deadline.Token);
        channel.EnsureSuccessStatusCode();
        var credentials = await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/clients/test-agent/channels/echo/credentials", deadline.Token);
        Assert.Empty(credentials.GetProperty("credentials").EnumerateArray());
        using var oldReplay = await CredentialPutAsync(http, endpoint, id, body, deadline.Token);
        Assert.Equal(HttpStatusCode.BadRequest, oldReplay.StatusCode);
        Assert.DoesNotContain("private-test-material", fixture.ReadLogs());
    }

    [Fact]
    public async Task Credential_operation_id_header_echoes_client_key_and_generated_retry_is_idempotent()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = await CredentialAdminAsync(fixture, deadline.Token);
        const string endpoint = "/api/v1/admin/clients/test-agent/channels/echo/credentials/login";
        var body = new { username = "user", purpose = "target", type = "password", secret = "private-test-material" };

        var supplied = Guid.NewGuid().ToString();
        using (var suppliedResponse = await CredentialPutAsync(http, endpoint, supplied, body, deadline.Token))
        {
            suppliedResponse.EnsureSuccessStatusCode();
            Assert.Equal(supplied, Assert.Single(suppliedResponse.Headers.GetValues("X-RelayLink-Operation-Id")));
            var json = await suppliedResponse.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            Assert.Equal(supplied, json.GetProperty("operationId").GetString());
            Assert.True(json.GetProperty("applied").GetBoolean());
            Assert.DoesNotContain("private-test-material", json.ToString());
        }

        Guid generated;
        string? generatedUpdatedUtc;
        using (var generatedResponse = await CredentialPutWithoutOperationIdAsync(http, endpoint, body, deadline.Token))
        {
            generatedResponse.EnsureSuccessStatusCode();
            var header = Assert.Single(generatedResponse.Headers.GetValues("X-RelayLink-Operation-Id"));
            Assert.True(Guid.TryParse(header, out generated));
            Assert.NotEqual(Guid.Empty, generated);
            var json = await generatedResponse.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            Assert.Equal(generated.ToString(), json.GetProperty("operationId").GetString());
            Assert.True(json.GetProperty("applied").GetBoolean());
            Assert.Equal("recorded", json.GetProperty("auditStatus").GetString());
            generatedUpdatedUtc = json.GetProperty("updatedUtc").GetString();
        }

        // The server-generated operation ID is a real idempotency key: replaying the same
        // request with it returns the original receipt instead of writing the credential again.
        using (var replay = await CredentialPutAsync(http, endpoint, generated.ToString(), body, deadline.Token))
        {
            replay.EnsureSuccessStatusCode();
            Assert.Equal(generated.ToString(), Assert.Single(replay.Headers.GetValues("X-RelayLink-Operation-Id")));
            var json = await replay.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            Assert.Equal(generated.ToString(), json.GetProperty("operationId").GetString());
            Assert.Equal(generatedUpdatedUtc, json.GetProperty("updatedUtc").GetString());
            Assert.True(json.GetProperty("applied").GetBoolean());
        }

        var credentials = await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/clients/test-agent/channels/echo/credentials", deadline.Token);
        Assert.Single(credentials.GetProperty("credentials").EnumerateArray());

        using (var malformed = new HttpRequestMessage(HttpMethod.Put, endpoint)
        { Content = JsonContent.Create(new { username = "user", purpose = "target", type = "password", secret = "private-test-material" }) })
        {
            malformed.Headers.Add("Idempotency-Key", "not-a-uuid");
            using var rejected = await http.SendAsync(malformed, deadline.Token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("Idempotency-Key", await rejected.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("invalid-key")]
    [InlineData("wrong-key")]
    [InlineData("corrupt-store")]
    [InlineData("missing-store")]
    public async Task Credential_mutations_fail_closed_with_503_while_the_store_is_unavailable(string fault)
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync(configureBeforeStart: path =>
        {
            SeedCredentialStore(path);
            var directory = CredentialStore.DirectoryFor(path);
            if (fault == "missing-key") File.Delete(Path.Combine(directory, "key"));
            if (fault == "invalid-key") File.WriteAllText(Path.Combine(directory, "key"), "not base64");
            if (fault == "wrong-key") File.WriteAllText(Path.Combine(directory, "key"), Convert.ToBase64String(new byte[32]));
            if (fault == "corrupt-store") File.WriteAllText(Path.Combine(directory, "store"), "corrupted");
            if (fault == "missing-store") File.Delete(Path.Combine(directory, "store"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = await CredentialAdminAsync(fixture, deadline.Token);
        var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/credentials/status", deadline.Token);
        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.Equal("credential_store_unavailable", status.GetProperty("reason").GetString());

        const string endpoint = "/api/v1/admin/clients/test-agent/channels/echo/credentials/login";
        var body = new { username = "user", purpose = "target", type = "password", secret = "private-test-material" };
        using (var saved = await CredentialPutAsync(http, endpoint, Guid.NewGuid().ToString(), body, deadline.Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, saved.StatusCode);
            Assert.Contains("credential store is unavailable", await saved.Content.ReadAsStringAsync(deadline.Token), StringComparison.OrdinalIgnoreCase);
        }
        using (var removed = await CredentialDeleteAsync(http, endpoint, Guid.NewGuid().ToString(), deadline.Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, removed.StatusCode);
            Assert.Contains("credential store is unavailable", await removed.Content.ReadAsStringAsync(deadline.Token), StringComparison.OrdinalIgnoreCase);
        }
        using (var listed = await http.GetAsync("/api/v1/admin/clients/test-agent/channels/echo/credentials", deadline.Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, listed.StatusCode);
            Assert.Contains("credential store is unavailable", await listed.Content.ReadAsStringAsync(deadline.Token), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Credential_write_is_rejected_while_deletion_cleanup_is_pending()
    {
        using var fixture = new TunnelFixture();
        byte[]? storeBefore = null;
        await fixture.StartAsync(configureBeforeStart: path =>
        {
            SeedCredentialStore(path);
            CredentialStore.Open(path).BeginDeletionAsync("test-agent", "echo", default).GetAwaiter().GetResult();
            storeBefore = File.ReadAllBytes(Path.Combine(CredentialStore.DirectoryFor(path), "store"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = await CredentialAdminAsync(fixture, deadline.Token);
        const string endpoint = "/api/v1/admin/clients/test-agent/channels/echo/credentials/login";
        var operationId = Guid.NewGuid().ToString();
        using (var written = await CredentialPutAsync(http, endpoint, operationId,
            new { username = "user", purpose = "target", type = "password", secret = "private-test-material" }, deadline.Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, written.StatusCode);
            Assert.Equal(operationId, Assert.Single(written.Headers.GetValues("X-RelayLink-Operation-Id")));
            Assert.Contains("cleanup is pending", await written.Content.ReadAsStringAsync(deadline.Token), StringComparison.OrdinalIgnoreCase);
        }
        var storePath = Path.Combine(CredentialStore.DirectoryFor(fixture.ServerPath), "store");
        Assert.NotNull(storeBefore);
        Assert.Equal(storeBefore, File.ReadAllBytes(storePath));
        using (var listed = await http.GetAsync("/api/v1/admin/clients/test-agent/channels/echo/credentials", deadline.Token))
        {
            // The store is readable; a pending cleanup is a conflict, not a store outage.
            Assert.Equal(HttpStatusCode.Conflict, listed.StatusCode);
            Assert.Contains("cleanup is pending", await listed.Content.ReadAsStringAsync(deadline.Token), StringComparison.OrdinalIgnoreCase);
        }
        var marker = Assert.Single(await CredentialStore.Open(fixture.ServerPath).PendingDeletionsAsync(deadline.Token));
        Assert.Equal("test-agent", marker.ClientId);
        Assert.Equal("echo", marker.ChannelId);
    }

#if PEER_TLS_TESTS
    [Fact]
    public async Task Server_jump_inspection_fails_closed_when_the_credential_store_is_unavailable()
    {
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: "server-jump", prepareServer: path =>
        {
            SeedCredentialStore(path);
            File.WriteAllText(Path.Combine(CredentialStore.DirectoryFor(path), "store"), "corrupted");
            return Task.CompletedTask;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.WaitOnlineAsync(deadline.Token);
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri($"http://{fixture.DashboardAddress}") };
        using var login = await http.PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Add("X-RelayLink-CSRF", (await login.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("csrfToken").GetString());
        var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/credentials/status", deadline.Token);
        Assert.False(status.GetProperty("available").GetBoolean());
        Assert.Equal("credential_store_unavailable", status.GetProperty("reason").GetString());

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, deadline.Token);
        int read;
        using (var closeWindow = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try { read = await caller.GetStream().ReadAsync(new byte[1], closeWindow.Token); }
            catch (Exception exception) when (exception is IOException or SocketException) { read = 0; }
        }
        Assert.Equal(0, read);
        Assert.Equal(0, fixture.TargetConnections);

        // The jump listener accepted the connection but rejected it because inspection cannot
        // read credentials; the fail-closed decision must be visible in the audit trail.
        var rejected = false;
        for (var attempt = 0; attempt < 50 && !rejected; attempt++)
        {
            var audit = await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?hours=1&eventType=proxy_connection_rejected&page=1&pageSize=100", deadline.Token);
            rejected = audit.GetProperty("events").EnumerateArray().Any(item =>
                item.GetProperty("clientId").GetString() == "visited"
                && item.GetProperty("channelId").GetString() == "jump"
                && item.GetProperty("reasonCode").GetString() == "transport_error");
            if (!rejected) await Task.Delay(100, deadline.Token);
        }
        Assert.True(rejected, fixture.Logs());
        Assert.DoesNotContain("private-test-material", fixture.Logs());
    }
#endif

    private static async Task<HttpClient> CredentialAdminAsync(TunnelFixture fixture, CancellationToken token)
    {
        var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{fixture.DashboardPort}") };
        using var login = await http.PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        var json = await login.Content.ReadFromJsonAsync<JsonElement>(token);
        http.DefaultRequestHeaders.Add("X-RelayLink-CSRF", json.GetProperty("csrfToken").GetString());
        return http;
    }

    private static async Task<HttpResponseMessage> CredentialPutAsync(HttpClient http, string endpoint, string operationId, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", operationId);
        return await http.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> CredentialPutWithoutOperationIdAsync(HttpClient http, string endpoint, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint) { Content = JsonContent.Create(body) };
        return await http.SendAsync(request, token);
    }

    private static async Task<HttpResponseMessage> CredentialDeleteAsync(HttpClient http, string endpoint, string operationId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.Add("Idempotency-Key", operationId);
        return await http.SendAsync(request, token);
    }

    private static void SeedCredentialStore(string configurationPath) =>
        CredentialStore.Open(configurationPath)
            .SaveAsync("test-agent", "echo", "login", "user", "target", "password", "private-test-material"u8.ToArray(), default)
            .GetAwaiter().GetResult();
}
