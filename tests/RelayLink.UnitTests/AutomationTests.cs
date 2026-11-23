using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Automation;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Testing;
using Xunit;

namespace RelayLink.UnitTests;

/// <summary>
/// 自动化管理面的单元测试：scope 语义、用户码、设备证明、配置校验与工具类。
/// 这些都是安全边界，必须独立于任何网络环境验证。
/// </summary>
public sealed class AutomationTests
{
    // ---------------- scope 语义 ----------------

    [Fact]
    public void Write_scope_implies_matching_read_scope()
    {
        // 写 scope 覆盖同名读 scope。
        Assert.True(AutomationScopes.Satisfies([AutomationScopes.ClientsWrite], AutomationScopes.ClientsRead));
        Assert.True(AutomationScopes.Satisfies([AutomationScopes.ChannelsWrite], AutomationScopes.ChannelsRead));
        Assert.True(AutomationScopes.Satisfies([AutomationScopes.SecurityWrite], AutomationScopes.SecurityRead));
    }

    [Fact]
    public void Read_scope_does_not_imply_write_scope()
    {
        // 这是关键方向：只读令牌不得获得写能力。
        Assert.False(AutomationScopes.Satisfies([AutomationScopes.ClientsRead], AutomationScopes.ClientsWrite));
        Assert.False(AutomationScopes.Satisfies([AutomationScopes.ChannelsRead], AutomationScopes.ChannelsWrite));
        Assert.False(AutomationScopes.Satisfies([AutomationScopes.SecurityRead], AutomationScopes.SecurityWrite));
    }

    [Fact]
    public void Unrelated_scope_does_not_satisfy()
    {
        Assert.False(AutomationScopes.Satisfies([AutomationScopes.AuditRead], AutomationScopes.ClientsRead));
        Assert.False(AutomationScopes.Satisfies([], AutomationScopes.ServerRead));
    }

    [Fact]
    public void Normalize_drops_unknown_and_deduplicates()
    {
        var normalized = AutomationScopes.Normalize(["clients.read", "clients.read", "bogus.scope", "  audit.read  "]);
        Assert.Equal([AutomationScopes.ClientsRead, AutomationScopes.AuditRead], normalized);
    }

    [Fact]
    public void Approval_cannot_expand_beyond_requested()
    {
        string[] requested = [AutomationScopes.ClientsRead, AutomationScopes.ChannelsRead];

        // 缩小批准：允许。
        Assert.True(AutomationScopes.IsSubsetOf([AutomationScopes.ClientsRead], requested));
        // 申请中未列出的 scope：拒绝。
        Assert.False(AutomationScopes.IsSubsetOf([AutomationScopes.ClientsRead, AutomationScopes.AuditRead], requested));
    }

    // ---------------- 用户码 ----------------

