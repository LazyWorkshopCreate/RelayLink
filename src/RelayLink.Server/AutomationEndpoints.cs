using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RelayLink.Server.Automation;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.Server;

/// <summary>
/// 自动化管理面的 HTTP 端点。只绑定 loopback 且只提供 TLS；
/// 可达性复用既有授权互访，不新增公网入口。
/// </summary>
public static class AutomationEndpoints
{
    private const string Prefix = "/api/v1/automation";

    public static void Map(WebApplication app)
    {
        // API 版本协商：不支持的头值返回 unsupported_api_version（CLI 退出码 9）。
        // 头缺省按当前版本处理，与契约一致。/info 本身不拦截——它是版本判定的来源。
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Prefix) &&
                !context.Request.Path.Value!.EndsWith("/info", StringComparison.Ordinal))
            {
                var header = context.Request.Headers[AutomationJson.ApiVersionHeader].ToString();
                if (!string.IsNullOrEmpty(header) &&
                    (!int.TryParse(header, out var version) || version != AutomationJson.SupportedApiVersion))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = new
                        {
                            code = AutomationErrors.UnsupportedApiVersion,
                            message = $"请求的 API 版本 {header} 不受支持；当前服务端支持 {AutomationJson.SupportedApiVersion}。",
                            retryable = false,
                            requestId = Guid.NewGuid().ToString()
                        }
                    }, AutomationJson.Options);
                    return;
                }
            }

            await next(context);
        });

        MapAnonymous(app);
        MapPairing(app);
        MapToken(app);
        MapAdmin(app);
    }

    // ---------------- 匿名端点 ----------------

    private static void MapAnonymous(WebApplication app)
    {
        app.MapGet(Prefix + "/info", async (HttpContext context, AutomationStore store, LoadedConfiguration configuration, AutomationPairingService pairing) =>
        {
            // /info 也是匿名端点：纳入进程级全局上限，避免被无限制刷。
            if (pairing.IsAnonymousRateLimited())
            {
                context.Response.Headers.RetryAfter = "60";
                return AutomationJson.Error(AutomationErrors.RateLimited, "请求过于频繁，请稍后重试。", 429, retryable: true);
            }

            var configured = configuration.Server.Automation;
            var enabled = configured is { Enabled: true };
            Guid? instanceId = null;
            if (enabled)
            {
                try { instanceId = await store.GetOrCreateInstanceIdAsync(context.RequestAborted); }
                catch (Exception) { instanceId = null; }
            }

            return AutomationJson.Ok(new
            {
                apiVersions = new[] { AutomationJson.SupportedApiVersion },
                minCliVersion = "0.1.0",
                requiresPairing = true,
                enabled,
                automationInstanceId = instanceId
            });
        });

        app.MapPost(Prefix + "/pairing/requests", async (HttpContext context, AutomationPairingService pairing) =>
        {
            PairingRequestBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<PairingRequestBody>(context.Request.Body, AutomationJson.Options, context.RequestAborted);
            }
            catch (JsonException)
            {
                return AutomationJson.Error(AutomationErrors.ValidationFailed, "请求体不是有效 JSON，或包含未定义字段。", 400);
            }

            if (body is null) return AutomationJson.Error(AutomationErrors.ValidationFailed, "请求体为空。", 400);

            // 匿名端点共享进程级全局上限：防止同主机进程刷穿待批准容量。
            // /info 与配对请求共用一个桶。
            if (pairing.IsAnonymousRateLimited())
            {
                context.Response.Headers.RetryAfter = "60";
                return AutomationJson.Error(AutomationErrors.RateLimited, "请求过于频繁，请稍后重试。", 429, retryable: true);
            }

            // 限流键用 RFC 7638 设备指纹 + 主体名，不用来源地址（来源恒为 loopback 临时端口），
            // 也不用公钥原文（可换钥绕过）。指纹与 CreatePairingAsync 内的登记值同源。
            var rateKey = $"{body.Subject ?? string.Empty}|{(string.IsNullOrEmpty(body.DevicePublicKey) ? "no-key" : ThumbprintOfPublicKey(body.DevicePublicKey))}";
            if (pairing.IsRateLimited(rateKey))
            {
                context.Response.Headers.RetryAfter = "60";
                return AutomationJson.Error(AutomationErrors.RateLimited, "配对请求过于频繁，请稍后重试。", 429, retryable: true);
            }

            var scopes = AutomationScopes.Normalize(body.Scopes);
            var (created, errorCode, message) = await pairing.CreatePairingAsync(
                body.Subject ?? string.Empty, body.Platform ?? "unknown", body.CliVersion ?? "unknown",
                scopes, body.DevicePublicKey ?? string.Empty, context.RequestAborted);

            if (created is null)
                return AutomationJson.Error(errorCode ?? AutomationErrors.ValidationFailed, message ?? "配对请求失败。",
                    AutomationJson.StatusFor(errorCode ?? AutomationErrors.ValidationFailed), AutomationJson.IsRetryable(errorCode ?? string.Empty));

            return AutomationJson.Ok(new
            {
                requestId = created.RequestId,
                userCode = UserCode.Format(created.UserCode),
                expiresAtUtc = created.ExpiresAtUtc,
                automationInstanceId = created.InstanceId,
                scopes
            });
        });
    }

    // ---------------- 配对端点（设备签名） ----------------

    private static void MapPairing(WebApplication app)
    {
        app.MapPost(Prefix + "/pairing/requests/{id}/status", async (string id, HttpContext context,
            AutomationStore store, AutomationPairingService pairing) =>
        {
            if (!Guid.TryParse(id, out var requestId))
                return AutomationJson.Error(AutomationErrors.PairingNotFound, "配对请求 ID 无效。", 404);

            var request = await store.GetPairingAsync(requestId, context.RequestAborted);
            if (request is null) return AutomationJson.Error(AutomationErrors.PairingNotFound, "配对请求不存在。", 404);

            using var deviceKey = DeviceProof.ImportPublicKey(request.DevicePublicKeyJwk);
            if (deviceKey is null) return AutomationJson.Error(AutomationErrors.StorageUnavailable, "设备公钥无法解析。", 503);

            if (!DeviceProof.TryParse(context.Request.Headers[DeviceProof.HeaderName].ToString(), deviceKey,
                    DateTimeOffset.UtcNow, request.DeviceThumbprint, out var proof, out var error))
                return AutomationJson.Error(error!.Code, error.Message, AutomationJson.StatusFor(error.Code));

            if (proof!.RequestId is not null && !string.Equals(proof.RequestId, requestId.ToString(), StringComparison.OrdinalIgnoreCase))
                return AutomationJson.Error(AutomationErrors.ProofInvalid, "证明中的 requestId 与路径不一致。", 401);

            // body 里可带 userCode（推荐）；为兼容仅凭设备证明的旧客户端，允许 proof.userCodeDigest 兜底。
            string? userCode = null;
            try
            {
                var statusBody = await JsonSerializer.DeserializeAsync<TokenRequestBody>(context.Request.Body, AutomationJson.Options, context.RequestAborted);
                userCode = statusBody?.UserCode;
            }
            catch (JsonException) { /* body 缺失或无效时走 proof 摘要校验 */ }

            var outcome = await pairing.QueryStatusAsync(requestId, proof, userCode, context.RequestAborted);

            if (!outcome.Ok)
                return AutomationJson.Error(outcome.ErrorCode!, outcome.Message!,
                    AutomationJson.StatusFor(outcome.ErrorCode!), AutomationJson.IsRetryable(outcome.ErrorCode!));

            return AutomationJson.Ok(new
            {
                requestId,
                state = outcome.Request!.State.ToString().ToLowerInvariant(),
                scopes = outcome.Request.ApprovedScopes ?? outcome.Request.Scopes,
                expiresAtUtc = outcome.Request.ExpiresAtUtc
            });
        });

        app.MapPost(Prefix + "/pairing/requests/{id}/token", async (string id, HttpContext context,
            AutomationStore store, AutomationPairingService pairing) =>
        {
            if (!Guid.TryParse(id, out var requestId))
                return AutomationJson.Error(AutomationErrors.PairingNotFound, "配对请求 ID 无效。", 404);

            var request = await store.GetPairingAsync(requestId, context.RequestAborted);
            if (request is null) return AutomationJson.Error(AutomationErrors.PairingNotFound, "配对请求不存在。", 404);

            using var deviceKey = DeviceProof.ImportPublicKey(request.DevicePublicKeyJwk);
            if (deviceKey is null) return AutomationJson.Error(AutomationErrors.StorageUnavailable, "设备公钥无法解析。", 503);

            if (!DeviceProof.TryParse(context.Request.Headers[DeviceProof.HeaderName].ToString(), deviceKey,
                    DateTimeOffset.UtcNow, request.DeviceThumbprint, out var proof, out var error))
                return AutomationJson.Error(error!.Code, error.Message, AutomationJson.StatusFor(error.Code));

            TokenRequestBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<TokenRequestBody>(context.Request.Body, AutomationJson.Options, context.RequestAborted);
            }
            catch (JsonException) { body = null; }

            var outcome = await pairing.ExchangeTokenAsync(requestId, proof!, body?.UserCode, context.RequestAborted);
            if (!outcome.Ok)
                return AutomationJson.Error(outcome.ErrorCode!, outcome.Message!,
                    AutomationJson.StatusFor(outcome.ErrorCode!), AutomationJson.IsRetryable(outcome.ErrorCode!));

            var grant = await store.FindGrantByTokenHashAsync(AutomationPairingService.HashToken(outcome.Token!), context.RequestAborted);
            return AutomationJson.Ok(new
            {
                token = outcome.Token,
                grantId = grant?.GrantId,
                scopes = grant?.Scopes ?? [],
                expiresAtUtc = grant?.ExpiresAtUtc,
                automationInstanceId = await store.GetOrCreateInstanceIdAsync(context.RequestAborted)
            });
        });

        // 取消自己尚未换取的配对请求。
        app.MapDelete(Prefix + "/pairing/requests/{id}", async (string id, HttpContext context,
            AutomationStore store) =>
        {
            if (!Guid.TryParse(id, out var requestId))
                return AutomationJson.Error(AutomationErrors.PairingNotFound, "配对请求 ID 无效。", 404);

            var request = await store.GetPairingAsync(requestId, context.RequestAborted);
            if (request is null) return AutomationJson.Error(AutomationErrors.PairingNotFound, "配对请求不存在。", 404);

            using var deviceKey = DeviceProof.ImportPublicKey(request.DevicePublicKeyJwk);
            if (deviceKey is null) return AutomationJson.Error(AutomationErrors.StorageUnavailable, "设备公钥无法解析。", 503);

            if (!DeviceProof.TryParse(context.Request.Headers[DeviceProof.HeaderName].ToString(), deviceKey,
                    DateTimeOffset.UtcNow, request.DeviceThumbprint, out _, out var error))
                return AutomationJson.Error(error!.Code, error.Message, AutomationJson.StatusFor(error.Code));

            if (request.State is not PairingState.Pending)
                return AutomationJson.Error(AutomationErrors.Conflict, "只有待批准的请求可以取消。", 409);

            // 带状态守卫：GetPairing 与更新之间管理员可能恰好批准，不能把 Approved 覆盖成 Denied。
            var cancelled = await store.SetPairingStateAsync(requestId, PairingState.Denied, DateTimeOffset.UtcNow,
                [PairingState.Pending], context.RequestAborted);
            if (!cancelled) return AutomationJson.Error(AutomationErrors.Conflict, "该请求状态已变化。", 409);
            return AutomationJson.Ok(new { requestId, state = "cancelled" });
        });
    }

    // ---------------- 令牌端点 ----------------

    private static void MapToken(WebApplication app)
    {
        app.MapGet(Prefix + "/whoami", async (HttpContext context, AutomationTokenService tokens) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, null, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            var grant = denial.Authorized.Grant;
            return AutomationJson.Ok(new
            {
                grantId = grant.GrantId,
                subject = grant.Subject,
                platform = grant.Platform,
                scopes = grant.Scopes,
                expiresAtUtc = grant.ExpiresAtUtc
            });
        });

        app.MapGet(Prefix + "/health", async (HttpContext context, AutomationTokenService tokens, ServerRuntime runtime) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ServerRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            await tokens.TryAuditAsync("automation_request", "accepted", denial.Authorized.Grant, null, null, context.RequestAborted);
            return AutomationJson.Ok(new { ready = runtime.IsReady, startedAtUtc = runtime.StartedAtUtc });
        });

        app.MapGet(Prefix + "/version", async (HttpContext context, AutomationTokenService tokens) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ServerRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            await tokens.TryAuditAsync("automation_request", "accepted", denial.Authorized.Grant, null, null, context.RequestAborted);
            var assembly = typeof(AutomationEndpoints).Assembly;
            return AutomationJson.Ok(new
            {
                serverVersion = assembly.GetName().Version?.ToString() ?? "unknown",
                apiVersions = new[] { AutomationJson.SupportedApiVersion },
                automationInstanceId = denial.Authorized.InstanceId
            });
        });

        app.MapGet(Prefix + "/overview", async (HttpContext context, AutomationTokenService tokens, ServerRuntime runtime) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ServerRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            await tokens.TryAuditAsync("automation_request", "accepted", denial.Authorized.Grant, null, null, context.RequestAborted);
            return AutomationJson.Ok(new
            {
                automationInstanceId = denial.Authorized.InstanceId,
                startedAtUtc = runtime.StartedAtUtc,
                clientsTotal = runtime.Configuration.Clients.Count,
                clientsOnline = runtime.Sessions.Count
            });
        });

        app.MapGet(Prefix + "/clients", async (HttpContext context, AutomationTokenService tokens, ServerRuntime runtime) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ClientsRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            await tokens.TryAuditAsync("automation_request", "accepted", denial.Authorized.Grant, null, null, context.RequestAborted);
            return AutomationJson.Ok(runtime.Configuration.Clients.Values.Select(client => new
            {
                clientId = client.ClientId,
                displayName = client.DisplayName,
                enabled = client.Enabled,
                tags = client.Tags,
                maxConnections = client.MaxConnections,
                maxPendingConnections = client.MaxPendingConnections,
                online = runtime.Sessions.TryGet(client.ClientId, out _),
                channels = client.Channels.Count
            }));
        });

        app.MapGet(Prefix + "/clients/{clientId}/channels", async (string clientId, HttpContext context,
            AutomationTokenService tokens, ServerRuntime runtime) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ChannelsRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            if (!runtime.Configuration.Clients.TryGetValue(clientId, out var client))
                return AutomationJson.Error(AutomationErrors.NotFound, "客户端不存在。", 404);

            await tokens.TryAuditAsync("automation_request", "accepted", denial.Authorized.Grant, null, null, context.RequestAborted);
            return AutomationJson.Ok(client.Channels.Select(channel => new
            {
                channelId = channel.ChannelId,
                displayName = channel.DisplayName,
                enabled = channel.Enabled,
                transportProtocol = channel.TransportProtocol,
                applicationProtocol = channel.ApplicationProtocol,
                listenAddress = channel.ListenAddress,
                listenPort = channel.ListenPort,
                targetHost = channel.TargetHost,
                targetPort = channel.TargetPort,
                maxConnections = channel.MaxConnections,
                authorizedClientsOnly = channel.AuthorizedClientsOnly,
                endToEndEncryptionEnabled = channel.EndToEndEncryptionEnabled,
                tags = channel.Tags
            }));
        });

        app.MapGet(Prefix + "/clients/{clientId}/mappings", async (string clientId, HttpContext context,
            AutomationTokenService tokens, ServerRuntime runtime) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.MappingsRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            if (!runtime.Configuration.Clients.TryGetValue(clientId, out var client))
                return AutomationJson.Error(AutomationErrors.NotFound, "客户端不存在。", 404);

            await tokens.TryAuditAsync("automation_request", "accepted", denial.Authorized.Grant, null, null, context.RequestAborted);
            return AutomationJson.Ok(client.OutboundMappings.Select(mapping => new
            {
                mappingId = mapping.MappingId,
                enabled = mapping.Enabled,
                targetClientId = mapping.TargetClientId,
                targetChannelId = mapping.TargetChannelId
            }));
        });

        // B 类：通道受控写入。字段白名单，未列出的一律 unknown_field。
        app.MapPatch(Prefix + "/clients/{clientId}/channels/{channelId}", async (string clientId, string channelId,
            HttpContext context, AutomationTokenService tokens, ServerRuntime runtime,
            ChannelConfigurationEditor editor, IdempotencyCache idempotency) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ChannelsWrite, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            var key = context.Request.Headers[AutomationJson.IdempotencyHeader].ToString();
            if (string.IsNullOrWhiteSpace(key) || !Guid.TryParse(key, out _))
                return AutomationJson.Error(AutomationErrors.ValidationFailed, "缺少有效的 Idempotency-Key（UUID）。", 400);

            if (!runtime.Configuration.Clients.TryGetValue(clientId, out var owner) ||
                owner.Channels.All(channel => channel.ChannelId != channelId))
                return AutomationJson.Error(AutomationErrors.NotFound, "通道不存在。", 404);

            // 自动化接入通道自身不可经自动化停用：会当场断掉本 CLI 的连接。
            if (idempotency.IsAutomationChannel(runtime, clientId, channelId))
                return AutomationJson.Error(AutomationErrors.Conflict,
                    "目标是当前自动化接入通道，禁止经自动化停用或修改；请改走管理页面。", 409);

            PatchBody? patch;
            try
            {
                patch = await JsonSerializer.DeserializeAsync<PatchBody>(context.Request.Body, AutomationJson.Options, context.RequestAborted);
            }
            catch (JsonException exception)
            {
                return AutomationJson.Error(AutomationErrors.UnknownField, $"请求体包含未允许的字段：{exception.Message}", 400);
            }

            if (patch is null) return AutomationJson.Error(AutomationErrors.ValidationFailed, "请求体为空。", 400);

            // 幂等键在这里真正生效：同键不同载荷 conflict；同键同载荷重放返回回执。
            // fingerprint 取载荷与目标通道的组合摘要，避免「同键不同目标」被误判为重放。
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{clientId}/{channelId}|{JsonSerializer.Serialize(patch, AutomationJson.Options)}")));
            if (!idempotency.TryReserve(key, fingerprint))
            {
                // 预登记已存在：键可能在 24h 清理窗口边界被移除（prior 为 null），此时按可重新执行处理，
                // 不能落进 replayed 假成功。
                var prior = idempotency.GetFingerprint(key);
                if (prior is not null && !string.Equals(prior, fingerprint, StringComparison.Ordinal))
                    return AutomationJson.Error(AutomationErrors.Conflict, "该 Idempotency-Key 已用于不同的载荷。", 409);
                if (prior is not null)
                {
                    // 同键同载荷重放：不再次执行，按幂等语义返回回执。
                    context.Response.Headers[AutomationJson.OperationIdHeader] = key;
                    context.Response.Headers[AutomationJson.AuditStatusHeader] = "recorded";
                    return AutomationJson.Ok(new { channelId, replayed = true },
                        auditStatus: "recorded", operationId: key);
                }
                // prior 为 null：重新登记并继续执行。
                idempotency.TryReserve(key, fingerprint);
            }

            var existing = owner.Channels.Single(channel => channel.ChannelId == channelId);
            var updated = new ChannelUpdateRequest(
                patch.DisplayName ?? existing.DisplayName, patch.Enabled ?? existing.Enabled,
                existing.ListenAddress, existing.ListenPort, existing.TargetHost, existing.TargetPort,
                patch.MaxConnections ?? existing.MaxConnections,
                patch.TargetConnectTimeoutSeconds ?? existing.TargetConnectTimeoutSeconds)
            {
                Tags = patch.Tags ?? existing.Tags,
                UdpIdleTimeoutSeconds = patch.UdpIdleTimeoutSeconds ?? existing.UdpIdleTimeoutSeconds
            };

            try
            {
                // 写前审计闸门：审计不可用则不执行任何配置变更（需求「失败关闭」）。
                // 用独立的 audit_unavailable 判断与 catch，避免把业务写异常误报为审计故障。
                try
                {
                    await tokens.RequireAuditAsync(denial.Authorized.Grant, "automation_change_applied", context.RequestAborted);
                }
                catch (AuditUnavailableException)
                {
                    idempotency.Release(key);
                    return AutomationJson.Error(AutomationErrors.AuditUnavailable,
                        "审计存储不可用；本次变更未执行。", 503, retryable: true);
                }

                var (saved, push) = await editor.UpdateAsync(clientId, channelId, updated, context.RequestAborted);
                context.Response.Headers[AutomationJson.OperationIdHeader] = key;
                context.Response.Headers[AutomationJson.AuditStatusHeader] = "recorded";

                return AutomationJson.Ok(new
                {
                    channelId = saved.ChannelId,
                    displayName = saved.DisplayName,
                    enabled = saved.Enabled,
                    maxConnections = saved.MaxConnections,
                    tags = saved.Tags,
                    pushedToAgent = push.PushedToAgent,
                    pushFailureReason = push.FailureReason
                }, auditStatus: "recorded", operationId: key);
            }
            catch (Exception exception) when (exception is ChannelUpdateException or ConfigurationException)
            {
                // 执行失败必须解除预留：否则该键被毒化 24h，同键同载荷重试会命中
                // replayed 分支返回 200 假成功，而变更实际从未生效。
                idempotency.Release(key);
                throw;
            }
        });

        // C 类：变更申请。
        app.MapPost(Prefix + "/change-requests", async (HttpContext context, AutomationTokenService tokens, AutomationChangeService changes) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ChangesWrite, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            var key = context.Request.Headers[AutomationJson.IdempotencyHeader].ToString();
            if (string.IsNullOrWhiteSpace(key) || !Guid.TryParse(key, out _))
                return AutomationJson.Error(AutomationErrors.ValidationFailed, "缺少有效的 Idempotency-Key（UUID）。", 400);

            ChangeRequestBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<ChangeRequestBody>(context.Request.Body, AutomationJson.Options, context.RequestAborted);
            }
            catch (JsonException exception)
            {
                return AutomationJson.Error(AutomationErrors.UnknownField, $"请求体包含未允许的字段：{exception.Message}", 400);
            }

            if (body is null) return AutomationJson.Error(AutomationErrors.ValidationFailed, "请求体为空。", 400);

            // C 类申请同样受审计闸门约束：审计不可用时不落库、返回 audit_unavailable。
            try
            {
                await tokens.RequireAuditAsync(denial.Authorized.Grant, "automation_change_requested", context.RequestAborted);
            }
            catch (AuditUnavailableException)
            {
                return AutomationJson.Error(AutomationErrors.AuditUnavailable,
                    "审计存储不可用；本次申请未提交。", 503, retryable: true);
            }

            var submission = await changes.SubmitAsync(body.Type ?? string.Empty, body.Target, body.Payload, body.Reason,
                $"automation:{denial.Authorized.Grant.Subject}", key, context.RequestAborted);
            if (!submission.Ok)
                return AutomationJson.Error(submission.ErrorCode!, submission.Message!,
                    AutomationJson.StatusFor(submission.ErrorCode!), AutomationJson.IsRetryable(submission.ErrorCode!));

            return AutomationJson.Ok(new
            {
                changeRequestId = submission.Request!.ChangeRequestId,
                type = submission.Request.Type,
                state = submission.Request.State.ToString().ToLowerInvariant(),
                expiresAtUtc = submission.Request.ExpiresAtUtc
            }, operationId: key);
        });

        app.MapGet(Prefix + "/change-requests", async (HttpContext context, AutomationTokenService tokens, AutomationChangeService changes) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ChangesRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            var items = await changes.ListAsync(null, context.RequestAborted);
            return AutomationJson.Ok(items.Select(item => new
            {
                changeRequestId = item.ChangeRequestId,
                type = item.Type,
                state = item.State.ToString().ToLowerInvariant(),
                createdAtUtc = item.CreatedAtUtc,
                expiresAtUtc = item.ExpiresAtUtc,
                failureReason = item.FailureReason
            }));
        });

        app.MapGet(Prefix + "/change-requests/{id}", async (string id, HttpContext context,
            AutomationTokenService tokens, AutomationChangeService changes) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ChangesRead, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            if (!Guid.TryParse(id, out var changeRequestId))
                return AutomationJson.Error(AutomationErrors.NotFound, "申请 ID 无效。", 404);
            var item = await changes.GetAsync(changeRequestId, context.RequestAborted);
            if (item is null) return AutomationJson.Error(AutomationErrors.NotFound, "申请不存在。", 404);

            return AutomationJson.Ok(new
            {
                changeRequestId = item.ChangeRequestId,
                type = item.Type,
                targetKey = item.TargetKey,
                state = item.State.ToString().ToLowerInvariant(),
                createdAtUtc = item.CreatedAtUtc,
                expiresAtUtc = item.ExpiresAtUtc,
                decidedAtUtc = item.DecidedAtUtc,
                failureReason = item.FailureReason
            });
        });

        app.MapDelete(Prefix + "/change-requests/{id}", async (string id, HttpContext context,
            AutomationTokenService tokens, AutomationChangeService changes) =>
        {
            var denial = await tokens.AuthorizeAsync(context.Request, AutomationScopes.ChangesWrite, context.RequestAborted);
            if (denial.Authorized is null)
                return AutomationJson.Error(denial.Code, denial.Message, AutomationJson.StatusFor(denial.Code), AutomationJson.IsRetryable(denial.Code));

            if (!Guid.TryParse(id, out var changeRequestId))
                return AutomationJson.Error(AutomationErrors.NotFound, "申请 ID 无效。", 404);
            var result = await changes.CancelAsync(changeRequestId, $"automation:{denial.Authorized.Grant.Subject}", context.RequestAborted);
            if (!result.Ok) return AutomationJson.Error(result.ErrorCode!, result.Message!, AutomationJson.StatusFor(result.ErrorCode!));
            return AutomationJson.Ok(new { changeRequestId, state = "cancelled" });
        });
    }

    // ---------------- 管理页面端点（管理员会话 + CSRF） ----------------

    private static void MapAdmin(WebApplication app)
    {
        app.MapGet("/api/v1/admin/automation/pairings", async (HttpRequest request, AdminSessionService sessions,
            AutomationAdminService admin, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
            var state = request.Query.TryGetValue("state", out var value) && Enum.TryParse<PairingState>(value.ToString(), true, out var parsed)
                ? parsed : (PairingState?)null;
            var items = await admin.ListPairingsAsync(state, cancellationToken);
            return Results.Ok(new
            {
                pairings = items.Select(item => new
                {
                    requestId = item.RequestId,
                    subject = item.Subject,
                    platform = item.Platform,
                    cliVersion = item.CliVersion,
                    scopes = item.Scopes,
                    deviceKeyThumbprint = item.DeviceThumbprint,
                    userCode = item.UserCodeDisplay,
                    state = item.State.ToString().ToLowerInvariant(),
                    createdAtUtc = item.CreatedAtUtc,
                    expiresAtUtc = item.ExpiresAtUtc,
                    decidedBy = item.DecidedBy
                })
            });
        });

        app.MapPost("/api/v1/admin/automation/pairings/{id}/approve", async (string id, HttpRequest request,
            AdminSessionService sessions, AutomationAdminService admin, AutomationApproveRequest body, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: true, out var session)) return Results.Unauthorized();
            if (!Guid.TryParse(id, out var requestId)) return Results.NotFound();
            var result = await admin.ApproveAsync(requestId, body.ApprovedScopes, body.LifetimeMinutes, $"admin:{session!.Id[..8]}", cancellationToken);
            return result.Ok ? Results.NoContent()
                : Results.BadRequest(new { error = result.Message, code = result.ErrorCode });
        });

        app.MapPost("/api/v1/admin/automation/pairings/{id}/deny", async (string id, HttpRequest request,
            AdminSessionService sessions, AutomationAdminService admin, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: true, out var session)) return Results.Unauthorized();
            if (!Guid.TryParse(id, out var requestId)) return Results.NotFound();
            var result = await admin.DenyAsync(requestId, $"admin:{session!.Id[..8]}", cancellationToken);
            return result.Ok ? Results.NoContent() : Results.BadRequest(new { error = result.Message, code = result.ErrorCode });
        });

        app.MapGet("/api/v1/admin/automation/grants", async (HttpRequest request, AdminSessionService sessions,
            AutomationAdminService admin, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
            var grants = await admin.ListGrantsAsync(cancellationToken);
            return Results.Ok(new
            {
                grants = grants.Select(item => new
                {
                    grantId = item.GrantId,
                    subject = item.Subject,
                    platform = item.Platform,
                    deviceKeyThumbprint = item.DeviceThumbprint,
                    scopes = item.Scopes,
                    createdAtUtc = item.CreatedAtUtc,
                    expiresAtUtc = item.ExpiresAtUtc,
                    revokedAtUtc = item.RevokedAtUtc
                })
            });
        });

        app.MapDelete("/api/v1/admin/automation/grants/{id}", async (string id, HttpRequest request,
            AdminSessionService sessions, AutomationAdminService admin, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: true, out var session)) return Results.Unauthorized();
            if (!Guid.TryParse(id, out var grantId)) return Results.NotFound();
            var result = await admin.RevokeAsync(grantId, $"admin:{session!.Id[..8]}", cancellationToken);
            return result.Ok ? Results.NoContent() : Results.NotFound(new { error = result.Message });
        });

        // 变更申请的批准/拒绝/执行/重试，全部只在管理页面。
        app.MapGet("/api/v1/admin/automation/change-requests", async (HttpRequest request, AdminSessionService sessions,
            AutomationChangeService changes, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
            var state = request.Query.TryGetValue("state", out var value) && Enum.TryParse<ChangeState>(value.ToString(), true, out var parsed)
                ? parsed : (ChangeState?)null;
            var items = await changes.ListAsync(state, cancellationToken);
            return Results.Ok(new { changeRequests = items.Select(ChangeView) });
        });

        app.MapPost("/api/v1/admin/automation/change-requests/{id}/approve", async (string id, HttpRequest request,
            AdminSessionService sessions, AutomationChangeService changes, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: true, out var session)) return Results.Unauthorized();
            if (!Guid.TryParse(id, out var changeId)) return Results.NotFound();
            var result = await changes.ApproveAsync(changeId, $"admin:{session!.Id[..8]}", cancellationToken);
            return result.Ok ? Results.NoContent() : Results.BadRequest(new { error = result.Message, code = result.ErrorCode });
        });

        app.MapPost("/api/v1/admin/automation/change-requests/{id}/reject", async (string id, HttpRequest request,
            AdminSessionService sessions, AutomationChangeService changes, AutomationRejectRequest? body, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: true, out var session)) return Results.Unauthorized();
            if (!Guid.TryParse(id, out var changeId)) return Results.NotFound();
            var result = await changes.RejectAsync(changeId, $"admin:{session!.Id[..8]}", body?.Reason, cancellationToken);
            return result.Ok ? Results.NoContent() : Results.BadRequest(new { error = result.Message, code = result.ErrorCode });
        });

        app.MapPost("/api/v1/admin/automation/change-requests/{id}/execute", async (string id, HttpRequest request,
            AdminSessionService sessions, AutomationChangeService changes, CancellationToken cancellationToken) =>
        {
            if (!sessions.TryAuthorize(request, requireCsrf: true, out var session)) return Results.Unauthorized();
            if (!Guid.TryParse(id, out var changeId)) return Results.NotFound();
            var result = await changes.ExecuteAsync(changeId, $"admin:{session!.Id[..8]}", cancellationToken);
            return result.Ok ? Results.Ok(new { state = "applied" })
                : Results.BadRequest(new { error = result.Message, code = result.ErrorCode, state = result.State.ToString().ToLowerInvariant() });
        });
    }

    private static object ChangeView(ChangeRequest item) => new
    {
        changeRequestId = item.ChangeRequestId,
        type = item.Type,
        targetKey = item.TargetKey,
        payload = item.PayloadJson,
        reason = item.Reason,
        requestedBy = item.RequestedBy,
        state = item.State.ToString().ToLowerInvariant(),
        createdAtUtc = item.CreatedAtUtc,
        expiresAtUtc = item.ExpiresAtUtc,
        failureReason = item.FailureReason
    };

    /// <summary>从 JWK 文本计算 RFC 7638 指纹，用于限流键（与设备登记时的指纹同源）。</summary>
    private static string? ThumbprintOfPublicKey(string jwkJson)
    {
        using var key = DeviceProof.ImportPublicKey(jwkJson);
        return key is null ? null : DeviceProof.ThumbprintOf(key);
    }

    private sealed record PairingRequestBody(string? Subject, string? Platform, string? CliVersion, string[]? Scopes, string? DevicePublicKey);
    private sealed record TokenRequestBody(string? UserCode);
    private sealed record PatchBody(string? DisplayName, bool? Enabled, int? MaxConnections, int? TargetConnectTimeoutSeconds,
        int? UdpIdleTimeoutSeconds, string[]? Tags);
    private sealed record ChangeRequestBody(string? Type, JsonElement Target, JsonElement Payload, string? Reason);
}

