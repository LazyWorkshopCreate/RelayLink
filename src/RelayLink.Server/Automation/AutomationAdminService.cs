using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.Server.Automation;

/// <summary>
/// 管理页面侧的自动化授权管理：列出待批准请求、批准/拒绝、列出与撤销授权。
/// 批准时只能缩小 scope 与有效期，不能扩大；申请中未列出的 scope 不得获批。
/// </summary>
public sealed class AutomationAdminService(
    AutomationStore store,
    ServerRuntime runtime,
    AuditService audit,
    ILogger<AutomationAdminService> logger)
{
    public async Task<IReadOnlyList<PairingRequest>> ListPairingsAsync(PairingState? state, CancellationToken cancellationToken) =>
        await store.ListPairingsAsync(state, 200, cancellationToken);

    public async Task<IReadOnlyList<AutomationGrant>> ListGrantsAsync(CancellationToken cancellationToken) =>
        await store.ListGrantsAsync(cancellationToken);

    public sealed record Decision(bool Ok, string? ErrorCode, string? Message);

    public async Task<Decision> ApproveAsync(Guid requestId, string[]? approvedScopes, int? lifetimeMinutes, string admin, CancellationToken cancellationToken)
    {
        var request = await store.GetPairingAsync(requestId, cancellationToken);
        if (request is null) return new Decision(false, AutomationErrors.PairingNotFound, "配对请求不存在。");
        if (request.State != PairingState.Pending) return new Decision(false, AutomationErrors.Conflict, "该请求已处理。");
        if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow) return new Decision(false, AutomationErrors.PairingExpired, "该请求已过期。");

        var automationConfig = runtime.Configuration.Server.Automation;
        var maxAuthorizations = automationConfig?.MaxAuthorizations ?? AutomationConfiguration.DefaultAuthorizations;
        if (await store.CountActiveGrantsAsync(cancellationToken) >= maxAuthorizations)
            return new Decision(false, AutomationErrors.PendingCapacityExceeded, "有效授权已达上限，请先撤销不再使用的授权。");

        string[] scopes;
        if (approvedScopes is { Length: > 0 })
        {
            if (!AutomationScopes.TryNormalize(approvedScopes, out scopes, out var unknown))
                return new Decision(false, AutomationErrors.ValidationFailed, $"包含未知 scope：{string.Join(", ", unknown)}。");
        }
        else
        {
            scopes = request.Scopes;
        }
        if (scopes.Length == 0) return new Decision(false, AutomationErrors.ValidationFailed, "批准时必须保留至少一个 scope。");
        // 只允许缩小：批准的每一项都必须由申请 scope 覆盖。
        if (!AutomationScopes.IsSubsetOf(scopes, request.Scopes))
            return new Decision(false, AutomationErrors.ValidationFailed, "批准不能包含申请中未列出的 scope。");

        var configuredLifetime = automationConfig?.SessionLifetimeMinutes ?? AutomationConfiguration.DefaultSessionLifetimeMinutes;
        var lifetime = lifetimeMinutes ?? configuredLifetime;
        if (lifetime < 15 || lifetime > configuredLifetime)
            return new Decision(false, AutomationErrors.ValidationFailed, $"有效期必须在 15–{configuredLifetime} 分钟之间。");

        var ok = await store.ApprovePairingAsync(requestId, scopes, lifetime, admin, DateTimeOffset.UtcNow, cancellationToken);
        if (!ok) return new Decision(false, AutomationErrors.Conflict, "该请求已处理。");

        await TryAuditAsync("automation_pairing_approved", "accepted", requestId, admin, request.DeviceThumbprint, cancellationToken);
        return new Decision(true, null, null);
    }

    public async Task<Decision> DenyAsync(Guid requestId, string admin, CancellationToken cancellationToken)
    {
        var request = await store.GetPairingAsync(requestId, cancellationToken);
        if (request is null) return new Decision(false, AutomationErrors.PairingNotFound, "配对请求不存在。");
        var ok = await store.DenyPairingAsync(requestId, admin, DateTimeOffset.UtcNow, cancellationToken);
        if (!ok) return new Decision(false, AutomationErrors.Conflict, "该请求已处理。");
        await TryAuditAsync("automation_pairing_denied", "denied", requestId, admin, request.DeviceThumbprint, cancellationToken);
        return new Decision(true, null, null);
    }

    /// <summary>撤销先落库再确认，避免「提示成功但重启后复活」。</summary>
    public async Task<Decision> RevokeAsync(Guid grantId, string admin, CancellationToken cancellationToken)
    {
        var ok = await store.RevokeGrantAsync(grantId, admin, DateTimeOffset.UtcNow, cancellationToken);
        if (!ok) return new Decision(false, AutomationErrors.NotFound, "授权不存在或已撤销。");
        try
        {
            await audit.RecordAsync(new AuditEvent("automation_token_revoked", "accepted") { Actor = admin, AutomationGrantId = grantId }, cancellationToken);
        }
        catch (AuditUnavailableException)
        {
            // 撤销必须生效；审计失败只影响回执，不能让撤销回滚。
            logger.LogError("Audit unavailable while revoking automation grant {GrantId}.", grantId);
        }

        return new Decision(true, null, null);
    }

    private async Task TryAuditAsync(string eventType, string outcome, Guid requestId, string admin, string thumbprint, CancellationToken cancellationToken)
    {
        try
        {
            await audit.RecordAsync(new AuditEvent(eventType, outcome)
            {
                Actor = admin,
                DeviceKeyThumbprint = thumbprint,
                ReasonCode = requestId.ToString()
            }, cancellationToken);
        }
        catch (AuditUnavailableException)
        {
            logger.LogError("Audit unavailable for automation {EventType} {RequestId}.", eventType, requestId);
        }
    }
}