    [Fact]
    public void User_code_is_eight_unambiguous_characters()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var code = UserCode.Generate();
            Assert.Equal(8, code.Length);
            // 必须排除易混淆字符。
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('1', code);
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('L', code);
        }
    }

    [Fact]
    public void User_code_display_has_separator_and_normalizes_back()
    {
        var code = UserCode.Generate();
        var display = UserCode.Format(code);
        Assert.Equal(9, display.Length);
        Assert.Equal('-', display[4]);
        Assert.Equal(code, UserCode.Normalize(display));
        // 用户输入带空格与小写也能归一化。
        Assert.Equal(code, UserCode.Normalize(display.ToLowerInvariant().Replace("-", " - ")));
    }

    [Fact]
    public void User_code_digest_is_stable_and_code_specific()
    {
        Assert.Equal(UserCode.Digest("ABCDEFGH"), UserCode.Digest("ABCDEFGH"));
        Assert.NotEqual(UserCode.Digest("ABCDEFGH"), UserCode.Digest("ABCDEFGI"));
    }

    [Fact]
    public void Fixed_time_comparison_rejects_different_inputs()
    {
        Assert.True(UserCode.FixedTimeEquals("ABC", "ABC"));
        Assert.False(UserCode.FixedTimeEquals("ABC", "ABD"));
        Assert.False(UserCode.FixedTimeEquals("ABC", "ABCD"));
        Assert.False(UserCode.FixedTimeEquals(null, "ABC"));
    }

    // ---------------- 设备证明 ----------------

    [Fact]
    public void Device_proof_round_trips_and_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(key, new Dictionary<string, object?>
        {
            ["htm"] = "GET",
            ["htu"] = "/api/v1/automation/whoami",
            ["jti"] = Guid.NewGuid().ToString()
        });

        Assert.True(DeviceProof.TryParse(header, key, DateTimeOffset.UtcNow, null, out var proof, out var error), error?.Message);
        Assert.Equal("GET", proof!.Method);
        Assert.Equal("/api/v1/automation/whoami", proof.Uri);
    }

    [Fact]
    public void Device_proof_rejects_tampered_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(key, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x", ["jti"] = Guid.NewGuid().ToString() });
        var parts = header.Split('.');
        // 篡改载荷，签名即失效。
        var tampered = parts[0] + "." + Base64Url(Encoding.UTF8.GetBytes("{\"iat\":1,\"exp\":2,\"jti\":\"x\",\"htm\":\"POST\",\"htu\":\"/x\"}")) + "." + parts[2];

        Assert.False(DeviceProof.TryParse(tampered, key, DateTimeOffset.UtcNow, null, out _, out var error));
        Assert.Equal("proof_invalid", error!.Code);
    }

    [Fact]
    public void Device_proof_rejects_wrong_public_key()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(signer, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x", ["jti"] = Guid.NewGuid().ToString() });

        Assert.False(DeviceProof.TryParse(header, other, DateTimeOffset.UtcNow, null, out _, out var error));
        Assert.Equal("proof_invalid", error!.Code);
    }

    [Fact]
    public void Device_proof_rejects_expired_window()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);
        var header = CreateProof(key, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x", ["jti"] = Guid.NewGuid().ToString() }, old);

        Assert.False(DeviceProof.TryParse(header, key, DateTimeOffset.UtcNow, null, out _, out var error));
        Assert.Equal("proof_invalid", error!.Code);
    }

    [Fact]
    public void Device_proof_rejects_thumbprint_mismatch()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(signer, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x", ["jti"] = Guid.NewGuid().ToString() });

        // 令牌绑定的是另一个设备的指纹：必须拒绝。
        Assert.False(DeviceProof.TryParse(header, signer, DateTimeOffset.UtcNow, "deadbeef", out _, out var error));
        Assert.Equal("proof_invalid", error!.Code);
    }

    // ---------------- 证明窗口边界（修复 A 的回归护栏） ----------------
    // 这组用例锁死「exp-iat ≤60s + iat 超前 ≤60s + skew 60s」的联动数值：
    // 任何人单独调整其一（例如把 60 改回 65）而不同步 jti 保留期，测试就会失败。

    [Fact]
    public void Proof_with_lifetime_of_61_seconds_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(key, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x" },
            lifetimeSeconds: 61);

        Assert.False(DeviceProof.TryParse(header, key, DateTimeOffset.UtcNow, null, out _, out var error));
        Assert.Equal("proof_invalid", error!.Code);
    }

    [Fact]
    public void Proof_with_lifetime_of_exactly_60_seconds_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(key, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x" },
            lifetimeSeconds: 60);

        Assert.True(DeviceProof.TryParse(header, key, DateTimeOffset.UtcNow, null, out _, out var error), error?.Message);
    }

    [Fact]
    public void Proof_with_issued_at_61_seconds_in_future_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(key, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x" },
            issuedAtOffsetSeconds: 61);

        Assert.False(DeviceProof.TryParse(header, key, DateTimeOffset.UtcNow, null, out _, out var error));
        Assert.Equal("proof_invalid", error!.Code);
    }

    [Fact]
    public void Proof_with_issued_at_exactly_60_seconds_in_future_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(key, new Dictionary<string, object?> { ["htm"] = "GET", ["htu"] = "/x" },
            issuedAtOffsetSeconds: 60);

        Assert.True(DeviceProof.TryParse(header, key, DateTimeOffset.UtcNow, null, out _, out var error), error?.Message);
    }

    [Fact]
    public void Uri_match_requires_method_path_and_query()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var header = CreateProof(key, new Dictionary<string, object?>
        {
            ["htm"] = "POST",
            ["htu"] = "/api/v1/automation/change-requests?x=1",
            ["jti"] = Guid.NewGuid().ToString()
        });
        Assert.True(DeviceProof.TryParse(header, key, DateTimeOffset.UtcNow, null, out var proof, out _));

        Assert.True(DeviceProof.UriMatches(proof!, "POST", "/api/v1/automation/change-requests", "x=1"));
        Assert.False(DeviceProof.UriMatches(proof!, "GET", "/api/v1/automation/change-requests", "x=1"));
        Assert.False(DeviceProof.UriMatches(proof!, "POST", "/api/v1/automation/change-requests", null));
    }

    private static string CreateProof(ECDsa key, IReadOnlyDictionary<string, object?> claims, DateTimeOffset? at = null, int? lifetimeSeconds = null, int? issuedAtOffsetSeconds = null)
    {
        var parameters = key.ExportParameters(false);
        var jwk = "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + Base64Url(parameters.Q.X!) + "\",\"y\":\"" + Base64Url(parameters.Q.Y!) + "\"}";
        var header = "{\"alg\":\"ES256\",\"jwk\":" + jwk + "}";
        var now = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        // issuedAtOffsetSeconds 模拟 iat 超前/滞后（窗口边界负例）；lifetimeSeconds 覆盖 exp-iat 上限负例。
        var issuedAt = now + (issuedAtOffsetSeconds ?? 0);
        var expiresAt = issuedAt + (lifetimeSeconds ?? 60);
        var payload = "{\"iat\":" + issuedAt + ",\"exp\":" + expiresAt + ",\"jti\":\"" +
                      (claims.TryGetValue("jti", out var jti) ? jti : Guid.NewGuid().ToString()) + "\",\"htm\":\"" +
                      claims["htm"] + "\",\"htu\":\"" + claims["htu"] + "\"}";
        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    // ---------------- 配置校验 ----------------

    // 这些用例只验证自动化配置的校验逻辑，因此关闭 tunnel TLS，
    // 避免加载器先去读取不存在的隧道证书文件而提前失败。
    private static ServerConfiguration ServerWith(AutomationConfiguration automation) => new(
        1,
        new TunnelConfiguration("0.0.0.0", 7443, false, string.Empty, string.Empty, 10, 15, 45),
        new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "PBKDF2-SHA256$210000$x$y", 60)),
        "/tmp/clients",
        new LimitsConfiguration(2000, 200, 100, 100, 20, 120, 300),
        null)
    {
        Automation = automation
    };

    [Fact]
    public void Automation_must_bind_loopback_only()
    {
        var loader = new ConfigurationLoader();

        // 通过文件加载来验证：这样 Resolved* 路径由加载器填充，测的是真实启动路径。
        using var directory = new TestDirectory();
        foreach (var address in new[] { "0.0.0.0", "::", "192.168.1.10", "localhost" })
        {
            var path = WriteServerConfiguration(directory, address);
            var exception = Assert.Throws<ConfigurationException>(() => loader.Load(path));
            Assert.Contains("listenAddress", exception.Message);
        }

        var loopbackPath = WriteServerConfiguration(directory, "127.0.0.1");
        var loaded = loader.Load(loopbackPath);
        Assert.NotNull(loaded.Server.Automation);
        Assert.Equal("127.0.0.1", loaded.Server.Automation!.ListenAddress);
    }

    /// <summary>写一份最小可用 server.json，并生成自签证书供自动化入口使用。</summary>
    private static string WriteServerConfiguration(TestDirectory directory, string automationListenAddress)
    {
        var clients = Path.Combine(directory.Path, "clients");
        Directory.CreateDirectory(clients);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=relaylink-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var certificatePath = Path.Combine(directory.Path, "automation.pem");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        var keyPath = Path.Combine(directory.Path, "automation.key");
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        var path = Path.Combine(directory.Path, "server.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1,
              "tunnel": {
                "listenAddress": "0.0.0.0", "port": 7443, "dataPort": 7444, "tlsEnabled": false,
                "certificatePemPath": "", "privateKeyPemPath": "",
                "handshakeTimeoutSeconds": 10, "heartbeatIntervalSeconds": 15, "heartbeatTimeoutSeconds": 45
              },
              "dashboard": {
                "listenAddress": "127.0.0.1", "port": 18080, "refreshSeconds": 5,
                "admin": { "username": "admin", "passwordHash": "PBKDF2-SHA256$210000$c2FsdA==$aGFzaA==", "sessionLifetimeMinutes": 60 }
              },
              "clientsDirectory": "clients",
              "limits": {
                "maxConnections": 2000, "maxPendingConnections": 200, "maxUnauthenticatedConnections": 100,
                "maxChannelsPerClient": 100, "openTimeoutSeconds": 20, "blockedWriteTimeoutSeconds": 120,
                "halfCloseDrainTimeoutSeconds": 300, "tcpIdleTimeoutSeconds": 900
              },
              "automation": {
                "enabled": true, "listenAddress": "{{automationListenAddress}}", "port": 18082,
                "certificatePemPath": "automation.pem", "privateKeyPemPath": "automation.key"
              }
            }
            """);
        return path;
    }

    [Fact]
    public void Automation_port_must_not_conflict_with_server_endpoints()
    {
        var loader = new ConfigurationLoader();
        // tunnel 7443 / data 7444 / dashboard 18080
        foreach (var port in new[] { 7443, 7444, 18080 })
        {
            var server = ServerWith(new AutomationConfiguration(true, "127.0.0.1", port, "/tmp/a.pem", "/tmp/ak.pem"));
            var exception = Assert.Throws<ConfigurationException>(() =>
                loader.ValidateServerUpdate(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>())));
            Assert.Contains("conflicts", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Automation_rejects_out_of_range_limits()
    {
        var loader = new ConfigurationLoader();

        var shortLifetime = ServerWith(new AutomationConfiguration(true, "127.0.0.1", 18082, "/tmp/a.pem", "/tmp/ak.pem") { SessionLifetimeMinutes = 5 });
        Assert.Throws<ConfigurationException>(() => loader.ValidateServerUpdate(new LoadedConfiguration(shortLifetime, new Dictionary<string, ClientConfiguration>())));

        var badAuthorizations = ServerWith(new AutomationConfiguration(true, "127.0.0.1", 18082, "/tmp/a.pem", "/tmp/ak.pem") { MaxAuthorizations = 2, MaxPendingPairings = 8 });
        Assert.Throws<ConfigurationException>(() => loader.ValidateServerUpdate(new LoadedConfiguration(badAuthorizations, new Dictionary<string, ClientConfiguration>())));
    }

    /// <summary>
    /// A109 端口变更路径：通道原本指向一个普通回环端口，管理员把 automation.port
    /// 改成该端口时，ValidateServerUpdate 必须通过全量扫描拒绝，不能让已存在的
    /// 非授权互访通道借用新的自动化端口。
    /// </summary>
    [Fact]
    public void Automation_port_change_revalidates_existing_loopback_channels()
    {
        var loader = new ConfigurationLoader();
        var accessSecret = Convert.ToBase64String(new byte[32]);
        const int targetPort = 19000;
        // ValidateAutomation 检查的是「已解析」的证书路径，单测里直接给出非空值。
        AutomationConfiguration Automation(int port) => new(true, "127.0.0.1", port, "cert.pem", "key.pem")
        {
            ResolvedCertificatePath = "/tmp/automation.pem",
            ResolvedPrivateKeyPath = "/tmp/automation.key"
        };

        // 现有通道：指向 127.0.0.1:19000，非授权互访形态（会在服务端监听公网端口）。
        var publicChannel = new ChannelConfiguration("exposed", "exposed", true, "0.0.0.0", 21433, "127.0.0.1", targetPort, 10, 20)
        {
            AuthorizedClientsOnly = false
        };
        var client = new ClientConfiguration(1, "node-01", "node", true, accessSecret, 100, 20, [publicChannel]);

        // 变更前：automation.port=18082，与通道目标 19000 不冲突，配置合法。
        var before = ServerWith(Automation(18082));
        loader.ValidateServerUpdate(new LoadedConfiguration(before, new Dictionary<string, ClientConfiguration> { ["node-01"] = client }));

        // 变更后：把 automation.port 改成通道已指向的 19000，必须被拒绝。
        var after = ServerWith(Automation(targetPort));
        var exception = Assert.Throws<ConfigurationException>(() =>
            loader.ValidateServerUpdate(new LoadedConfiguration(after, new Dictionary<string, ClientConfiguration> { ["node-01"] = client })));
        Assert.Contains("not an authorized-peer channel", exception.Message);
        Assert.Contains("19000", exception.Message);
    }

    [Fact]
    public void Automation_enabled_requires_certificate_paths()
    {
        var loader = new ConfigurationLoader();
        var server = ServerWith(new AutomationConfiguration(true, "127.0.0.1", 18082, string.Empty, string.Empty));
        var exception = Assert.Throws<ConfigurationException>(() =>
            loader.ValidateServerUpdate(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>())));
        Assert.Contains("certificatePemPath", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Loopback_target_reaching_automation_port_must_be_authorized_peer()
    {
        var server = ServerWith(new AutomationConfiguration(true, "127.0.0.1", 18082, "/tmp/a.pem", "/tmp/ak.pem"));
        var accessSecret = Convert.ToBase64String(new byte[32]);

        // 非授权互访形态：会在服务端监听公网端口，必须拒绝。
        var publicChannel = new ChannelConfiguration("exposed", "exposed", true, "0.0.0.0", 21433, "127.0.0.1", 18082, 10, 20)
        {
            AuthorizedClientsOnly = false
        };

        var client = new ClientConfiguration(1, "node-01", "node", true, accessSecret, 100, 20, [publicChannel]);
        var exception = Assert.Throws<ConfigurationException>(() =>
            ConfigurationLoader.ValidateAutomationLoopbackTargets(server, new[] { client }, "test"));
        Assert.Contains("not an authorized-peer channel", exception.Message);
    }

    [Fact]
    public void Authorized_peer_target_reaching_automation_port_is_allowed()
    {
        var server = ServerWith(new AutomationConfiguration(true, "127.0.0.1", 18082, "/tmp/a.pem", "/tmp/ak.pem"));
        var accessSecret = Convert.ToBase64String(new byte[32]);

        var authorizedChannel = new ChannelConfiguration("auto", "auto", true, "127.0.0.1", 0, "127.0.0.1", 18082, 10, 20)
        {
            AuthorizedClientsOnly = true,
            EndToEndEncryptionEnabled = true,
            ApplicationProtocol = "raw",
            AccessSecret = accessSecret
        };

        var client = new ClientConfiguration(1, "node-01", "node", true, accessSecret, 100, 20, [authorizedChannel]);
        // 不应抛出：这正是接入向导生成的形态。
        ConfigurationLoader.ValidateAutomationLoopbackTargets(server, new[] { client }, "test");
    }

    [Fact]
    public void Loopback_target_with_encryption_disabled_is_rejected()
    {
        var server = ServerWith(new AutomationConfiguration(true, "127.0.0.1", 18082, "/tmp/a.pem", "/tmp/ak.pem"));
        var accessSecret = Convert.ToBase64String(new byte[32]);

        // 授权互访形态但关闭了内层 TLS：不得通向自动化端口。
        var channel = new ChannelConfiguration("auto", "auto", true, "127.0.0.1", 0, "127.0.0.1", 18082, 10, 20)
        {
            AuthorizedClientsOnly = true,
            EndToEndEncryptionEnabled = false,
            ApplicationProtocol = "raw",
            AccessSecret = accessSecret
        };

        var client = new ClientConfiguration(1, "node-01", "node", true, accessSecret, 100, 20, [channel]);
        Assert.Throws<ConfigurationException>(() =>
            ConfigurationLoader.ValidateAutomationLoopbackTargets(server, new[] { client }, "test"));
    }

    [Fact]
    public void Non_loopback_target_at_automation_port_is_not_restricted()
    {
        var server = ServerWith(new AutomationConfiguration(true, "127.0.0.1", 18082, "/tmp/a.pem", "/tmp/ak.pem"));
        var accessSecret = Convert.ToBase64String(new byte[32]);

        // 目标不是回环，即使端口巧合相同也不受限。
        var channel = new ChannelConfiguration("other", "other", true, "0.0.0.0", 21434, "10.1.2.3", 18082, 10, 20);
        var client = new ClientConfiguration(1, "node-01", "node", true, accessSecret, 100, 20, [channel]);
        ConfigurationLoader.ValidateAutomationLoopbackTargets(server, new[] { client }, "test");
    }
}

/// <summary>
/// 幂等回执缓存的三态与清理窗口边界。修复 D（原为死代码）的回归护栏：
/// 同键不同载荷 conflict、同键同载荷 replayed、失败 Release 后可重新登记。
/// </summary>
public sealed class IdempotencyCacheTests
{
    private static RelayLink.Server.IdempotencyCache CreateCache() => new();
    [Fact]
    public void Same_key_same_payload_is_reserved_once()
    {
        var cache = CreateCache();
        Assert.True(cache.TryReserve("k", "f1"));
        Assert.False(cache.TryReserve("k", "f1")); // 同键重放：不再登记
        Assert.Equal("f1", cache.GetFingerprint("k"));
    }

    [Fact]
    public void Same_key_different_payload_is_detected_as_conflict()
    {
        var cache = CreateCache();
        Assert.True(cache.TryReserve("k", "f1"));
        Assert.False(cache.TryReserve("k", "f2"));
        // conflict 的依据是首次指纹与本次不同。
        Assert.Equal("f1", cache.GetFingerprint("k"));
    }

    [Fact]
    public void Release_allows_retry_after_failure()
    {
        // 失败毒化回归：执行失败必须解除预留，否则同键重试命中 replayed 假成功。
        var cache = CreateCache();
        Assert.True(cache.TryReserve("k", "f1"));
        cache.Release("k");
        Assert.Null(cache.GetFingerprint("k"));
        Assert.True(cache.TryReserve("k", "f1"));
    }

    [Fact]
    public void Different_keys_are_independent()
    {
        var cache = CreateCache();
        Assert.True(cache.TryReserve("k1", "f1"));
        Assert.True(cache.TryReserve("k2", "f2"));
        Assert.Equal("f1", cache.GetFingerprint("k1"));
        Assert.Equal("f2", cache.GetFingerprint("k2"));
    }

    [Fact]
    public void Unknown_key_returns_null_fingerprint()
    {
        var cache = CreateCache();
        Assert.Null(cache.GetFingerprint("missing"));
    }
}

/// <summary>
/// A114 审计故障注入：审计存储不可用时写操作必须失败关闭。
/// 把审计库路径指向一个目录，SecretFilePermissions 会拒绝它，
/// AuditService.RecordAsync 抛 AuditUnavailableException；写前闸门
/// RequireAuditAsync 必须原样传播该异常（不得像 TryAuditAsync 那样吞掉）。
/// </summary>
public sealed class AuditUnavailableTests
{
    [Fact]
    public async Task Audit_service_throws_when_storage_path_is_a_directory()
    {
        using var directory = new TestDirectory("relaylink-audit-fail-");
        var auditPath = Path.Combine(directory.Path, "audit.db");
        Directory.CreateDirectory(auditPath); // 同名目录而非文件：审计存储不可用

        var server = ServerConfigurationForAudit(auditPath);
        var runtime = new ServerRuntime(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>()), new SessionRegistry());
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);

        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            audit.RecordAsync(new AuditEvent("automation_change_applied", "accepted")));
    }

    [Fact]
    public async Task RequireAuditAsync_propagates_audit_failure_instead_of_swallowing_it()
    {
        using var directory = new TestDirectory("relaylink-audit-fail-");
        var auditPath = Path.Combine(directory.Path, "audit.db");
        Directory.CreateDirectory(auditPath);

        var server = ServerConfigurationForAudit(auditPath);
        var runtime = new ServerRuntime(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>()), new SessionRegistry());
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        // store/pairing 在 RequireAuditAsync 路径上不会被触碰，用 null! 隔离依赖。
        var tokens = new AutomationTokenService(null!, null!, audit, NullLogger<AutomationTokenService>.Instance);
        var grant = new AutomationGrant(Guid.NewGuid(), "subject", "test", "thumb",
            ["clients.read"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, null);

        await Assert.ThrowsAsync<AuditUnavailableException>(() =>
            tokens.RequireAuditAsync(grant, "automation_change_applied", CancellationToken.None));
    }

    private static ServerConfiguration ServerConfigurationForAudit(string auditPath) => new(
        1,
        new TunnelConfiguration("0.0.0.0", 7443, false, string.Empty, string.Empty, 10, 15, 45),
        new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "PBKDF2-SHA256$210000$x$y", 60)),
        "/tmp/clients",
        new LimitsConfiguration(2000, 200, 100, 100, 20, 120, 300),
        null)
    {
        Audit = new AuditConfiguration(auditPath, 90)
    };
}
