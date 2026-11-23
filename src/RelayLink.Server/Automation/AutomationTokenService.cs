using RelayLink.Server.Runtime;

namespace RelayLink.Server.Automation;

/// <summary>
/// 令牌鉴权：先核对令牌摘要，再校验设备签名证明，最后检查 scope。
/// 校验顺序固定，任一失败即拒绝，不降级为无证明调用。
/// </summary>
public sealed class AutomationTokenService(
    AutomationStore store,
    AutomationPairingService pairing,
    AuditService audit,
    ILogger<AutomationTokenService> logger)
{
    public sealed record Authorized(AutomationGrant Grant, string TokenHash, Guid InstanceId);

    public sealed record Denial(string Code, string Message, Authorized? Authorized);

    public async Task<Denial> AuthorizeAsync(HttpRequest request, string? requiredScope, CancellationToken cancellationToken)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new Denial(AutomationErrors.Unauthenticated, "缺少 Authorization: Bearer 令牌。", null);

        var token = header["Bearer ".Length..].Trim();
        if (token.Length is 0 or > 512)
            return new Denial(AutomationErrors.Unauthenticated, "令牌格式无效。", null);

        var tokenHash = AutomationPairingService.HashToken(token);
        var grant = await store.FindGrantByTokenHashAsync(tokenHash, cancellationToken);
        if (grant is null)
            return new Denial(AutomationErrors.Unauthenticated, "令牌不存在。", null);

        if (grant.RevokedAtUtc is not null)
            return new Denial(AutomationErrors.TokenRevoked, "该令牌已被撤销。", null);

        var now = DateTimeOffset.UtcNow;
        if (grant.ExpiresAtUtc <= now)
            return new Denial(AutomationErrors.TokenExpired, "该令牌已过期，请重新配对。", null);

        var instanceId = await store.GetOrCreateInstanceIdAsync(cancellationToken);

        // 设备签名证明：绑定设备公钥，复制令牌到另一台机器无法通过。
        // 公钥必须从持久存储读取；放进程内字典会在重启后丢失，导致所有未过期令牌被拒。
        var publicKeyJwk = await store.GetDevicePublicKeyAsync(tokenHash, cancellationToken);
        using var deviceKey = publicKeyJwk is null ? null : DeviceProof.ImportPublicKey(publicKeyJwk);
        if (deviceKey is null)
            return new Denial(AutomationErrors.StorageUnavailable, "无法加载该令牌绑定的设备公钥。", null);

        var proofHeader = request.Headers[DeviceProof.HeaderName].ToString();
        if (!DeviceProof.TryParse(proofHeader, deviceKey, now, grant.DeviceThumbprint, out var proof, out var error))
            return new Denial(error!.Code, error.Message, null);

        if (!DeviceProof.UriMatches(proof!, request.Method, request.Path.Value ?? "/", request.QueryString.Value?.TrimStart('?')))
            return new Denial(AutomationErrors.ProofInvalid, "证明中的 htu/htm 与本次请求不一致。", null);

        if (!string.Equals(proof!.TokenHash, tokenHash, StringComparison.Ordinal))
            return new Denial(AutomationErrors.ProofInvalid, "证明中的 ath 与所携带的令牌不一致。", null);

        if (string.IsNullOrEmpty(proof!.InstanceId))
            return new Denial(AutomationErrors.ProofInvalid,
                "证明缺少 instanceId；请以 /info 返回的 automationInstanceId 填充。", null);

        if (!string.Equals(proof.InstanceId, instanceId.ToString(), StringComparison.OrdinalIgnoreCase))
            return new Denial(AutomationErrors.ProofInvalid, "证明中的 instanceId 与本服务端不一致。", null);

        if (!pairing.AcceptJti(proof.Jti!))
            return new Denial(AutomationErrors.ProofReplayed, "该证明已使用过。", null);

        if (requiredScope is not null && !AutomationScopes.Satisfies(grant.Scopes, requiredScope))
        {
            await TryAuditAsync("automation_request", "denied", grant, null, AutomationErrors.ScopeInsufficient, cancellationToken);
            return new Denial(AutomationErrors.ScopeInsufficient, $"令牌不具备 {requiredScope} 权限。", null);
        }

        return new Denial(string.Empty, string.Empty, new Authorized(grant, tokenHash, instanceId));
    }

    public async Task TryAuditAsync(string eventType, string outcome, AutomationGrant? grant, Guid? changeRequestId, string? reasonCode, CancellationToken cancellationToken)
    {
        try
        {
            await audit.RecordAsync(new AuditEvent(eventType, outcome)
            {
                Actor = grant is null ? "automation" : $"automation:{grant.Subject}",
                AutomationGrantId = grant?.GrantId,
                DeviceKeyThumbprint = grant?.DeviceThumbprint,
                ReasonCode = reasonCode
            }, cancellationToken);
        }
        catch (AuditUnavailableException)
        {
            logger.LogError("Audit unavailable for automation {EventType} {Outcome}.", eventType, outcome);
        }
    }

    /// <summary>
    /// 写操作前置审计闸门：审计存储不可用时抛出 AuditUnavailableException，
    /// 由端点转成 audit_unavailable 且不执行任何配置变更（需求 §「审计存储不可用」）。
    /// 只读路径继续用 TryAuditAsync 放行并告警，这里只服务于写与 C 类申请。
    /// </summary>
    public async Task RequireAuditAsync(AutomationGrant grant, string eventType, CancellationToken cancellationToken)
    {
        await audit.RecordAsync(new AuditEvent(eventType, "accepted")
        {
            Actor = $"automation:{grant.Subject}",
            AutomationGrantId = grant.GrantId,
            DeviceKeyThumbprint = grant.DeviceThumbprint
        }, cancellationToken);
    }
}
