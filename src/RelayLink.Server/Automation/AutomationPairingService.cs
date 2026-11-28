using System.Collections.Concurrent;
using System.Security.Cryptography;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.Server.Automation;

/// <summary>
/// 一次性配对与设备绑定令牌的编排层。
/// 安全模型：用户码不单独构成凭据；换令牌必须由设备私钥签名，且签名内容含用户码摘要；
/// 令牌绑定设备公钥，复制到另一台机器无效。
/// </summary>
public sealed class AutomationPairingService(
    AutomationStore store,
    LoadedConfiguration configuration,
    AuditService audit,
    ILogger<AutomationPairingService> logger)
{
    public const int UserCodeLifetimeMinutes = 10;
    public const int UserCodeLockAfter = 5;
    // 限流按设备指纹 + 主体名，不用来源地址：经互访到达的来源恒为 Agent 的 loopback 临时端口。
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private const int RateLimitPerWindow = 10;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> rateBuckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> tokenRateBuckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> replayCache = new(StringComparer.Ordinal);
    private long lastReplaySweepTick = Environment.TickCount64;

    // jti 重放缓存保留期必须覆盖证明的最大可重放寿命，三段之和：
    // iat 允许超前 60s + exp-iat 上限 60s + 过期容忍 skew 60s = 180s。
    // 少算任何一段都会出现「缓存已淘汰、时间窗仍接受」的重放窗口（上一轮按 120s
    // 计算漏了 iat 超前，150s 仍留 30s 窗口，本轮补齐）。
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromSeconds(180);
    private const int MaxReplayEntries = 65_536;
    private const int TokenRateLimitPerWindow = 120;

    private AutomationConfiguration? Configuration => configuration.Server.Automation;

    public sealed record PairingCreation(string RequestId, string UserCode, DateTimeOffset ExpiresAtUtc, Guid InstanceId);

    public sealed record PairingOutcome(bool Ok, string? ErrorCode, string? Message, PairingRequest? Request, string? Token);

    // 按主体分桶的键数上限。攻击者可用任意假 subject+假公钥制造大量键，
    // 无上限会带来无界内存增长；超限时先整体清扫过期键，仍超则拒绝新键
    // （已有键的合法调用不受影响）。
    private const int MaxRateBuckets = 4096;

    public bool IsRateLimited(string key) => IsRateLimitedCore(rateBuckets, key, RateLimitPerWindow);

    public bool IsTokenRateLimited(string grantKey) => IsRateLimitedCore(tokenRateBuckets, grantKey, TokenRateLimitPerWindow);

    private bool IsRateLimitedCore(ConcurrentDictionary<string, Queue<DateTimeOffset>> buckets, string key, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        if (!buckets.ContainsKey(key) && buckets.Count >= MaxRateBuckets)
        {
            SweepExpiredBuckets(buckets, now);
            if (buckets.Count >= MaxRateBuckets) return true;
        }

        var bucket = buckets.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (bucket)
        {
            while (bucket.Count > 0 && now - bucket.Peek() > RateWindow) bucket.Dequeue();
            if (bucket.Count >= limit) return true;
            bucket.Enqueue(now);
            return false;
        }
    }

    private static void SweepExpiredBuckets(ConcurrentDictionary<string, Queue<DateTimeOffset>> buckets, DateTimeOffset now)
    {
        foreach (var key in buckets.Keys.ToArray())
        {
            if (!buckets.TryGetValue(key, out var bucket)) continue;
            lock (bucket)
            {
                var hasLive = false;
                foreach (var stamp in bucket)
                {
                    if (now - stamp <= RateWindow) { hasLive = true; break; }
                }
                if (!hasLive) buckets.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// 匿名端点（/info 与配对请求）共享的进程级全局上限。
    /// 按主体分桶的限流可被换钥绕过，这里是同主机进程刷不穿的兜底。
    /// </summary>
    public bool IsAnonymousRateLimited() => IsRateLimited("__anonymous_global__");

    /// <summary>jti 重放缓存。返回 false 表示该 jti 已出现过或缓存已满。</summary>
    public bool AcceptJti(string jti)
    {
        var now = DateTimeOffset.UtcNow;
        var nowTick = Environment.TickCount64;
        var lastSweep = Volatile.Read(ref lastReplaySweepTick);
        if (replayCache.Count >= MaxReplayEntries || (nowTick - lastSweep >= 30_000 && Interlocked.CompareExchange(ref lastReplaySweepTick, nowTick, lastSweep) == lastSweep))
        {
            Volatile.Write(ref lastReplaySweepTick, nowTick);
            foreach (var entry in replayCache.Where(pair => now - pair.Value > ReplayRetention).Select(pair => pair.Key).ToArray())
                replayCache.TryRemove(entry, out _);
        }
        if (replayCache.Count >= MaxReplayEntries)
            return false;
        return replayCache.TryAdd(jti, now);
    }

    public static string HashToken(string token) => DeviceProof.HashToken(System.Text.Encoding.UTF8.GetBytes(token));

    public async Task<(PairingCreation? Created, string? ErrorCode, string? Message)> CreatePairingAsync(
        string subject, string platform, string cliVersion, string[] scopes, string devicePublicKeyJwk,
        CancellationToken cancellationToken)
    {
        if (Configuration is null || !Configuration.Enabled)
            return (null, "automation_disabled", "自动化入口未启用。");

        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 128)
            return (null, "validation_failed", "主体名必须为 1–128 字符。");
        if (scopes.Length == 0)
            return (null, "validation_failed", "至少申请一个 scope。");

        using var deviceKey = DeviceProof.ImportPublicKey(devicePublicKeyJwk);
        if (deviceKey is null)
            return (null, "validation_failed", "devicePublicKey 必须是 P-256 EC 公钥的 JWK。");
        var thumbprint = DeviceProof.ThumbprintOf(deviceKey);

        var instanceId = await store.GetOrCreateInstanceIdAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var code = UserCode.Generate();
        var request = new PairingRequest(Guid.NewGuid(), subject.Trim(), platform ?? "unknown", cliVersion ?? "unknown",
            scopes, thumbprint, devicePublicKeyJwk, UserCode.Digest(code), UserCode.Format(code), PairingState.Pending,
            0, now, now.AddMinutes(UserCodeLifetimeMinutes), null, null, null, null, null, null, null, null);
        if (!await store.TryCreatePairingAsync(request, Configuration.MaxPendingPairings, Configuration.MaxAuthorizations, cancellationToken))
        {
            logger.LogWarning("Automation pairing rejected: pending or authorization capacity reached (pending limit {PendingLimit}, grant limit {GrantLimit}).",
                Configuration.MaxPendingPairings, Configuration.MaxAuthorizations);
            return (null, "pending_capacity_exceeded", "待批准请求或有效授权已达上限，请管理员先处理。");
        }
        await RecordAsync("automation_pairing_requested", "accepted", thumbprint, request.RequestId, subject, cancellationToken);

        return (new PairingCreation(request.RequestId.ToString(), code, request.ExpiresAtUtc, instanceId), null, null);
    }

    /// <summary>查询配对状态。需要设备签名证明，且证明必须携带正确用户码摘要；错码计入锁定。</summary>
    public async Task<PairingOutcome> QueryStatusAsync(Guid requestId, DeviceProof.Proof proof, string? submittedUserCode, CancellationToken cancellationToken)
    {
        var request = await store.GetPairingAsync(requestId, cancellationToken);
        if (request is null) return new PairingOutcome(false, "pairing_not_found", "配对请求不存在。", null, null);
        if (!AcceptJti(proof.Jti!)) return new PairingOutcome(false, "proof_replayed", "该证明已使用过。", null, null);

        var effective = await EffectiveStateAsync(request, cancellationToken);
        if (effective is PairingState.Completed)
            return new PairingOutcome(false, "pairing_already_completed", "该请求已换取过令牌。", request, null);
        if (effective is PairingState.Denied)
            return new PairingOutcome(false, "pairing_denied", "配对请求已被拒绝，请重新发起。", request, null);
        if (effective is PairingState.Locked)
            return new PairingOutcome(false, "pairing_locked", "用户码错误次数过多，该请求已作废。", request, null);
        if (effective is PairingState.Expired)
            return new PairingOutcome(false, "pairing_expired", "配对请求已过期，请重新发起。", request, null);

        var normalized = UserCode.Normalize(submittedUserCode);
        if (!UserCode.FixedTimeEquals(UserCode.Digest(normalized), request.UserCodeDigest) ||
            !UserCode.FixedTimeEquals(proof.UserCodeDigest, request.UserCodeDigest))
        {
            var (attempts, lockedState) = await store.RegisterUserCodeFailureAsync(requestId, UserCodeLockAfter, cancellationToken);
            await RecordAsync(lockedState == PairingState.Locked ? "automation_pairing_locked" : "automation_pairing_denied",
                "failed", request.DeviceThumbprint, requestId, request.Subject, cancellationToken);
            return lockedState == PairingState.Locked
                ? new PairingOutcome(false, "pairing_locked", "用户码错误次数过多，该请求已作废。", request, null)
                : new PairingOutcome(false, "pairing_denied", $"用户码不正确（第 {attempts} 次）。", request, null);
        }

        return new PairingOutcome(true, null, null, request, null);
    }

    /// <summary>
    /// 校验用户码并换取令牌。必须在单事务内完成「查状态 → 作废 → 签发」，
    /// 否则两个不同 jti 的并发证明会各自签发一枚令牌。
    /// </summary>
    public async Task<PairingOutcome> ExchangeTokenAsync(Guid requestId, DeviceProof.Proof proof, string? submittedUserCode, CancellationToken cancellationToken)
    {
        var request = await store.GetPairingAsync(requestId, cancellationToken);
        if (request is null) return new PairingOutcome(false, "pairing_not_found", "配对请求不存在。", null, null);
        if (!AcceptJti(proof.Jti!)) return new PairingOutcome(false, "proof_replayed", "该证明已使用过。", null, null);

        var effective = await EffectiveStateAsync(request, cancellationToken);
        if (effective is PairingState.Completed)
            return new PairingOutcome(false, "pairing_already_completed", "该请求已换取过令牌。", request, null);
        if (effective is PairingState.Denied)
            return new PairingOutcome(false, "pairing_denied", "配对请求已被拒绝。", request, null);
        if (effective is PairingState.Locked)
            return new PairingOutcome(false, "pairing_locked", "用户码错误次数过多，该请求已作废。", request, null);
        if (effective is PairingState.Expired)
            return new PairingOutcome(false, "pairing_expired", "配对请求已过期，请重新发起。", request, null);
        if (effective is PairingState.Pending)
            return new PairingOutcome(false, "change_request_not_approved", "配对请求尚未获得管理员批准。", request, null);

        // 用户码必须与请求中保存的摘要一致。用户码错误计入锁定计数。
        var normalized = UserCode.Normalize(submittedUserCode);
        var digest = UserCode.Digest(normalized);
        if (!UserCode.FixedTimeEquals(digest, request.UserCodeDigest) ||
            !UserCode.FixedTimeEquals(proof.UserCodeDigest, request.UserCodeDigest))
        {
            var (attempts, state) = await store.RegisterUserCodeFailureAsync(requestId, UserCodeLockAfter, cancellationToken);
            await RecordAsync(state == PairingState.Locked ? "automation_pairing_locked" : "automation_pairing_denied",
                "failed", request.DeviceThumbprint, requestId, request.Subject, cancellationToken);
            return state == PairingState.Locked
                ? new PairingOutcome(false, "pairing_locked", "用户码错误次数过多，该请求已作废。", request, null)
                : new PairingOutcome(false, "pairing_denied", $"用户码不正确（第 {attempts} 次）。", request, null);
        }

        // 生成一次性令牌，只把 SHA-256 摘要落库。
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = DeviceProof.Base64UrlEncode(tokenBytes);
        var tokenHash = HashToken(token);
        var grant = await store.CompletePairingAsync(requestId, tokenHash, DateTimeOffset.UtcNow, null, cancellationToken,
            Configuration?.MaxAuthorizations ?? AutomationConfiguration.DefaultAuthorizations);
        if (grant is null)
            return await store.CountActiveGrantsAsync(cancellationToken) >= (Configuration?.MaxAuthorizations ?? AutomationConfiguration.DefaultAuthorizations)
                ? new PairingOutcome(false, AutomationErrors.PendingCapacityExceeded, "有效授权已达上限，请管理员先撤销旧授权。", request, null)
                : new PairingOutcome(false, "pairing_already_completed", "该请求已换取过令牌。", request, null);

        await RecordAsync("automation_token_issued", "accepted", request.DeviceThumbprint, requestId, request.Subject, cancellationToken);
        return new PairingOutcome(true, null, null, request, token);
    }

    private static async Task<PairingState> EffectiveStateAsync(PairingRequest request, CancellationToken cancellationToken) =>
        request.State is PairingState.Pending or PairingState.Approved && request.ExpiresAtUtc <= DateTimeOffset.UtcNow
            ? PairingState.Expired
            : await Task.FromResult(request.State);

    private async Task RecordAsync(string eventType, string outcome, string thumbprint, Guid requestId, string subject, CancellationToken cancellationToken)
    {
        try
        {
            await audit.RecordAsync(new AuditEvent(eventType, outcome)
            {
                Actor = $"automation:{subject}",
                DeviceKeyThumbprint = thumbprint,
                ReasonCode = requestId.ToString()
            }, cancellationToken);
        }
        catch (AuditUnavailableException)
        {
            // 配对不是「建立业务连接」，审计不可用不阻止配对，但必须留下诊断。
            logger.LogError("Audit storage unavailable while recording {EventType} for automation request {RequestId}.", eventType, requestId);
        }
    }
}