public sealed record AutomationApproveRequest(string[]? ApprovedScopes, int? LifetimeMinutes);
public sealed record AutomationRejectRequest(string? Reason);

/// <summary>
/// 幂等回执缓存：同键不同载荷拒绝，同键重放返回首次结果。
/// 有界且按时间退休，避免无界增长。
/// </summary>
public sealed class IdempotencyCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Fingerprint, DateTimeOffset At)> entries = new(StringComparer.Ordinal);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public bool TryReserve(string key, string fingerprint)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in entries.Where(pair => now - pair.Value.At > Retention).Select(pair => pair.Key).ToArray())
            entries.TryRemove(stale, out _);
        return entries.TryAdd(key, (fingerprint, now));
    }

    /// <summary>取该键首次登记的载荷指纹；不存在时返回 null。</summary>
    public string? GetFingerprint(string key) =>
        entries.TryGetValue(key, out var value) ? value.Fingerprint : null;

    /// <summary>
    /// 解除预留。执行失败时必须调用：否则该键被毒化到 24h 过期，
    /// 客户端同键同载荷重试会命中 replayed 分支返回 200 假成功，而变更实际从未生效。
    /// </summary>
    public void Release(string key) => entries.TryRemove(key, out _);

    /// <summary>
    /// 判断某通道是否为自动化接入通道：目标为回环且端口等于 automation.port。
    /// 这类通道禁止经自动化停用，否则 CLI 会当场断开。
    /// </summary>
    public bool IsAutomationChannel(ServerRuntime runtime, string clientId, string channelId)
    {
        if (runtime.Configuration.Server.Automation is not { } automation) return false;
        if (!runtime.Configuration.Clients.TryGetValue(clientId, out var client)) return false;
        var channel = client.Channels.SingleOrDefault(item => item.ChannelId == channelId);
        return channel is not null && channel.TargetPort == automation.Port &&
               (channel.TargetHost is "127.0.0.1" or "::1" or "localhost");
    }
}
