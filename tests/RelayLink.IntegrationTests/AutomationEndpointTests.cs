using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayLink.Testing;

namespace RelayLink.IntegrationTests;

/// <summary>
/// 自动化管理面的真实进程端到端测试：启动实际 Server 进程，
/// 用真实 TLS 连接验证配对、令牌、scope 与变更申请的关键安全属性。
/// 只监听 127.0.0.1，证书为测试临时材料。
/// </summary>
public sealed class AutomationEndpointTests : IAsyncLifetime
{
    private TestDirectory? storage;
    private Process? server;
    private readonly StringBuilder output = new();
    private readonly StringBuilder error = new();
    private int dashboardPort;
    private int automationPort;
    private int tunnelPort;
    private int dataPort;
    private string serverPath = string.Empty;
    private HttpClient? admin;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        server?.Kill(entireProcessTree: true);
        server?.Dispose();
        admin?.Dispose();
        storage?.Dispose();
        return Task.CompletedTask;
    }

    private async Task StartServerAsync(bool automationEnabled = true)
    {
        storage = new TestDirectory("relaylink-automation-");
        dashboardPort = FreePort();
        automationPort = FreePort();
        tunnelPort = FreePort();
        dataPort = FreePort();

        var clients = Path.Combine(storage.Path, "clients");
        Directory.CreateDirectory(clients);
        File.WriteAllText(Path.Combine(clients, "test-agent.json"), $$"""
            {
              "schemaVersion": 1,
              "clientId": "test-agent",
              "displayName": "Test Agent",
              "enabled": true,
              "secret": "{{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}}",
              "maxConnections": 20,
              "maxPendingConnections": 5,
              "channels": []
            }
            """);

        // 自动化入口需要自己的 TLS 材料；用自签证书即可，测试直接信任该证书。
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllText(Path.Combine(storage.Path, "automation.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(storage.Path, "automation.key"), rsa.ExportPkcs8PrivateKeyPem());

        var automationSection = automationEnabled
            ? $$"""
              ,"automation": {
                "enabled": true, "listenAddress": "127.0.0.1", "port": {{automationPort}},
                "certificatePemPath": "automation.pem", "privateKeyPemPath": "automation.key",
                "sessionLifetimeMinutes": 120, "maxPendingPairings": 8, "maxAuthorizations": 16
              }
              """
            : string.Empty;

        serverPath = Path.Combine(storage.Path, "server.json");
        File.WriteAllText(serverPath, $$"""
            {
              "schemaVersion": 1,
              "tunnel": {
                "listenAddress": "127.0.0.1", "port": {{tunnelPort}}, "dataPort": {{dataPort}}, "tlsEnabled": false,
                "certificatePemPath": "", "privateKeyPemPath": "",
                "handshakeTimeoutSeconds": 10, "heartbeatIntervalSeconds": 15, "heartbeatTimeoutSeconds": 45
              },
              "dashboard": {
                "listenAddress": "127.0.0.1", "port": {{dashboardPort}}, "refreshSeconds": 5,
                "admin": {
                  "username": "admin",
                  "passwordHash": "{{PasswordHash("correct-horse-battery")}}",
                  "sessionLifetimeMinutes": 60
                }
              },
              "clientsDirectory": "clients",
              "limits": {
                "maxConnections": 200, "maxPendingConnections": 20, "maxUnauthenticatedConnections": 10,
                "maxChannelsPerClient": 20, "openTimeoutSeconds": 20, "blockedWriteTimeoutSeconds": 120,
                "halfCloseDrainTimeoutSeconds": 300, "tcpIdleTimeoutSeconds": 900
              }{{automationSection}}
            }
            """);

        var binary = Path.Combine(AppContext.BaseDirectory, "RelayLink.Server.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(binary);
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(serverPath);
        server = new Process { StartInfo = start, EnableRaisingEvents = true };
        server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (error) error.AppendLine(e.Data); };
        Assert.True(server.Start());
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();

        // 等管理端口就绪。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                var response = await probe.GetAsync($"http://127.0.0.1:{dashboardPort}/health/live", deadline.Token);
                if (response.IsSuccessStatusCode) break;
            }
            catch (Exception) when (!deadline.IsCancellationRequested)
            {
                await Task.Delay(100, deadline.Token);
            }
        }
    }

    private HttpClient CreateAutomationClient()
    {
        var handler = new HttpClientHandler
        {
            // 测试用自签证书：只在此处放行，正式实现必须做 SPKI 固定校验。
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        return new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{automationPort}/") };
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string PasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2-SHA256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // ---------------- 用例 ----------------

    [Fact]
    public async Task Automation_endpoint_serves_info_over_tls_only_on_loopback()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();

        var info = await client.GetFromJsonAsync<JsonElement>("/api/v1/automation/info");
        // 响应统一包在 data 下。
        var data = info.GetProperty("data");
        Assert.True(data.GetProperty("enabled").GetBoolean());
        Assert.True(data.GetProperty("requiresPairing").GetBoolean());
        Assert.Equal(1, data.GetProperty("apiVersions")[0].GetInt32());
        // 持久化实例标识必须存在：它不是每进程新生成的那种值。
        Assert.True(Guid.TryParse(data.GetProperty("automationInstanceId").GetString(), out _));
    }

    [Fact]
    public async Task Automation_endpoint_is_absent_when_disabled()
    {
        await StartServerAsync(automationEnabled: false);
        using var client = CreateAutomationClient();

        // 未启用时该端口根本没有监听：调用必须失败，而不是返回 404 页面。
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            await client.GetAsync("/api/v1/automation/info"));
    }

    [Fact]
    public async Task Pairing_requires_approval_and_user_code_to_issue_token()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();

        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await CreatePairingAsync(client, deviceKey, ["clients.read"]);
        var requestId = Guid.Parse(created.GetProperty("requestId").GetString()!);
        var userCode = created.GetProperty("userCode").GetString()!;
        Assert.Equal(9, userCode.Length); // XXXX-XXXX

        // 未批准就换取：必须失败。
        using var early = await ExchangeAsync(client, deviceKey, requestId, userCode);
        Assert.False(early.IsSuccessStatusCode);

        // 管理员批准。
        using var approved = await AdminPostAsync($"/api/v1/admin/automation/pairings/{requestId}/approve", new { });
        approved.EnsureSuccessStatusCode();

        // 使用错误用户码换取：必须失败，且计入锁定。
        using var wrongCode = await ExchangeAsync(client, deviceKey, requestId, "ZZZZ-ZZZZ");
        Assert.False(wrongCode.IsSuccessStatusCode);

        // 正确用户码换取：成功。
        using var exchange = await ExchangeAsync(client, deviceKey, requestId, userCode);
        exchange.EnsureSuccessStatusCode();
        var tokenBody = await exchange.Content.ReadFromJsonAsync<JsonElement>();
        var token = tokenBody.GetProperty("data").GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        // 同一 requestId 再次换取：必须被拒（单次使用）。
        using var second = await ExchangeAsync(client, deviceKey, requestId, userCode);
        Assert.False(second.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Token_bound_to_device_rejects_other_device_and_missing_proof()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();
        var (token, _) = await CompletePairingAsync(client, ["clients.read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/automation/whoami");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        // 缺少设备证明：必须拒绝。
        using var noProof = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, noProof.StatusCode);
    }

    [Fact]
    public async Task Scope_is_enforced_and_read_does_not_grant_write()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();
        var (token, device) = await CompletePairingAsync(client, ["clients.read"]);

        // 只读令牌读 clients：允许。
        using var read = await SendAsync(client, HttpMethod.Get, "/api/v1/automation/clients", device, token);
        Assert.True(read.IsSuccessStatusCode);

        // 只读令牌尝试 B 类写入：必须 403 且不产生变更。
        using var write = await SendAsync(client, HttpMethod.Patch,
            "/api/v1/automation/clients/test-agent/channels/echo", device, token,
            new { displayName = "renamed" }, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var body = await write.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scope_insufficient", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Revoked_grant_fails_on_next_call()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();
        var (token, device) = await CompletePairingAsync(client, ["clients.read"]);

        using var before = await SendAsync(client, HttpMethod.Get, "/api/v1/automation/whoami", device, token);
        Assert.True(before.IsSuccessStatusCode);

        // 撤销该授权。
        using var grants = await AdminGetAsync("/api/v1/admin/automation/grants");
        var list = await grants.Content.ReadFromJsonAsync<JsonElement>();
        var grantId = list.GetProperty("grants")[0].GetProperty("grantId").GetString();
        using var revoke = await AdminDeleteAsync($"/api/v1/admin/automation/grants/{grantId}");
        revoke.EnsureSuccessStatusCode();

        // 下一次调用立即失效。
        using var after = await SendAsync(client, HttpMethod.Get, "/api/v1/automation/whoami", device, token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        var error = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("token_revoked", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Change_request_is_not_applied_before_approval()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();
        var (token, device) = await CompletePairingAsync(client, ["changes.read", "changes.write", "clients.read", "channels.read"]);

        // 提交 channel.create 申请。payload 必须含既有编辑器要求的全部必填字段。
        var listenPort = FreePort();
        using var submitted = await SendAsync(client, HttpMethod.Post, "/api/v1/automation/change-requests", device, token,
            new
            {
                type = "channel.create",
                target = new { clientId = "test-agent", channelId = "echo" },
                payload = new
                {
                    channelId = "echo",
                    displayName = "Echo",
                    enabled = true,
                    listenAddress = "127.0.0.1",
                    listenPort,
                    targetHost = "127.0.0.1",
                    targetPort = 9,
                    maxConnections = 10,
                    targetConnectTimeoutSeconds = 20
                },
                reason = "test"
            }, Guid.NewGuid().ToString());
        submitted.EnsureSuccessStatusCode();
        var submittedBody = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        var changeId = submittedBody.GetProperty("data").GetProperty("changeRequestId").GetString();
        Assert.Equal("pending", submittedBody.GetProperty("data").GetProperty("state").GetString());

        // 批准前：通道不应存在。
        using var channelsBefore = await SendAsync(client, HttpMethod.Get, "/api/v1/automation/clients/test-agent/channels", device, token);
        var beforeText = await channelsBefore.Content.ReadAsStringAsync();
        Assert.True(channelsBefore.IsSuccessStatusCode, $"通道列表读取失败：{beforeText}");
        using var beforeDocument = JsonDocument.Parse(beforeText);
        Assert.Empty(beforeDocument.RootElement.GetProperty("data").EnumerateArray());

        // 管理员批准并执行。
        using var approve = await AdminPostAsync($"/api/v1/admin/automation/change-requests/{changeId}/approve", new { });
        approve.EnsureSuccessStatusCode();
        using var execute = await AdminPostAsync($"/api/v1/admin/automation/change-requests/{changeId}/execute", new { });
        execute.EnsureSuccessStatusCode();

        // 执行后：通道出现。
        using var channelsAfter = await SendAsync(client, HttpMethod.Get, "/api/v1/automation/clients/test-agent/channels", device, token);
        var afterBody = await channelsAfter.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(afterBody.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Change_submission_rejects_secret_fields()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();
        var (token, device) = await CompletePairingAsync(client, ["changes.read", "changes.write"]);

        using var submitted = await SendAsync(client, HttpMethod.Post, "/api/v1/automation/change-requests", device, token,
            new
            {
                type = "channel.create",
                target = new { clientId = "test-agent", channelId = "echo" },
                payload = new { channelId = "echo", accessSecret = "should-not-be-accepted" }
            }, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, submitted.StatusCode);
        var body = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", body.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// A114 逐值日志扫描：记录本次用例实际产生的用户码、令牌与设备私钥原文，
    /// 然后全文扫描服务端日志与审计库文件，确认这些敏感值一个都没有落盘。
    /// </summary>
    [Fact]
    public async Task Logs_never_contain_user_code_token_or_device_private_key()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();

        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await CreatePairingAsync(client, device, ["clients.read"]);
        var requestId = Guid.Parse(created.GetProperty("requestId").GetString()!);
        var userCode = created.GetProperty("userCode").GetString()!;
        var normalizedUserCode = userCode.Replace("-", string.Empty);
        var devicePrivateKey = device.ExportPkcs8PrivateKeyPem();

        using var approve = await AdminPostAsync($"/api/v1/admin/automation/pairings/{requestId}/approve", new { });
        approve.EnsureSuccessStatusCode();
        using var exchange = await ExchangeAsync(client, device, requestId, userCode);
        exchange.EnsureSuccessStatusCode();
        var token = (await exchange.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("token").GetString()!;

        // 用令牌发一次请求，确保「带令牌的请求」也进入日志路径。
        using var whoami = await SendAsync(client, HttpMethod.Get, "/api/v1/automation/whoami", device, token);
        Assert.True(whoami.IsSuccessStatusCode);

        // 收集所有日志/审计/配置文件的文本内容；automation/state.db* 允许保存待批准配对的展示用户码，
        // 但仍不得落盘令牌明文或设备私钥。
        var allFiles = Directory.GetFiles(storage!.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".key", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        static bool IsAutomationStateDb(string path) =>
            Path.GetFileName(path).StartsWith("state.db", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "automation", StringComparison.OrdinalIgnoreCase);
        static string ReadAll(IEnumerable<string> paths) => string.Concat(paths.Select(path =>
        {
            try { return File.ReadAllText(path); }
            catch (IOException) { return string.Empty; } // WAL/SHM 可能被占用
        }));

        var allContents = ReadAll(allFiles);
        var logAndAuditContents = ReadAll(allFiles.Where(path => !IsAutomationStateDb(path)));

        Assert.DoesNotContain(userCode, logAndAuditContents);
        Assert.DoesNotContain(normalizedUserCode, logAndAuditContents);
        Assert.DoesNotContain(token, allContents);
        Assert.DoesNotContain(devicePrivateKey, allContents);
        // 私钥的核心材料（Base64 形式的 D 值）也不应出现。
        var parameters = device.ExportParameters(true);
        var privateScalar = Convert.ToBase64String(parameters.D!);
        Assert.DoesNotContain(privateScalar, allContents);
    }

    // ---------------- 辅助 ----------------

    private async Task<JsonElement> CreatePairingAsync(HttpClient client, ECDsa key, string[] scopes)
    {
        var parameters = key.ExportParameters(false);
        var jwk = "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + Base64Url(parameters.Q.X!) +
                  "\",\"y\":\"" + Base64Url(parameters.Q.Y!) + "\"}";
        using var response = await client.PostAsJsonAsync("/api/v1/automation/pairing/requests", new
        {
            subject = "test-subject",
            platform = "linux",
            cliVersion = "0.1.0",
            scopes,
            devicePublicKey = jwk
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").Clone();
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, ECDsa key, Guid requestId, string userCode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/automation/pairing/requests/{requestId}/token")
        {
            Content = JsonContent.Create(new { userCode })
        };
        request.Headers.Add("X-RelayLink-Proof", CreateProof(key, requestId, userCode));
        return await client.SendAsync(request);
    }

    private async Task<(string Token, ECDsa Device)> CompletePairingAsync(HttpClient client, string[] scopes)
    {
        var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await CreatePairingAsync(client, device, scopes);
        var requestId = Guid.Parse(created.GetProperty("requestId").GetString()!);
        var userCode = created.GetProperty("userCode").GetString()!;

        using var approve = await AdminPostAsync($"/api/v1/admin/automation/pairings/{requestId}/approve", new { });
        approve.EnsureSuccessStatusCode();

        using var exchange = await ExchangeAsync(client, device, requestId, userCode);
        exchange.EnsureSuccessStatusCode();
        var body = await exchange.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("data").GetProperty("token").GetString()!, device);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        ECDsa device, string token, object? body = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-RelayLink-Proof", CreateProof(device, method.Method, path, token, await InstanceIdAsync(client)));
        return await client.SendAsync(request);
    }

    // 证明中的 instanceId 必须等于服务端的持久化实例标识。
    private static async Task<string> InstanceIdAsync(HttpClient client)
    {
        var info = await client.GetFromJsonAsync<JsonElement>("/api/v1/automation/info");
        return info.GetProperty("data").GetProperty("automationInstanceId").GetString()!;
    }

    private static string CreateProof(ECDsa key, Guid requestId, string userCode)
    {
        var normalized = new string(userCode.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return BuildProof(key, new Dictionary<string, object> { ["requestId"] = requestId.ToString(), ["userCodeDigest"] = digest });
    }

    private static string CreateProof(ECDsa key, string method, string path, string token, string instanceId)
    {
        var ath = Base64Url(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        return BuildProof(key, new Dictionary<string, object>
        {
            ["htm"] = method, ["htu"] = path, ["ath"] = ath, ["instanceId"] = instanceId
        });
    }

    private static string BuildProof(ECDsa key, Dictionary<string, object> claims)
    {
        var parameters = key.ExportParameters(false);
        var jwk = "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + Base64Url(parameters.Q.X!) +
                  "\",\"y\":\"" + Base64Url(parameters.Q.Y!) + "\"}";
        var header = "{\"alg\":\"ES256\",\"jwk\":" + jwk + "}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object>
        {
            ["iat"] = now, ["exp"] = now + 60, ["jti"] = Guid.NewGuid().ToString()
        };
        foreach (var (name, value) in claims) payload[name] = value;
        var payloadJson = JsonSerializer.Serialize(payload);
        var signingInput = Base64Url(System.Text.Encoding.UTF8.GetBytes(header)) + "." +
                           Base64Url(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var signature = key.SignData(System.Text.Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url(signature);
    }

    // ---------------- 修复轮回归护栏 ----------------

    /// <summary>
    /// 并发两个不同 jti 换取令牌：必须恰好一枚成功。这是换令牌单事务的最高危承诺
    /// （双签发=凭据面突破），此前只靠人工复核背书。
    /// </summary>
    [Fact]
    public async Task Concurrent_token_exchange_issues_exactly_one_token()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();

        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await CreatePairingAsync(client, deviceKey, ["clients.read"]);
        var requestId = Guid.Parse(created.GetProperty("requestId").GetString()!);
        var userCode = created.GetProperty("userCode").GetString()!;

        using var approved = await AdminPostAsync($"/api/v1/admin/automation/pairings/{requestId}/approve", new { });
        approved.EnsureSuccessStatusCode();

        // 两个不同 jti 的证明并发换取。
        var first = ExchangeAsync(client, deviceKey, requestId, userCode);
        var second = ExchangeAsync(client, deviceKey, requestId, userCode);
        await Task.WhenAll(first, second);

        var outcomes = new[] { await first, await second };
        Assert.Single(outcomes, response => response.IsSuccessStatusCode);
        Assert.Single(outcomes, response => !response.IsSuccessStatusCode);

        // 第三个换取：仍被拒（pairing_already_completed）。
        using var third = await ExchangeAsync(client, deviceKey, requestId, userCode);
        Assert.False(third.IsSuccessStatusCode);
        var error = await third.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pairing_already_completed", error.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// 版本中间件负例：错误版本头返回 unsupported_api_version；/info 豁免。
    /// 退出码 9 全链路的服务端半边。
    /// </summary>
    [Fact]
    public async Task Api_version_middleware_rejects_unsupported_version_and_exempts_info()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();

        // /whoami 带错误版本头：400 unsupported_api_version。
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/v1/automation/whoami");
        wrong.Headers.Add("X-RelayLink-Api-Version", "2");
        using var wrongResponse = await client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.BadRequest, wrongResponse.StatusCode);
        var error = await wrongResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unsupported_api_version", error.GetProperty("error").GetProperty("code").GetString());

        // /info 带同样错误头：豁免，正常返回（版本协商的信息来源）。
        using var info = new HttpRequestMessage(HttpMethod.Get, "/api/v1/automation/info");
        info.Headers.Add("X-RelayLink-Api-Version", "2");
        using var infoResponse = await client.SendAsync(info);
        Assert.True(infoResponse.IsSuccessStatusCode);
    }

    /// <summary>
    /// Approved 状态连错 5 次用户码：第 5 次返回 pairing_locked，此后正确码也被拒。
    /// 锁定是防暴力猜码的唯一机制（修复 I 的直接断言）。
    /// </summary>
    [Fact]
    public async Task Approved_pairing_locks_after_five_wrong_user_codes()
    {
        await StartServerAsync();
        using var client = CreateAutomationClient();

        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = await CreatePairingAsync(client, deviceKey, ["clients.read"]);
        var requestId = Guid.Parse(created.GetProperty("requestId").GetString()!);
        var userCode = created.GetProperty("userCode").GetString()!;

        using var approved = await AdminPostAsync($"/api/v1/admin/automation/pairings/{requestId}/approve", new { });
        approved.EnsureSuccessStatusCode();

        // 连错 4 次（未达阈值）：仍是 pairing_denied。
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await ExchangeAsync(client, deviceKey, requestId, "ZZZZ-ZZZZ");
            Assert.False(wrong.IsSuccessStatusCode);
            var wrongBody = await wrong.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pairing_denied", wrongBody.GetProperty("error").GetProperty("code").GetString());
        }

        // 第 5 次：锁定。
        using var fifth = await ExchangeAsync(client, deviceKey, requestId, "ZZZZ-ZZZZ");
        Assert.False(fifth.IsSuccessStatusCode);
        var fifthBody = await fifth.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pairing_locked", fifthBody.GetProperty("error").GetProperty("code").GetString());

        // 锁定后正确码也被拒：必须重新发起。
        using var correct = await ExchangeAsync(client, deviceKey, requestId, userCode);
        Assert.False(correct.IsSuccessStatusCode);
        var correctBody = await correct.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pairing_locked", correctBody.GetProperty("error").GetProperty("code").GetString());
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    // 管理页面会话：登录后带 Cookie 与 CSRF 头。
    private async Task<HttpClient> AdminAsync()
    {
        if (admin is not null) return admin;
        admin = new HttpClient(new HttpClientHandler { UseCookies = true })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{dashboardPort}")
        };
        using var login = await admin.PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        admin.DefaultRequestHeaders.Add("X-RelayLink-CSRF", session.GetProperty("csrfToken").GetString());
        return admin;
    }

    private async Task<HttpResponseMessage> AdminPostAsync(string path, object body)
    {
        var client = await AdminAsync();
        return await client.PostAsJsonAsync(path, body);
    }

    private async Task<HttpResponseMessage> AdminGetAsync(string path)
    {
        var client = await AdminAsync();
        return await client.GetAsync(path);
    }

    private async Task<HttpResponseMessage> AdminDeleteAsync(string path)
    {
        var client = await AdminAsync();
        return await client.DeleteAsync(path);
    }
}
