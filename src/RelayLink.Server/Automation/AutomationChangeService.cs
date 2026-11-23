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
    AuditService audit,
    ILogger<AutomationChangeService> logger)
{
    public static readonly IReadOnlyList<string> SupportedTypes =
    ["client.create", "client.delete", "channel.create", "channel.delete", "mapping.create", "channel.security"];

    public const int ExpiryHours = 24;

    public sealed record Submission(bool Ok, string? ErrorCode, string? Message, ChangeRequest? Request);
    public sealed record Execution(bool Ok, string? ErrorCode, string? Message, ChangeState State);

    public async Task<Submission> SubmitAsync(string type, JsonElement target, JsonElement payload, string? reason,
        string requestedBy, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!SupportedTypes.Contains(type, StringComparer.Ordinal))
            return new Submission(false, AutomationErrors.ValidationFailed, $"不支持的变更类型：{type}。", null);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || !Guid.TryParse(idempotencyKey, out _))
            return new Submission(false, AutomationErrors.ValidationFailed, "Idempotency-Key 必须是非空 UUID。", null);

        var targetKey = BuildTargetKey(type, target);
        if (targetKey is null)
            return new Submission(false, AutomationErrors.ValidationFailed, "target 字段不完整。", null);

        // credential.delete 在提交即拒：执行器无法代做凭据删除（凭据写入与删除始终
        // 由管理员在管理页面完成），放它进审批流只会产生一条必然失败的申请。
        if (type == "credential.delete")
            return new Submission(false, AutomationErrors.RequiresChangeRequest,
                "凭据删除请在管理页面执行；自动化接口不接受凭据变更申请。", null);

        // 自动化接入通道与访问映射不可经自动化删除或改加密开关：会让 CLI 当场断开。
        // 与 B 类 PATCH 的 IsAutomationChannel 保护对齐（复核发现 C 类路径漏了这条）。
        var selfProtected = SelfProtectionConflict(type, targetKey);
        if (selfProtected is not null)
            return new Submission(false, AutomationErrors.Conflict, selfProtected, null);

        // 同键重放返回同一申请。
        var existing = await store.FindChangeByIdempotencyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != type || existing.TargetKey != targetKey)
                return new Submission(false, AutomationErrors.Conflict, "该 Idempotency-Key 已用于不同的变更。", existing);
            return new Submission(true, null, null, existing);
        }

        // 同一 (type,target) 已有未终态申请时拒绝，避免多条申请以不确定顺序执行。
        var open = await store.FindOpenChangeAsync(type, targetKey, cancellationToken);
        if (open is not null)
            return new Submission(false, AutomationErrors.Conflict,
                $"该目标已有未完成的申请（{open.ChangeRequestId}），请先处理。", open);

        var validation = ValidatePayload(type, payload);
        if (validation is not null)
            return new Submission(false, AutomationErrors.ValidationFailed, validation, null);

        var now = DateTimeOffset.UtcNow;
        var request = new ChangeRequest(Guid.NewGuid(), type, targetKey, payload.GetRawText(), reason, requestedBy,
            ChangeState.Pending, now, now.AddHours(ExpiryHours), null, null, null, idempotencyKey);
        await store.CreateChangeAsync(request, cancellationToken);
        await TryAuditAsync("automation_change_requested", "accepted", request.ChangeRequestId, requestedBy, cancellationToken);
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
            channel.TargetHost is "127.0.0.1" or "::1" or "localhost";

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
    private static string? ValidatePayload(string type, JsonElement payload)
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

        return type switch
        {
            "client.create" when !payload.TryGetProperty("clientId", out _) => "client.create 需要 clientId。",
            "client.delete" when !payload.TryGetProperty("clientId", out _) => "client.delete 需要 clientId。",
            "channel.create" when !payload.TryGetProperty("channelId", out _) => "channel.create 需要 channelId。",
            "channel.delete" when !payload.TryGetProperty("channelId", out _) => "channel.delete 需要 channelId。",
            "mapping.create" when !payload.TryGetProperty("targetClientId", out _) || !payload.TryGetProperty("targetChannelId", out _) =>
                "mapping.create 需要 targetClientId 与 targetChannelId。",
            "channel.security" when !payload.TryGetProperty("endToEndEncryptionEnabled", out _) =>
                "channel.security 首期只支持 endToEndEncryptionEnabled（审计模式变更需删除后重建）。",
            _ => null
        };
    }

    private static string? BuildTargetKey(string type, JsonElement target)
    {
        if (target.ValueKind != JsonValueKind.Object) return null;
        var clientId = target.TryGetProperty("clientId", out var client) ? client.GetString() : null;
        var channelId = target.TryGetProperty("channelId", out var channel) ? channel.GetString() : null;
        var mappingId = target.TryGetProperty("mappingId", out var mapping) ? mapping.GetString() : null;
        return type switch
        {
            "client.create" or "client.delete" => clientId,
            "channel.create" or "channel.delete" or "channel.security" => clientId is null || channelId is null ? null : $"{clientId}/{channelId}",
            "mapping.create" => clientId is null || mappingId is null ? null : $"{clientId}/{mappingId}",
            // credential.delete 已从 SupportedTypes 移除；此分支仅为比对历史申请（idempotency 重放）保留，不代表该类型仍受支持。
            "credential.delete" => clientId is null || channelId is null ? null : $"{clientId}/{channelId}",
            _ => null
        };
    }

    public async Task<IReadOnlyList<ChangeRequest>> ListAsync(ChangeState? state, CancellationToken cancellationToken) =>
        await store.ListChangesAsync(state, 200, cancellationToken);

    public async Task<ChangeRequest?> GetAsync(Guid changeRequestId, CancellationToken cancellationToken) =>
        await store.GetChangeAsync(changeRequestId, cancellationToken);

    /// <summary>
    /// 管理员批准：pending → approved，并写审计。只改状态，不执行。
    /// </summary>
    public async Task<Execution> ApproveAsync(Guid changeRequestId, string admin, CancellationToken cancellationToken)
    {
        var changed = await store.TransitionChangeAsync(changeRequestId, ChangeState.Approved, [ChangeState.Pending], admin, null,
            DateTimeOffset.UtcNow, cancellationToken);
        if (!changed) return new Execution(false, AutomationErrors.Conflict, "该申请不在待批准状态。", ChangeState.Pending);
        await TryAuditAsync("automation_change_approved", "accepted", changeRequestId, admin, cancellationToken);
        return new Execution(true, null, null, ChangeState.Approved);
    }

    public async Task<Execution> RejectAsync(Guid changeRequestId, string admin, string? reason, CancellationToken cancellationToken)
    {
        var changed = await store.TransitionChangeAsync(changeRequestId, ChangeState.Rejected,
            [ChangeState.Pending, ChangeState.Approved], admin, reason, DateTimeOffset.UtcNow, cancellationToken);
        if (!changed) return new Execution(false, AutomationErrors.Conflict, "该申请已终结。", ChangeState.Rejected);
        await TryAuditAsync("automation_change_rejected", "denied", changeRequestId, admin, cancellationToken);
        return new Execution(true, null, null, ChangeState.Rejected);
    }

    public async Task<Execution> CancelAsync(Guid changeRequestId, string actor, CancellationToken cancellationToken)
    {
        var changed = await store.TransitionChangeAsync(changeRequestId, ChangeState.Cancelled,
            [ChangeState.Pending], actor, "cancelled by requester", DateTimeOffset.UtcNow, cancellationToken);
        if (!changed) return new Execution(false, AutomationErrors.Conflict, "只有待批准的申请可以撤销。", ChangeState.Cancelled);
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
        if (request.State is not ChangeState.Approved)
            return new Execution(false, AutomationErrors.ChangeRequestNotApproved, "只有已批准的申请可以执行。", request.State);

        if (!await store.TransitionChangeAsync(changeRequestId, ChangeState.Applying, [ChangeState.Approved], admin, null,
                DateTimeOffset.UtcNow, cancellationToken))
            return new Execution(false, AutomationErrors.Conflict, "该申请状态已变化。", request.State);

        await TryAuditAsync("automation_change_applying", "accepted", changeRequestId, admin, cancellationToken);

        try
        {
            await ApplyAsync(request, cancellationToken);
            await store.TransitionChangeAsync(changeRequestId, ChangeState.Applied, [ChangeState.Applying], admin, null,
                DateTimeOffset.UtcNow, cancellationToken);
            await TryAuditAsync("automation_change_applied", "accepted", changeRequestId, admin, cancellationToken);
            return new Execution(true, null, null, ChangeState.Applied);
        }
        catch (Exception exception) when (exception is ChannelUpdateException or ConfigurationException or InvalidOperationException)
        {
            logger.LogError(exception, "Automation change {ChangeRequestId} of type {Type} failed.", changeRequestId, request.Type);
            await store.TransitionChangeAsync(changeRequestId, ChangeState.Approved, [ChangeState.Applying], admin, exception.Message,
                DateTimeOffset.UtcNow, cancellationToken);
            await TryAuditAsync("automation_change_failed", "failed", changeRequestId, admin, cancellationToken);
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
                    GetInt(root, "maxConnections") ?? 100, GetInt(root, "maxPendingConnections") ?? 20, GetString(root, "agentServerHost") ?? string.Empty)
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
                // 首期只覆盖端到端加密开关。
                // 既有编辑器要求整包字段，因此先用当前通道值填充，只改动加密开关。
                var existing = runtime.Configuration.Clients.TryGetValue(clientId, out var owner)
                    ? owner.Channels.SingleOrDefault(channel => channel.ChannelId == channelId)
                    : null;
                if (existing is null) throw new InvalidOperationException($"Channel {clientId}/{channelId} was not found.");
                await channelEditor.UpdateAsync(clientId, channelId!, new ChannelUpdateRequest(
                    existing.DisplayName, existing.Enabled, existing.ListenAddress, existing.ListenPort,
                    existing.TargetHost, existing.TargetPort, existing.MaxConnections, existing.TargetConnectTimeoutSeconds)
                {
                    EndToEndEncryptionEnabled = GetBool(root, "endToEndEncryptionEnabled")
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
        var applying = await store.ListApplyingChangesAsync(cancellationToken);
        foreach (var request in applying)
        {
            var reached = ReachableState(request);
            if (reached)
            {
                await store.TransitionChangeAsync(request.ChangeRequestId, ChangeState.Applied, [ChangeState.Applying], "reconciler", null,
                    DateTimeOffset.UtcNow, cancellationToken);
                logger.LogWarning("Automation change {ChangeRequestId} was reconciled to applied after a restart.", request.ChangeRequestId);
            }
            else
            {
                await store.TransitionChangeAsync(request.ChangeRequestId, ChangeState.Approved, [ChangeState.Applying], "reconciler",
                    "执行在配置写入前中断，已回退为已批准，可重试。", DateTimeOffset.UtcNow, cancellationToken);
                logger.LogWarning("Automation change {ChangeRequestId} was rolled back to approved after a restart.", request.ChangeRequestId);
            }
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
                return clients.TryGetValue(clientId, out var owner) && channelId is not null &&
                       (request.Type == "channel.delete"
                           ? owner.Channels.All(channel => channel.ChannelId != channelId)
                           : owner.Channels.Any(channel => channel.ChannelId == channelId));

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

    private async Task TryAuditAsync(string eventType, string outcome, Guid changeRequestId, string? actor, CancellationToken cancellationToken)
    {
        try
        {
            await audit.RecordAsync(new AuditEvent(eventType, outcome)
            {
                Actor = actor ?? "automation",
                ReasonCode = changeRequestId.ToString()
            }, cancellationToken);
        }
        catch (AuditUnavailableException)
        {
            logger.LogError("Audit unavailable for automation change {EventType} {ChangeRequestId}.", eventType, changeRequestId);
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