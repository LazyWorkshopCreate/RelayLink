using System.Text.Json;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.Server.Automation;

/// <summary>
/// C 类结构性变更申请。只能生成申请，由管理员在管理页面批准后执行一次。
/// 执行分两段并带 applying 中间态：配置写入与状态落库是两个存储、无跨库事务，
/// 因此必须靠 applying 暴露「执行中」，并在启动时按 (type,target) 对账恢复。
/// </summary>
public sealed class AutomationChangeService(
    AutomationStore store,
    ChannelConfigurationEditor channelEditor,
    ClientConfigurationEditor clientEditor,
    ServerRuntime runtime,
    ConfigurationWriteLock writeLock,
    AuditService audit,
    ILogger<AutomationChangeService> logger)
{
    public static readonly IReadOnlyList<string> SupportedTypes =
    ["client.create", "client.delete", "channel.create", "channel.delete", "mapping.create", "channel.security"];

    public const int ExpiryHours = 24;

    public sealed record Submission(bool Ok, string? ErrorCode, string? Message, ChangeRequest? Request);
    public sealed record Execution(bool Ok, string? ErrorCode, string? Message, ChangeState State);

    public async Task<Submission> SubmitAsync(string type, JsonElement target, JsonElement payload, string? reason,
        string requestedBy, string idempotencyKey, CancellationToken cancellationToken,
        string? deviceThumbprint = null, Guid? grantId = null)
    {
        if (!SupportedTypes.Contains(type, StringComparer.Ordinal))
            return new Submission(false, AutomationErrors.ValidationFailed, $"不支持的变更类型：{type}。", null);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || !Guid.TryParse(idempotencyKey, out _))
            return new Submission(false, AutomationErrors.ValidationFailed, "Idempotency-Key 必须是非空 UUID。", null);

        var targetKey = BuildTargetKey(type, target);
        if (targetKey is null)
            return new Submission(false, AutomationErrors.ValidationFailed, "target 字段不完整。", null);

        if (type == "credential.delete")
            return new Submission(false, AutomationErrors.RequiresChangeRequest,
                "凭据删除请在管理页面执行；自动化接口不接受凭据变更申请。", null);

        var validation = ValidatePayload(type, target, payload);
        if (validation is not null)
            return new Submission(false, AutomationErrors.ValidationFailed, validation, null);

        var selfProtected = SelfProtectionConflict(type, targetKey);
        if (selfProtected is not null)
            return new Submission(false, AutomationErrors.Conflict, selfProtected, null);

        var canonicalPayload = JsonSerializer.Serialize(payload, AutomationJson.Options);
        var now = DateTimeOffset.UtcNow;
        var request = new ChangeRequest(Guid.NewGuid(), type, targetKey, canonicalPayload, reason, requestedBy,
            ChangeState.Pending, now, now.AddHours(ExpiryHours), null, null, null, idempotencyKey)
        {
            DeviceThumbprint = deviceThumbprint
        };

        var (created, existingIdempotent, existingOpen) = await store.TrySubmitChangeAsync(request, cancellationToken);
        if (!created)
        {
            if (existingIdempotent is not null)
            {
                if (existingIdempotent.Type != type ||
                    existingIdempotent.TargetKey != targetKey ||
                    !string.Equals(NormalizeJson(existingIdempotent.PayloadJson), canonicalPayload, StringComparison.Ordinal) ||
                    !string.Equals(existingIdempotent.DeviceThumbprint, deviceThumbprint, StringComparison.Ordinal) ||
                    !string.Equals(existingIdempotent.RequestedBy, requestedBy, StringComparison.Ordinal))
                    return new Submission(false, AutomationErrors.Conflict, "该 Idempotency-Key 已用于不同的变更或主体。", existingIdempotent);
                return new Submission(true, null, null, existingIdempotent);
            }
            if (existingOpen is not null)
                return new Submission(false, AutomationErrors.Conflict,
                    $"该目标已有未完成的申请（{existingOpen.ChangeRequestId}），请先处理。", existingOpen);
        }

        await TryAuditAsync("automation_change_requested", "accepted", request.ChangeRequestId, requestedBy, cancellationToken, grantId, deviceThumbprint);
        return new Submission(true, null, null, request);
    }

    /// <summary>
    /// 自动化接入通道/映射的自保护：删除承载自身的通道或映射、或改其加密开关，
    /// 都会让 CLI 当场断开。B 类 PATCH 已有同款保护（AutomationEndpoints.IsAutomationChannel），
    /// C 类提交路径在此对齐。
    /// </summary>
    private string? SelfProtectionConflict(string type, string targetKey)
    {
        if (runtime.Configuration.Server.Automation is not { } automation) return null;
        var parts = targetKey.Split('/');
        var clientId = parts[0];
        var second = parts.Length > 1 ? parts[1] : null;
        if (!runtime.Configuration.Clients.TryGetValue(clientId, out var client)) return null;

        var isAutomationChannel = (ChannelConfiguration channel) =>
            channel.TargetPort == automation.Port &&
            ConfigurationLoader.IsLoopbackOrUnspecifiedHost(channel.TargetHost);

        switch (type)
        {
            case "client.delete":
                return client.Channels.Any(isAutomationChannel)
                    ? $"客户端 {clientId} 承载当前自动化接入通道，禁止经自动化删除；请改走管理页面。"
                    : null;
            case "channel.delete" when second is not null:
                var channel = client.Channels.SingleOrDefault(item => item.ChannelId == second);
                return channel is not null && isAutomationChannel(channel)
                    ? $"通道 {targetKey} 是当前自动化接入通道，禁止经自动化删除；请改走管理页面。"
                    : null;
            case "channel.security" when second is not null:
                var secured = client.Channels.SingleOrDefault(item => item.ChannelId == second);
                return secured is not null && isAutomationChannel(secured)
                    ? $"通道 {targetKey} 是当前自动化接入通道，禁止经自动化修改加密开关；请改走管理页面。"
                    : null;
            case "mapping.create" when second is not null:
                // 映射指向自动化通道属正常使用（CLI 自己就靠它），不拦。
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 提交时的校验：复用既有编辑器的校验语义，但不落盘。
    /// 真正的写入只在管理员批准后的执行阶段发生。
    /// </summary>
    private static string? ValidatePayload(string type, JsonElement target, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "payload 必须是 JSON 对象。";
        foreach (var property in payload.EnumerateObject())
        {
            // 明确拒绝任何形似秘密的字段：凭据写入永不经过自动化接口。
            if (property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("privateKey", StringComparison.OrdinalIgnoreCase))
                return $"payload 不接受秘密字段：{property.Name}。";
        }

        var targetClientId = GetString(target, "clientId");
        var targetChannelId = GetString(target, "channelId");
        var targetMappingId = GetString(target, "mappingId");

        switch (type)
        {
            case "client.create" or "client.delete":
            {
                var payloadClientId = GetString(payload, "clientId");
                if (string.IsNullOrWhiteSpace(payloadClientId))
                    return $"{type} 需要 clientId。";
                if (!string.Equals(payloadClientId, targetClientId, StringComparison.Ordinal))
                    return "payload.clientId 必须与 target.clientId 一致。";
                return null;
            }
            case "channel.create" or "channel.delete":
            {
                var payloadChannelId = GetString(payload, "channelId");
                if (string.IsNullOrWhiteSpace(payloadChannelId))
                    return $"{type} 需要 channelId。";
                if (!string.Equals(payloadChannelId, targetChannelId, StringComparison.Ordinal))
                    return "payload.channelId 必须与 target.channelId 一致。";
                var payloadClientId = GetString(payload, "clientId");
                if (payloadClientId is not null && !string.Equals(payloadClientId, targetClientId, StringComparison.Ordinal))
                    return "payload.clientId 与 target.clientId 不一致。";
                return null;
            }
            case "channel.security":
            {
                var payloadChannelId = GetString(payload, "channelId");
                if (payloadChannelId is not null && !string.Equals(payloadChannelId, targetChannelId, StringComparison.Ordinal))
                    return "payload.channelId 与 target.channelId 不一致。";
                if (GetBool(payload, "endToEndEncryptionEnabled") is null)
                    return "channel.security 只支持布尔字段 endToEndEncryptionEnabled。";
                return null;
            }
            case "mapping.create":
            {
                var remoteClient = GetString(payload, "targetClientId");
                var remoteChannel = GetString(payload, "targetChannelId");
                if (string.IsNullOrWhiteSpace(remoteClient) || string.IsNullOrWhiteSpace(remoteChannel))
                    return "mapping.create 需要非空字符串 targetClientId 与 targetChannelId。";
                if (!string.Equals(targetMappingId, $"{remoteClient}-{remoteChannel}", StringComparison.Ordinal))
                    return "target.mappingId 必须等于 <targetClientId>-<targetChannelId>。";
                return null;
            }
            default:
                return null;
        }
    }

    private static string? BuildTargetKey(string type, JsonElement target)
    {
        if (target.ValueKind != JsonValueKind.Object) return null;
        var clientId = GetString(target, "clientId");
        var channelId = GetString(target, "channelId");
        var mappingId = GetString(target, "mappingId");
        return type switch
        {
            "client.create" or "client.delete" => string.IsNullOrWhiteSpace(clientId) ? null : clientId,
            "channel.create" or "channel.delete" or "channel.security" =>
                string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(channelId) ? null : $"{clientId}/{channelId}",
            "mapping.create" =>
                string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(mappingId) ? null : $"{clientId}/{mappingId}",
            "credential.delete" =>
                string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(channelId) ? null : $"{clientId}/{channelId}",
            _ => null
        };
    }

    public async Task<IReadOnlyList<ChangeRequest>> ListAsync(ChangeState? state, CancellationToken cancellationToken, string? deviceThumbprint = null) =>
        await store.ListChangesAsync(state, 200, cancellationToken, deviceThumbprint);

    public async Task<ChangeRequest?> GetAsync(Guid changeRequestId, CancellationToken cancellationToken) =>
        await store.GetChangeAsync(changeRequestId, cancellationToken);

    /// <summary>
    /// 管理员批准：pending → approved，并写审计。只改状态，不执行。
    /// </summary>
    public async Task<Execution> ApproveAsync(Guid changeRequestId, string admin, CancellationToken cancellationToken)
    {
        var existing = await store.GetChangeAsync(changeRequestId, cancellationToken);
        if (existing is null) return new Execution(false, AutomationErrors.NotFound, "申请不存在。", ChangeState.Pending);
        if (existing.State == ChangeState.Expired || existing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return new Execution(false, AutomationErrors.Conflict, "该申请已超过 24 小时有效期，已自动过期。", ChangeState.Expired);
        var changed = await store.TransitionChangeAsync(changeRequestId, ChangeState.Approved, [ChangeState.Pending], admin, null,
            DateTimeOffset.UtcNow, cancellationToken);
        if (!changed) return new Execution(false, AutomationErrors.Conflict, "该申请不在待批准状态。", existing.State);
        await TryAuditAsync("automation_change_approved", "accepted", changeRequestId, admin, cancellationToken, deviceThumbprint: existing.DeviceThumbprint);
        return new Execution(true, null, null, ChangeState.Approved);
    }

    public async Task<Execution> RejectAsync(Guid changeRequestId, string admin, string? reason, CancellationToken cancellationToken)
    {
        var existing = await store.GetChangeAsync(changeRequestId, cancellationToken);
        if (existing is null) return new Execution(false, AutomationErrors.NotFound, "申请不存在。", ChangeState.Pending);
        var changed = await store.TransitionChangeAsync(changeRequestId, ChangeState.Rejected,
            [ChangeState.Pending, ChangeState.Approved], admin, reason, DateTimeOffset.UtcNow, cancellationToken, requireUnexpired: false);
        if (!changed) return new Execution(false, AutomationErrors.Conflict, "该申请已终结。", existing.State);
        await TryAuditAsync("automation_change_rejected", "denied", changeRequestId, admin, cancellationToken, deviceThumbprint: existing.DeviceThumbprint);
        return new Execution(true, null, null, ChangeState.Rejected);
    }

    public async Task<Execution> CancelAsync(Guid changeRequestId, string actor, CancellationToken cancellationToken,
        string? requiredDeviceThumbprint = null, Guid? grantId = null)
    {
        var existing = await store.GetChangeAsync(changeRequestId, cancellationToken);
        if (existing is null || (requiredDeviceThumbprint is not null && !string.Equals(existing.DeviceThumbprint, requiredDeviceThumbprint, StringComparison.Ordinal)))
            return new Execution(false, AutomationErrors.NotFound, "申请不存在。", ChangeState.Cancelled);
        if (existing.State == ChangeState.Expired || existing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return new Execution(false, AutomationErrors.Conflict, "该申请已过期。", ChangeState.Expired);

        var changed = await store.TransitionChangeAsync(changeRequestId, ChangeState.Cancelled,
            [ChangeState.Pending], actor, "cancelled by requester", DateTimeOffset.UtcNow, cancellationToken,
            requireUnexpired: true, requiredDeviceThumbprint: requiredDeviceThumbprint);
        if (!changed) return new Execution(false, AutomationErrors.Conflict, "只有待批准的申请可以撤销。", existing.State);
        await TryAuditAsync("automation_change_cancelled", "accepted", changeRequestId, actor, cancellationToken, grantId, existing.DeviceThumbprint);
        return new Execution(true, null, null, ChangeState.Cancelled);
    }

    /// <summary>
    /// 执行一次。顺序是：置 applying 并落库 → 在配置写锁内写入配置 → 置 applied 并落库。
    /// 不自动重试；失败回到 approved 并记录原因，由管理员决定重试或拒绝。
    /// </summary>
    public async Task<Execution> ExecuteAsync(Guid changeRequestId, string admin, CancellationToken cancellationToken)
    {
        var request = await store.GetChangeAsync(changeRequestId, cancellationToken);
        if (request is null) return new Execution(false, AutomationErrors.NotFound, "申请不存在。", ChangeState.Pending);
        if (request.State == ChangeState.Expired || request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return new Execution(false, AutomationErrors.Conflict, "该申请已超过 24 小时有效期，已自动过期。", ChangeState.Expired);
        if (request.State is not ChangeState.Approved)
            return new Execution(false, AutomationErrors.ChangeRequestNotApproved, "只有已批准的申请可以执行。", request.State);

        if (!await store.TransitionChangeAsync(changeRequestId, ChangeState.Applying, [ChangeState.Approved], admin, null,
                DateTimeOffset.UtcNow, cancellationToken))
            return new Execution(false, AutomationErrors.Conflict, "该申请状态已变化或已过期。", request.State);

        await TryAuditAsync("automation_change_applying", "accepted", changeRequestId, admin, cancellationToken, deviceThumbprint: request.DeviceThumbprint);

        try
        {
            await ApplyAsync(request, cancellationToken);
            await store.TransitionChangeAsync(changeRequestId, ChangeState.Applied, [ChangeState.Applying], admin, null,
                DateTimeOffset.UtcNow, cancellationToken, requireUnexpired: false);
            await TryAuditAsync("automation_change_applied", "accepted", changeRequestId, admin, cancellationToken, deviceThumbprint: request.DeviceThumbprint);
            return new Execution(true, null, null, ChangeState.Applied);
        }
        catch (Exception exception) when (exception is ChannelUpdateException or ClientUpdateException or ConfigurationException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Automation change {ChangeRequestId} of type {Type} failed.", changeRequestId, request.Type);
            await store.TransitionChangeAsync(changeRequestId, ChangeState.Approved, [ChangeState.Applying], admin, exception.Message,
                DateTimeOffset.UtcNow, cancellationToken, requireUnexpired: false);
            await TryAuditAsync("automation_change_failed", "failed", changeRequestId, admin, cancellationToken, deviceThumbprint: request.DeviceThumbprint);
            return new Execution(false, AutomationErrors.ValidationFailed, exception.Message, ChangeState.Approved);
        }
    }

    private async Task ApplyAsync(ChangeRequest request, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(request.PayloadJson);
        var root = payload.RootElement;
        var parts = request.TargetKey.Split('/');
        var clientId = parts[0];
        var channelId = parts.Length > 1 ? parts[1] : null;

        switch (request.Type)
        {
            case "client.create":
                await clientEditor.CreateAsync(new ClientCreateRequest(
                    clientId, GetString(root, "displayName") ?? clientId, true,
                    GetInt(root, "maxConnections") ?? 100, GetInt(root, "maxPendingConnections") ?? 20,
                    GetString(root, "agentServerHost") ?? runtime.Configuration.Server.Tunnel.DefaultAgentServerHost)
                {
                    Tags = GetStringArray(root, "tags") ?? []
                }, cancellationToken);
                break;
            case "client.delete":
                await clientEditor.DeleteAsync(clientId, cancellationToken);
                break;
            case "channel.create":
                await channelEditor.CreateAsync(clientId, new ChannelCreateRequest(
                    channelId!, GetString(root, "displayName") ?? channelId!, GetBool(root, "enabled") ?? true,
                    GetString(root, "listenAddress") ?? "0.0.0.0", GetInt(root, "listenPort") ?? 0,
                    GetString(root, "targetHost") ?? "127.0.0.1", GetInt(root, "targetPort") ?? 0,
                    GetInt(root, "maxConnections") ?? 10, GetInt(root, "targetConnectTimeoutSeconds") ?? 20)
                {
                    TransportProtocol = GetString(root, "transportProtocol"),
                    UdpIdleTimeoutSeconds = GetInt(root, "udpIdleTimeoutSeconds"),
                    AuthorizedClientsOnly = GetBool(root, "authorizedClientsOnly"),
                    EndToEndEncryptionEnabled = GetBool(root, "endToEndEncryptionEnabled"),
                    Tags = GetStringArray(root, "tags") ?? [],
                    SecurityGroupId = GetString(root, "securityGroupId")
                }, cancellationToken);
                break;
            case "channel.delete":
                await channelEditor.DeleteAsync(clientId, channelId!, cancellationToken);
                break;
            case "mapping.create":
                await channelEditor.CreateMappingAsync(clientId, new MappingCreateRequest(
                    GetString(root, "targetClientId")!, GetString(root, "targetChannelId")!), cancellationToken);
                break;
            case "channel.security":
                var existing = runtime.Configuration.Clients.TryGetValue(clientId, out var owner)
                    ? owner.Channels.SingleOrDefault(channel => channel.ChannelId == channelId)
                    : null;
                if (existing is null) throw new InvalidOperationException($"Channel {clientId}/{channelId} was not found.");
                await channelEditor.UpdateAsync(clientId, channelId!, new ChannelUpdateRequest(
                    existing.DisplayName, existing.Enabled, existing.ListenAddress, existing.ListenPort,
                    existing.TargetHost, existing.TargetPort, existing.MaxConnections, existing.TargetConnectTimeoutSeconds)
                {
                    TransportProtocol = existing.TransportProtocol,
                    UdpIdleTimeoutSeconds = existing.UdpIdleTimeoutSeconds,
                    AuthorizedClientsOnly = existing.AuthorizedClientsOnly,
                    EndToEndEncryptionEnabled = GetBool(root, "endToEndEncryptionEnabled"),
                    AccessSecret = existing.AccessSecret,
                    SecurityGroupId = existing.SecurityGroupId,
                    Tags = existing.Tags
                }, cancellationToken);
                break;
            case "credential.delete":
                throw new InvalidOperationException("credential.delete 需要在管理页面执行：自动化接口只负责提交申请，凭据写入与删除始终由管理员在页面完成。");
            default:
                throw new InvalidOperationException($"Unsupported change type {request.Type}.");
        }
    }

    /// <summary>
    /// 启动对账。apply 中间崩溃后：目标已达期望则补落 applied；否则回退 approved。
    /// 没有这一步，重试会撞上既有校验（Channel ID already exists / Channel was not found），
    /// 申请会永久卡在 approved，而配置其实已经生效。
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await writeLock.Gate.WaitAsync(cancellationToken);
        try
        {
            var applying = await store.ListApplyingChangesAsync(cancellationToken);
            foreach (var request in applying)
            {
                var reached = ReachableState(request);
                if (reached)
                {
                    await store.TransitionChangeAsync(request.ChangeRequestId, ChangeState.Applied, [ChangeState.Applying], "reconciler", null,
                        DateTimeOffset.UtcNow, cancellationToken, requireUnexpired: false);
                    logger.LogWarning("Automation change {ChangeRequestId} was reconciled to applied after a restart.", request.ChangeRequestId);
                }
                else
                {
                    await store.TransitionChangeAsync(request.ChangeRequestId, ChangeState.Approved, [ChangeState.Applying], "reconciler",
                        "执行在配置写入前中断，已回退为已批准，可重试。", DateTimeOffset.UtcNow, cancellationToken, requireUnexpired: false);
                    logger.LogWarning("Automation change {ChangeRequestId} was rolled back to approved after a restart.", request.ChangeRequestId);
                }
            }
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    private bool ReachableState(ChangeRequest request)
    {
        var parts = request.TargetKey.Split('/');
        var clientId = parts[0];
        var channelId = parts.Length > 1 ? parts[1] : null;
        var clients = runtime.Configuration.Clients;
        switch (request.Type)
        {
            case "client.create" or "client.delete":
                return request.Type == "client.create" ? clients.ContainsKey(clientId) : !clients.ContainsKey(clientId);

            case "channel.create" or "channel.delete":
                if (channelId is null) return false;
                if (!clients.TryGetValue(clientId, out var owner))
                    return request.Type == "channel.delete";
                return request.Type == "channel.delete"
                    ? owner.Channels.All(channel => channel.ChannelId != channelId)
                    : owner.Channels.Any(channel => channel.ChannelId == channelId);

            case "channel.security":
            {
                // 切换型目标不能只看「通道存在」：崩溃发生在写入前时通道也在，
                // 会被误判为已生效而吞掉这次加密开关变更。必须比对期望布尔值与实际值。
                if (!clients.TryGetValue(clientId, out var securedClient) || channelId is null) return false;
                var channel = securedClient.Channels.SingleOrDefault(item => item.ChannelId == channelId);
                if (channel is null) return false;
                if (!bool.TryParse(GetExpectedSecurityValue(request.PayloadJson), out var expected)) return false;
                return channel.EndToEndEncryptionEnabled == expected;
            }

            case "mapping.create":
                return clients.TryGetValue(clientId, out var caller) && channelId is not null &&
                       caller.OutboundMappings.Any(mapping => mapping.MappingId == channelId);

            default:
                return false;
        }
    }

    private static string? GetExpectedSecurityValue(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("endToEndEncryptionEnabled", out var value) &&
                   value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean().ToString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task TryAuditAsync(string eventType, string outcome, Guid changeRequestId, string? actor, CancellationToken cancellationToken,
        Guid? grantId = null, string? deviceThumbprint = null)
    {
        try
        {
            await audit.RecordAsync(new AuditEvent(eventType, outcome)
            {
                Actor = actor ?? "automation",
                ReasonCode = changeRequestId.ToString(),
                AutomationGrantId = grantId,
                DeviceKeyThumbprint = deviceThumbprint
            }, cancellationToken);
        }
        catch (AuditUnavailableException)
        {
            logger.LogError("Audit unavailable for automation change {EventType} {ChangeRequestId}.", eventType, changeRequestId);
        }
    }

    private static string NormalizeJson(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(doc.RootElement, AutomationJson.Options);
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string[]? GetStringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : null;
}