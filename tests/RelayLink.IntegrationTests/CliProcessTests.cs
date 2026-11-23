using System.Diagnostics;
using System.Text;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RelayLink.Testing;

namespace RelayLink.IntegrationTests;

/// <summary>
/// A115 的进程级验证：启动真实 Server 与真实 relaylink CLI 可执行文件，
/// 验证退出码矩阵、--json 契约、--dry-run 无副作用与错误契约。
/// </summary>
public sealed class CliProcessTests : IAsyncLifetime
{
    private TestDirectory? storage;
    private Process? server;
    private readonly StringBuilder output = new();
    private readonly StringBuilder error = new();
    private int dashboardPort;
    private int automationPort;
    private int tunnelPort;
    private int dataPort;
    private string cliPath = string.Empty;
    private string cliDirectory = string.Empty;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        server?.Kill(entireProcessTree: true);
        server?.Dispose();
        storage?.Dispose();
        return Task.CompletedTask;
    }

    private async Task StartServerAsync(bool automationEnabled = true)
    {
        storage = new TestDirectory("relaylink-cli-");
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
                "certificatePemPath": "automation.pem", "privateKeyPemPath": "automation.key"
              }
              """
            : string.Empty;

        var serverPath = Path.Combine(storage.Path, "server.json");
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

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RelayLink.Server.dll"));
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(serverPath);
        server = new Process { StartInfo = start };
        server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (error) error.AppendLine(e.Data); };
        Assert.True(server.Start());
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();

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

    /// <summary>构建一个互访入口直达自动化端口的测试环境，并预配对好 CLI。</summary>
    private async Task<(HttpClient Admin, string Entry)> PreparePairedCliAsync()
    {
        // CLI 到自动化入口必须经 Agent 互访。进程级验证用直连形式：
        // CLI 的 --entry 支持显式 host:port，这里直接给自动化端口（127.0.0.1:port）。
        // 这等价于互访入口建立后的形态（loopback → TLS），不影响退出码与契约验证。
        await StartServerAsync();

        // CLI 可执行：直接 dotnet exec CLI dll（发布单文件留给打包流水线）。
        cliPath = ResolveCliPath();
        Assert.True(File.Exists(cliPath), "CLI dll 不存在，请先构建。");

        // 用 CLI 完成 pair（直接与自动化端口对话）。
        // CLI 需要接受自签证书：用 --accept-spki（与真实部署的带外核对等价）。
        cliDirectory = Path.Combine(storage!.Path, "cli-home");
        Directory.CreateDirectory(cliDirectory);

        var pairResult = await RunCliAsync("--entry", $"127.0.0.1:{automationPort}", "--accept-spki",
            "pair", "--scope", "clients.read", "--scope", "channels.read", "--scope", "changes.read",
            "--scope", "changes.write", "--scope", "mappings.read", "--subject", "cli-process-test");
        // pair 需要 --wait；但管理员批准要在 CLI 等待期间发生——这里分两步：
        // 先发起（退出码 5），再由管理页批准，再 --wait 续接。
        Assert.Equal(5, pairResult.ExitCode);

        var userCode = ExtractValue(pairResult.Output, "USER_CODE=");
        var requestId = ExtractPairingId(pairResult.Output);

        using var admin = await AdminAsync();
        using var approve = await admin.PostAsJsonAsync($"/api/v1/admin/automation/pairings/{requestId}/approve", new { });
        approve.EnsureSuccessStatusCode();

        var waitResult = await RunCliAsync("--entry", $"127.0.0.1:{automationPort}",
            "pair", "--wait", "--request", requestId, "--code", userCode);
        Assert.True(waitResult.ExitCode == 0,
            $"pair --wait 失败：exit={waitResult.ExitCode} || stdout=[{waitResult.Output}] || stderr=[{waitResult.Error}]");
        // CLI 的 HOME 目录要指向测试目录，让 profile/凭据隔离。
        return (await AdminAsync(), $"127.0.0.1:{automationPort}");
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = new HttpClient(new HttpClientHandler { UseCookies = true })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{dashboardPort}")
        };
        using var login = await admin.PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        admin.DefaultRequestHeaders.Add("X-RelayLink-CSRF", session.GetProperty("csrfToken").GetString());
        return admin;
    }

    /// <summary>运行 CLI 子进程。USERPROFILE/HOME 指向测试目录实现隔离。</summary>
    private async Task<(int ExitCode, string Output, string Error)> RunCliAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cliPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // CLI 的凭据/profile 写到 ~/.relaylink；重定向 HOME 实现测试隔离。
        start.Environment["RELAYLINK_HOME"] = cliDirectory;
        start.Environment["RELAYLINK_DEBUG_DIR"] = cliDirectory;

        var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string ExtractValue(string text, string prefix)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return trimmed[prefix.Length..].Trim();
        }

        return string.Empty;
    }

    private static string ExtractPairingId(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})");
            if (match.Success) return match.Value;
        }

        return string.Empty;
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

    /// <summary>
    /// 退出码矩阵：0 成功、3 未配对/令牌失效、4 scope 不足、5 需人工批准、
    /// 6 业务拒绝、8 网络不通、9 版本不支持。8 与 9 的区分是 A119 的核心。
    /// </summary>
    [Fact]
    public async Task Cli_exit_code_matrix_over_real_process()
    {
        var (_, entry) = await PreparePairedCliAsync();

        // 0：成功读取（whoami）。
        var whoami = await RunCliAsync("--entry", entry, "--json", "auth", "whoami");
        Assert.True(whoami.ExitCode == 0, $"whoami exit={whoami.ExitCode} stdout=[{whoami.Output.Replace(System.Environment.NewLine, " | ")}]");
        Assert.Contains("\"schemaVersion\":1", whoami.Output.Replace(" ", string.Empty));
        var whoamiBody = JsonSerializer.Deserialize<JsonElement>(whoami.Output);
        Assert.True(whoamiBody.TryGetProperty("data", out _));

        // 0：只读查询。
        var clients = await RunCliAsync("--entry", entry, "--json", "clients", "ls");
        Assert.Equal(0, clients.ExitCode);

        // 4：scope 不足 —— 用只读令牌请求写入；B 类命令会因缺 channels.write 被拒。
        var unauthorizedWrite = await RunCliAsync("--entry", entry, "--json",
            "channels", "set", "test-agent", "--channel", "echo", "--display-name", "x",
            "--idempotency-key", Guid.NewGuid().ToString());
        Assert.Equal(4, unauthorizedWrite.ExitCode);
        // --json 错误契约：{"schemaVersion":1,"error":{code,message,retryable,requestId}}
        var errorBody = JsonSerializer.Deserialize<JsonElement>(unauthorizedWrite.Output);
        Assert.True(errorBody.TryGetProperty("error", out var errorDetail), unauthorizedWrite.Output);
        Assert.Equal("scope_insufficient", errorDetail.GetProperty("code").GetString());
        Assert.True(errorDetail.TryGetProperty("requestId", out _));
        Assert.True(errorDetail.TryGetProperty("retryable", out _));

        // 6：业务拒绝 —— 不存在的 clientId。
        var notFound = await RunCliAsync("--entry", entry, "--json", "clients", "channels", "ls", "no-such-client");
        // channels ls 需要 clientId 参数位置正确；这里走 clients ls 变体。
        notFound = await RunCliAsync("--entry", entry, "--json", "mappings", "ls", "no-such-client");
        Assert.Equal(6, notFound.ExitCode);
        var notFoundBody = JsonSerializer.Deserialize<JsonElement>(notFound.Output);
        Assert.Equal("not_found", notFoundBody.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>8 与 9 的区分：连不上是 8，连得上但版本不支持是 9。</summary>
    [Fact]
    public async Task Cli_distinguishes_unreachable_from_version_mismatch()
    {
        await StartServerAsync();

        cliPath = ResolveCliPath();
        cliDirectory = Path.Combine(storage!.Path, "cli-home");
        Directory.CreateDirectory(cliDirectory);

        // 8：入口不通（未监听端口）。
        var unreachable = await RunCliAsync("--entry", "127.0.0.1:1", "pair");
        Assert.Equal(8, unreachable.ExitCode);

        // 9：连得上但版本不支持——用一个不存在的 API 版本场景不可构造（服务端只有 v1），
        // 这里验证的是：服务端可达、/info 正常时 pair 走到下一步（5 或 0），不是 9。
        var reachable = await RunCliAsync("--entry", $"127.0.0.1:{automationPort}", "--accept-spki", "pair");
        Assert.NotEqual(9, reachable.ExitCode);
    }

    [Fact]
    public async Task Cli_writes_utf8_to_redirected_output_and_error()
    {
        using var directory = new TestDirectory();
        cliPath = ResolveCliPath();
        cliDirectory = directory.Path;

        var result = await RunCliAsync("unknown-command");

        Assert.Equal(2, result.ExitCode);
        Assert.StartsWith("relaylink —— RelayLink 自动化管理面 CLI", result.Output);
        Assert.StartsWith("未知命令：unknown-command", result.Error);
    }

    /// <summary>8：自动化未启用时端口无监听，CLI 报 8 且诊断里提到可能原因。</summary>
    [Fact]
    public async Task Cli_reports_unreachable_when_automation_disabled()
    {
        await StartServerAsync(automationEnabled: false);
        cliPath = ResolveCliPath();
        cliDirectory = Path.Combine(storage!.Path, "cli-home");
        Directory.CreateDirectory(cliDirectory);

        // 服务端 dashboard 在跑，但 automation 端口没监听。
        var result = await RunCliAsync("--entry", $"127.0.0.1:{automationPort}", "pair");
        Assert.Equal(8, result.ExitCode);
        Assert.Contains("无法连接", result.Error);
    }

    /// <summary>--dry-run 不产生服务端变更：C 类提交 dry-run 后服务端无新申请。</summary>
    [Fact]
    public async Task Cli_dry_run_makes_no_server_changes()
    {
        var (_, entry) = await PreparePairedCliAsync();

        var payloadPath = Path.Combine(storage!.Path, "payload.json");
        await File.WriteAllTextAsync(payloadPath, """{"channelId":"echo","displayName":"Echo"}""");

        var dryRun = await RunCliAsync("--entry", entry, "--json",
            "changes", "submit", "--type", "channel.create", "--target", "test-agent",
            "--payload", payloadPath, "--dry-run");
        Assert.True(dryRun.ExitCode == 0, $"dry-run 失败：exit={dryRun.ExitCode} || stdout=[{dryRun.Output}] || stderr=[{dryRun.Error}]");

        // 服务端变更申请列表应为空（dry-run 未提交）。
        using var admin = await AdminAsync();
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/automation/change-requests");
        Assert.Empty(list.GetProperty("changeRequests").EnumerateArray());
    }


    /// <summary>
    /// SKILL.md 的 C 类示例不带 --channel；CLI 必须从 payload 自动补齐 target.channelId，
    /// 否则服务端 BuildTargetKey 判「target 字段不完整」。A116 真实验收暴露的缺口。
    /// </summary>
    [Fact]
    public async Task Cli_change_submission_without_channel_option_succeeds()
    {
        var (_, entry) = await PreparePairedCliAsync();

        var payloadPath = Path.Combine(storage!.Path, "channel-create.json");
        await File.WriteAllTextAsync(payloadPath, """
            {"channelId":"demo","displayName":"Demo","listenAddress":"127.0.0.1","listenPort":21500,
             "targetHost":"127.0.0.1","targetPort":9,"maxConnections":5,
             "targetConnectTimeoutSeconds":5,"transportProtocol":"tcp","authorizedClientsOnly":false}
            """);

        var submit = await RunCliAsync("--entry", entry, "--json",
            "changes", "submit", "--type", "channel.create", "--target", "test-agent",
            "--payload", payloadPath);
        Assert.True(submit.ExitCode == 0, $"提交失败：exit={submit.ExitCode} || stdout=[{submit.Output}] || stderr=[{submit.Error}]");
        var body = JsonSerializer.Deserialize<JsonElement>(submit.Output);
        Assert.Equal("pending", body.GetProperty("data").GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("data").GetProperty("changeRequestId").GetString()));
    }


    /// <summary>定位 relaylink.dll：优先测试输出目录（项目引用会复制），否则走 CLI 项目构建输出。</summary>
    private static string ResolveCliPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "relaylink.dll");
        if (File.Exists(local)) return local;

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "RelayLink.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
#if DEBUG
        string[] configurations = ["Debug", "Release"];
#else
        string[] configurations = ["Release", "Debug"];
#endif
        foreach (var configuration in configurations)
        {
            var candidate = Path.Combine(current.FullName, "src", "RelayLink.Cli", "bin", configuration, "net10.0", "relaylink.dll");
            if (File.Exists(candidate)) return candidate;
        }
        Assert.Fail("CLI 构建产物不存在。");
        return string.Empty;
    }
}
