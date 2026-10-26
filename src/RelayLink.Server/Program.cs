using System.Net;
using Microsoft.Data.Sqlite;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Logging;
using Serilog;

var parsed = ParseArguments(args);
Serilog.Debugging.SelfLog.Enable(message => Console.Error.WriteLine(message));
var logDirectory = parsed.LogDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(parsed.ConfigurationPath))!, "logs");
using var logger = parsed.CheckOnly ? null : FileLogging.Create(logDirectory, "server");
var loader = new ConfigurationLoader();
LoadedConfiguration loaded;
try
{
    loaded = loader.Load(parsed.ConfigurationPath);
}
catch (ConfigurationException exception)
{
    logger?.Error("Server configuration validation failed: {Reason}", exception.Message);
    Console.Error.WriteLine($"Configuration validation failed: {exception.Message}");
    return 2;
}

foreach (var path in SecretFilePermissions.FindLoose(SecretFilePermissions.SensitivePaths(parsed.ConfigurationPath, loaded)))
{
    var warning = SecretFilePermissions.LooseWarning(path);
    logger?.Error(warning);
    Console.Error.WriteLine(warning);
}

if (parsed.CheckOnly)
{
    Console.WriteLine("Configuration validation succeeded.");
    return 0;
}

try
{
    logger!.Information("Server starting.");
    if (!DashboardListenPolicy.IsLoopback(loaded.Server.Dashboard.ListenAddress))
    {
        var warning = DashboardListenPolicy.NonLoopbackWarning(loaded.Server.Dashboard.ListenAddress);
        logger.Error(warning);
        Console.Error.WriteLine(warning);
    }
    var options = new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory };
    var builder = WebApplication.CreateBuilder(options);
    builder.Host.UseWindowsService();
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(logger, dispose: false);
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(40));
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
        kestrel.Listen(IPAddress.Parse(loaded.Server.Dashboard.ListenAddress), loaded.Server.Dashboard.Port);
    });
    builder.Services.AddSingleton(loaded);
    builder.Services.AddSingleton(loader);
    builder.Services.AddSingleton<SessionRegistry>();
    builder.Services.AddSingleton<AuthenticationService>();
    builder.Services.AddSingleton<ServerRuntime>();
    builder.Services.AddSingleton<PendingConnectionRegistry>();
    builder.Services.AddSingleton<PeerRelayRegistry>();
    builder.Services.AddSingleton<MetricsRegistry>();
    builder.Services.AddSingleton<TrafficHistoryService>();
    builder.Services.AddSingleton<AuditService>();
    builder.Services.AddSingleton<AdminSessionService>();
    builder.Services.AddSingleton<AdminLoginGuard>();
    builder.Services.AddSingleton<ProxyListenerService>();
    builder.Services.AddSingleton<ConfigurationWriteLock>();
    builder.Services.AddSingleton(sp => new ChannelConfigurationEditor(parsed.ConfigurationPath, loader, sp.GetRequiredService<ServerRuntime>(), sp.GetRequiredService<ProxyListenerService>(), sp.GetRequiredService<PeerRelayRegistry>(), sp.GetRequiredService<SessionRegistry>(), sp.GetRequiredService<ConfigurationWriteLock>()));
    builder.Services.AddSingleton(sp => new ClientConfigurationEditor(parsed.ConfigurationPath, loader, sp.GetRequiredService<ServerRuntime>(), sp.GetRequiredService<ProxyListenerService>(), sp.GetRequiredService<PeerRelayRegistry>(), sp.GetRequiredService<SessionRegistry>(), sp.GetRequiredService<ConfigurationWriteLock>()));
    builder.Services.AddSingleton(sp => new SecurityGroupEditor(parsed.ConfigurationPath, loader, sp.GetRequiredService<ServerRuntime>(), sp.GetRequiredService<ProxyListenerService>(), sp.GetRequiredService<ConfigurationWriteLock>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditService>());
    builder.Services.AddHostedService<TunnelAcceptorService>();
    builder.Services.AddHostedService<DataAcceptorService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyListenerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TrafficHistoryService>());

    var app = builder.Build();
    app.Use(async (context, next) =>
    {
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        await next(context);
    });
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
    app.MapGet("/health/ready", (ServerRuntime runtime) => runtime.IsReady
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/api/v1/admin/session", (HttpRequest request, AdminSessionService sessions) =>
    {
        return sessions.TryAuthorize(request, requireCsrf: false, out var session)
            ? Results.Ok(new { authenticated = true, csrfToken = session!.CsrfToken, expiresAtUtc = session.ExpiresAtUtc })
            : Results.Ok(new { authenticated = false });
    });
    app.MapGet("/api/v1/admin/agent-defaults", (HttpRequest request, AdminSessionService sessions, ServerRuntime runtime) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
        var tunnel = runtime.Configuration.Server.Tunnel;
        return Results.Ok(new { serverHost = tunnel.DefaultAgentServerHost, serverPort = tunnel.Port, dataPort = tunnel.EffectiveDataPort, useTls = tunnel.TlsEnabled });
    });
    app.MapGet("/api/v1/admin/next-channel-port", (HttpRequest request, AdminSessionService sessions, ServerRuntime runtime) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
        var transportProtocol = request.Query["transportProtocol"].FirstOrDefault() ?? "tcp";
        if (transportProtocol is not ("tcp" or "udp")) return Results.BadRequest(new { error = "Invalid transport protocol." });
        var port = ChannelPortSuggestion.Find(runtime.Configuration, transportProtocol: transportProtocol);
        return port is int available ? Results.Ok(new { listenPort = available }) : Results.Problem("No available cloud listener port.", statusCode: StatusCodes.Status503ServiceUnavailable);
    });
    app.MapGet("/api/v1/admin/security-groups", (HttpRequest request, AdminSessionService sessions, ServerRuntime runtime) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
        return Results.Ok(new { securityGroups = runtime.Configuration.Server.SecurityGroups });
    });
    app.MapPost("/api/v1/admin/security-groups", async (SecurityGroupCreateRequest create, HttpRequest request, AdminSessionService sessions, SecurityGroupEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try { var group = await editor.CreateAsync(create, cancellationToken); return Results.Created($"/api/v1/admin/security-groups/{group.Id}", group); }
        catch (SecurityGroupUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapPut("/api/v1/admin/security-groups/{id}", async (string id, SecurityGroupUpdateRequest update, HttpRequest request, AdminSessionService sessions, SecurityGroupEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try { return Results.Ok(await editor.UpdateAsync(id, update, cancellationToken)); }
        catch (SecurityGroupUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapDelete("/api/v1/admin/security-groups/{id}", async (string id, HttpRequest request, AdminSessionService sessions, SecurityGroupEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try { await editor.DeleteAsync(id, cancellationToken); return Results.NoContent(); }
        catch (SecurityGroupUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapPost("/api/v1/admin/session", async (HttpRequest request, HttpResponse response, AdminLoginRequest login, AdminSessionService sessions, AdminLoginGuard guard, AuditService audit, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
    {
        var actor = new string((login.Username ?? string.Empty).Where(c => !char.IsControl(c)).Take(128).ToArray());
        var remoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
        var logger = loggerFactory.CreateLogger("RelayLink.Server.AdminLogin");
        if (guard.IsLocked(remoteIp, actor, out var retryAfter))
        {
            response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            try { await audit.RecordAsync(new AuditEvent("admin_login_failure", "denied") { Actor = actor, RemoteIp = remoteIp, ReasonCode = "rate_limited" }, cancellationToken); }
            catch (AuditUnavailableException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!AdminLoginGuard.PasswordWithinLimit(login.Password))
        {
            if (!guard.TryReserve(remoteIp, actor, out retryAfter, out var passwordCapacityExhausted))
                return await LoginThrottled(response, audit, logger, guard, actor, remoteIp, retryAfter, passwordCapacityExhausted, cancellationToken);

            guard.CompleteAttempt(remoteIp, actor, success: false);
            try { await audit.RecordAsync(new AuditEvent("admin_login_failure", "denied") { Actor = actor, RemoteIp = remoteIp, ReasonCode = "password_too_long" }, cancellationToken); }
            catch (AuditUnavailableException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
            return Results.BadRequest(new { error = "Password exceeds the maximum length." });
        }

        if (!guard.TryReserve(remoteIp, actor, out retryAfter, out var capacityExhausted))
            return await LoginThrottled(response, audit, logger, guard, actor, remoteIp, retryAfter, capacityExhausted, cancellationToken);

        if (!sessions.TryPrepareLogin(login.Username, login.Password, out var session))
        {
            guard.CompleteAttempt(remoteIp, actor, success: false);
            try { await audit.RecordAsync(new AuditEvent("admin_login_failure", "denied") { Actor = actor, RemoteIp = remoteIp, ReasonCode = "invalid_credentials" }, cancellationToken); }
            catch (AuditUnavailableException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
            return Results.Unauthorized();
        }
        guard.CompleteAttempt(remoteIp, actor, success: true);
        try { await audit.RecordAsync(new AuditEvent("admin_login_success", "success") { Actor = actor, RemoteIp = remoteIp }, cancellationToken); }
        catch (AuditUnavailableException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
        sessions.CommitLogin(session!);
        response.Cookies.Append(AdminSessionService.CookieName, session!.Id, AdminSessionService.CreateCookieOptions(request.IsHttps, session.ExpiresAtUtc - DateTimeOffset.UtcNow));
        return Results.Ok(new { authenticated = true, csrfToken = session.CsrfToken, expiresAtUtc = session.ExpiresAtUtc });
    });
    app.MapDelete("/api/v1/admin/session", async (HttpRequest request, HttpResponse response, AdminSessionService sessions, AuditService audit, CancellationToken cancellationToken) =>
    {
        if (sessions.TryAuthorize(request, requireCsrf: false, out _))
        {
            try { await audit.RecordAsync(new AuditEvent("admin_logout", "success") { RemoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString() }, cancellationToken); }
            catch (AuditUnavailableException) { /* AuditService logged the fault; logout still revokes the session. */ }
        }
        sessions.Logout(request);
        response.Cookies.Delete(AdminSessionService.CookieName, AdminSessionService.CreateCookieOptions(request.IsHttps));
        return Results.NoContent();
    });
    app.MapGet("/api/v1/admin/audit", async (HttpRequest request, AdminSessionService sessions, AuditService audit, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
        if (!TryPositiveQuery(request, "hours", 24, 24 * 90, out var hours) ||
            !TryPositiveQuery(request, "page", 1, 10000, out var page) ||
            !TryPositiveQuery(request, "pageSize", 50, 100, out var pageSize)) return Results.BadRequest(new { error = "Invalid audit range or pagination." });
        var eventType = request.Query["eventType"].ToString();
        var clientId = request.Query["clientId"].ToString();
        if (eventType.Length > 64 || clientId.Length > 128) return Results.BadRequest(new { error = "Invalid audit filter." });
        var until = DateTimeOffset.UtcNow;
        try { return Results.Ok(await audit.ReadAsync(until.AddHours(-hours), until, string.IsNullOrEmpty(eventType) ? null : eventType, string.IsNullOrEmpty(clientId) ? null : clientId, page, pageSize, cancellationToken)); }
        catch (AuditUnavailableException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
    });
    app.MapGet("/api/v1/overview", (ServerRuntime runtime, MetricsRegistry metrics) =>
    {
        var snapshots = runtime.Configuration.Clients.Values.SelectMany(client => client.Channels.Select(channel => metrics.For(client.ClientId, channel.ChannelId).Snapshot())).ToArray();
        return Results.Ok(new
        {
        serverInstanceId = runtime.InstanceId,
        statsSinceUtc = runtime.StartedAtUtc,
        snapshotTimeUtc = DateTimeOffset.UtcNow,
        clientsOnline = runtime.Sessions.Count,
        clientsTotal = runtime.Configuration.Clients.Count,
        channelsTotal = runtime.Configuration.Clients.Values.Sum(client => client.Channels.Count),
        channelsAvailable = ChannelAvailability.CountAvailablePublicChannels(runtime.Configuration.Clients.Values, runtime.Sessions),
        activeConnections = snapshots.Sum(snapshot => snapshot.ActiveConnections + snapshot.PeerActiveConnections),
        bytesToTarget = snapshots.Sum(snapshot => snapshot.BytesToTarget),
        bytesToCaller = snapshots.Sum(snapshot => snapshot.BytesToCaller),
        peerCiphertextToTarget = snapshots.Sum(snapshot => snapshot.PeerCiphertextToTarget),
        peerCiphertextToCaller = snapshots.Sum(snapshot => snapshot.PeerCiphertextToCaller),
        tcpIdleTimeoutSeconds = runtime.Configuration.Server.Limits.TcpIdleTimeoutSeconds,
        halfCloseDrainTimeoutSeconds = runtime.Configuration.Server.Limits.HalfCloseDrainTimeoutSeconds
        });
    });
    app.MapGet("/api/v1/dashboard/snapshot", (HttpRequest request, ServerRuntime runtime, MetricsRegistry metrics) =>
        GetDashboardSnapshot(request, runtime, metrics));
    app.MapGet("/api/v1/clients", (HttpRequest request, ServerRuntime runtime) =>
    {
        var query = request.Query["query"].ToString();
        var status = request.Query["status"].ToString();
        if (!string.IsNullOrEmpty(status) && status is not ("online" or "offline" or "disabled")) return Results.BadRequest(new { error = "Invalid status." });
        if (!TryPositiveQuery(request, "page", 1, int.MaxValue, out var page) || !TryPositiveQuery(request, "pageSize", 100, 100, out var pageSize)) return Results.BadRequest(new { error = "Invalid page or pageSize." });
        var matches = runtime.Configuration.Clients.Values
            .Where(client => string.IsNullOrWhiteSpace(query) || client.ClientId.Contains(query, StringComparison.OrdinalIgnoreCase) || client.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(client => string.IsNullOrEmpty(status) || (status == "disabled" ? !client.Enabled : runtime.Sessions.TryGet(client.ClientId, out _) == (status == "online")))
            .OrderBy(client => client.ClientId, StringComparer.Ordinal)
            .ToArray();
        var offset = (long)(page - 1) * pageSize;
        var clients = (offset >= matches.Length ? Enumerable.Empty<ClientConfiguration>() : matches.Skip((int)offset))
            .Take(pageSize)
            .Select(client =>
            {
                var online = runtime.Sessions.TryGet(client.ClientId, out var session);
                var heartbeat = session?.Heartbeat;
                return new { clientId = client.ClientId, displayName = client.DisplayName, tags = client.Tags, enabled = client.Enabled, client.E2eCertificateSha256, client.MaxConnections, client.MaxPendingConnections, online, connectedAtUtc = session?.ConnectedAtUtc, lastHeartbeatUtc = heartbeat?.Utc, heartbeatRttMs = heartbeat?.Rtt?.TotalMilliseconds, agentVersion = session?.AgentVersion };
            })
            .ToArray();
        return Results.Ok(new { snapshotTimeUtc = DateTimeOffset.UtcNow, page, pageSize, total = matches.Length, clients });
    });
    app.MapGet("/api/v1/clients/{id}/channels", (string id, ServerRuntime runtime, MetricsRegistry metrics) =>
    {
        if (!runtime.Configuration.Clients.TryGetValue(id, out var client)) return Results.NotFound();
        var online = runtime.Sessions.TryGet(id, out var channelSession);
        return Results.Ok(new
        {
            snapshotTimeUtc = DateTimeOffset.UtcNow,
            channels = client.Channels.Select(channel =>
            {
                var metric = metrics.For(client.ClientId, channel.ChannelId).Snapshot();
                return new { channelId = channel.ChannelId, displayName = channel.DisplayName, tags = channel.Tags, listenAddress = channel.ListenAddress, listenPort = channel.ListenPort, securityGroupId = channel.SecurityGroupId, targetHost = channel.TargetHost, targetPort = channel.TargetPort, enabled = channel.Enabled, authorizedClientsOnly = channel.AuthorizedClientsOnly, endToEndEncryptionEnabled = channel.EndToEndEncryptionEnabled, maxConnections = channel.MaxConnections, targetConnectTimeoutSeconds = channel.TargetConnectTimeoutSeconds, applicationProtocol = channel.ApplicationProtocol, httpState = channelSession?.HttpStates.FirstOrDefault(state => state.ChannelId == channel.ChannelId)?.State, entryScheme = channel.Http is null ? null : channel.AuthorizedClientsOnly ? channel.Http.PeerEntryScheme : channel.Http.PublicEntryScheme, transportProtocol = channel.TransportProtocol, udpIdleTimeoutSeconds = channel.UdpIdleTimeoutSeconds, listenerState = channel.AuthorizedClientsOnly ? "peer-only" : channel.Enabled ? "listening" : "stopped", available = ChannelAvailability.IsPublicChannelAvailable(channel, channelSession), pendingConnections = channel.AuthorizedClientsOnly ? metric.PeerPendingConnections : metric.PendingConnections, activeConnections = channel.AuthorizedClientsOnly ? metric.PeerActiveConnections : metric.ActiveConnections, acceptedTotal = channel.AuthorizedClientsOnly ? metric.PeerAcceptedTotal : metric.AcceptedTotal, openedTotal = channel.AuthorizedClientsOnly ? metric.PeerOpenedTotal : metric.OpenedTotal, openFailedTotal = channel.AuthorizedClientsOnly ? metric.PeerOpenFailedTotal : metric.OpenFailedTotal, metric.NormalClosedTotal, metric.AbortedTotal, metric.BytesToTarget, metric.BytesToCaller, metric.PeerCiphertextToTarget, metric.PeerCiphertextToCaller, udpDroppedTotal = metric.UdpDroppedTotal, targetLastResult = metric.TargetLastResult?.Result, targetLastResultTimeUtc = metric.TargetLastResult?.TimeUtc };
            }).ToArray()
        });
    });
    app.MapGet("/api/v1/clients/{id}/channels/{channelId}/connections", (string id, string channelId, ServerRuntime runtime, ProxyListenerService proxy, PeerRelayRegistry peers) =>
    {
        if (!runtime.Configuration.Clients.TryGetValue(id, out var client) || !client.Channels.Any(channel => channel.ChannelId == channelId)) return Results.NotFound();
        var connections = proxy.ListConnections(id, channelId).Concat(peers.ListConnections(id, channelId)).OrderBy(connection => connection.StartedAtUtc).ToArray();
        return Results.Ok(new { snapshotTimeUtc = DateTimeOffset.UtcNow, connections });
    });
    app.MapDelete("/api/v1/admin/clients/{id}/channels/{channelId}/connections/{connectionId}", (string id, string channelId, string connectionId, HttpRequest request, AdminSessionService sessions, ProxyListenerService proxy, PeerRelayRegistry peers) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        if (!Guid.TryParse(connectionId, out var parsedId)) return Results.NotFound();
        return proxy.TryDisconnect(id, channelId, parsedId) || peers.TryDisconnect(id, channelId, parsedId) ? Results.NoContent() : Results.NotFound();
    });
    app.MapGet("/api/v1/history", async (HttpRequest request, TrafficHistoryService history, CancellationToken cancellationToken) =>
    {
        var clientId = request.Query["clientId"].ToString();
        var channelId = request.Query["channelId"].ToString();
        if (!TryPositiveQuery(request, "hours", 24, 24 * 365, out var hours)) return Results.BadRequest(new { error = "Invalid hours." });
        var samples = await history.ReadAsync(string.IsNullOrEmpty(clientId) ? null : clientId, string.IsNullOrEmpty(channelId) ? null : channelId, hours, cancellationToken);
        return Results.Ok(new { samples });
    });
    app.MapPost("/api/v1/admin/clients", async (ClientCreateRequest create, HttpRequest request, AdminSessionService sessions, ClientConfigurationEditor editor, ServerRuntime runtime, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        var tunnel = runtime.Configuration.Server.Tunnel;
        var agentServerHost = string.IsNullOrWhiteSpace(create.AgentServerHost) ? tunnel.DefaultAgentServerHost : create.AgentServerHost.Trim();
        if (string.IsNullOrWhiteSpace(agentServerHost) || Uri.CheckHostName(agentServerHost) == UriHostNameType.Unknown) return Results.BadRequest(new { error = "A valid Agent server host (without a port) is required." });
        if (tunnel.TlsEnabled && string.IsNullOrWhiteSpace(tunnel.TrustedCaPemPath)) return Results.Problem("Configure tunnel.trustedCaPemPath on the server before creating clients.", statusCode: 409);
        try
        {
            var (client, push) = await editor.CreateAsync(create with { AgentServerHost = agentServerHost }, cancellationToken);
            return Results.Created($"/api/v1/clients/{client.ClientId}", new { clientId = client.ClientId, client.DisplayName, tags = client.Tags, client.Enabled, client.MaxConnections, client.MaxPendingConnections, pushedToAgent = push.PushedToAgent, pushFailureReason = push.FailureReason, message = push.FailureReason is null ? "客户端已创建；请下载并安全部署 Agent 配置文件。" : "客户端已创建，但配置下发失败；客户端重连后会取得最新配置。" });
        }
        catch (ClientUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapPut("/api/v1/admin/clients/{id}", async (string id, ClientUpdateRequest update, HttpRequest request, AdminSessionService sessions, ClientConfigurationEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try
        {
            var (client, push) = await editor.UpdateAsync(id, update, cancellationToken);
            return Results.Ok(new { clientId = client.ClientId, client.DisplayName, tags = client.Tags, client.Enabled, client.MaxConnections, client.MaxPendingConnections, pushedToAgent = push.PushedToAgent, pushFailureReason = push.FailureReason, message = PushMessage(push, "客户端已保存并下发。", "客户端已保存；当前没有可下发的在线会话。", "客户端已保存，但下发失败；重连后会取得最新配置。") });
        }
        catch (ClientUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapDelete("/api/v1/admin/clients/{id}", async (string id, HttpRequest request, AdminSessionService sessions, ClientConfigurationEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try { await editor.DeleteAsync(id, cancellationToken); return Results.NoContent(); }
        catch (ClientUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapGet("/api/v1/admin/clients/{id}/agent-config", (string id, HttpRequest request, AdminSessionService sessions, ServerRuntime runtime) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
        if (!runtime.Configuration.Clients.TryGetValue(id, out var client)) return Results.NotFound();
        var tunnel = runtime.Configuration.Server.Tunnel;
        var serverHost = client.AgentServerHost ?? tunnel.DefaultAgentServerHost;
        if (string.IsNullOrWhiteSpace(serverHost)) return Results.Problem("Configure tunnel.agentServerHost before downloading an Agent configuration.", statusCode: 409);
        if (tunnel.TlsEnabled && string.IsNullOrWhiteSpace(tunnel.TrustedCaPemPath)) return Results.Problem("Configure tunnel.trustedCaPemPath on the server before downloading an Agent configuration.", statusCode: 409);
        return Results.File(AgentConfigurationFactory.Create(runtime.Configuration.Server, client), "application/json", $"relaylink-agent-{client.ClientId}.json");
    });
    app.MapGet("/api/v1/admin/clients/{id}/identity", (string id, HttpRequest request, AdminSessionService sessions, ServerRuntime runtime) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
        if (!runtime.Configuration.Clients.TryGetValue(id, out var client)) return Results.NotFound();
        runtime.Sessions.TryGet(id, out var agentSession);
        return Results.Ok(new { clientId = id, online = agentSession is not null, e2eCertificateSha256 = client.E2eCertificateSha256 });
    });
    app.MapGet("/api/v1/clients/{id}/mappings", (string id, ServerRuntime runtime) => GetMappingStatus(id, runtime));
    app.MapGet("/api/v1/admin/clients/{id}/mappings", (string id, HttpRequest request, AdminSessionService sessions, ServerRuntime runtime) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: false, out _)) return Results.Unauthorized();
        return GetMappingStatus(id, runtime);
    });
    app.MapPost("/api/v1/admin/clients/{id}/mappings", async (string id, MappingCreateRequest create, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try
        {
            var (mapping, push) = await editor.CreateMappingAsync(id, create, cancellationToken);
            return Results.Created($"/api/v1/admin/clients/{id}/mappings", new { mapping.MappingId, mapping.Enabled, localAddress = (string?)null, localPort = (int?)null, available = false, mapping.TargetClientId, mapping.TargetChannelId, pushedToAgent = push.PushedToAgent, pushFailureReason = push.FailureReason, message = PushMessage(push, "端到端访问入口已创建并下发。", "端到端访问入口已创建；客户端离线，将在下次连接时下发。", "端到端访问入口已创建，但下发失败；重连后会取得最新配置。") });
        }
        catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapDelete("/api/v1/admin/clients/{id}/mappings/{mappingId}", async (string id, string mappingId, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try
        {
            var push = await editor.DeleteMappingAsync(id, mappingId, cancellationToken);
            return Results.Ok(new { pushedToAgent = push.PushedToAgent, pushFailureReason = push.FailureReason, message = PushMessage(push, "端到端访问入口已删除并下发。", "端到端访问入口已删除；客户端离线，将在下次连接时同步。", "端到端访问入口已删除，但下发失败；重连后会取得最新配置。") });
        }
        catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapPut("/api/v1/admin/clients/{id}/channels/{channelId}", async (string id, string channelId, ChannelUpdateRequest update, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try
        {
            var (saved, push) = await editor.UpdateAsync(id, channelId, update, cancellationToken);
            return Results.Ok(new { channel = new { saved.ChannelId, saved.DisplayName, tags = saved.Tags, saved.Enabled, saved.ListenAddress, saved.ListenPort, saved.TargetHost, saved.TargetPort, saved.MaxConnections, saved.TargetConnectTimeoutSeconds, saved.TransportProtocol, saved.UdpIdleTimeoutSeconds, saved.AuthorizedClientsOnly, saved.EndToEndEncryptionEnabled }, pushedToAgent = push.PushedToAgent, pushFailureReason = push.FailureReason, message = PushMessage(push, "已保存；运行配置变化已下发，tag 变更仅用于列表筛选。", "已保存；客户端离线时将在下次连接获取运行配置，tag 变更仅用于列表筛选。", "已保存，但下发到在线客户端失败；重连后会取得最新配置。tag 变更仅用于列表筛选。") });
        }
        catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapPost("/api/v1/admin/clients/{id}/channels", async (string id, ChannelCreateRequest create, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try
        {
            var (saved, push) = await editor.CreateAsync(id, create, cancellationToken);
            return Results.Created($"/api/v1/clients/{id}/channels", new { channel = new { saved.ChannelId, saved.DisplayName, tags = saved.Tags, saved.Enabled, saved.ListenAddress, saved.ListenPort, saved.TargetHost, saved.TargetPort, saved.MaxConnections, saved.TargetConnectTimeoutSeconds, saved.TransportProtocol, saved.UdpIdleTimeoutSeconds, saved.AuthorizedClientsOnly, saved.EndToEndEncryptionEnabled }, pushedToAgent = push.PushedToAgent, pushFailureReason = push.FailureReason, message = PushMessage(push, "已新增并下发给在线客户端；服务端监听已更新。", "已新增并更新服务端监听；客户端离线，将在下次连接时下发。", "已新增并更新服务端监听，但下发失败；客户端重连后会取得最新配置。") });
        }
        catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });
    app.MapDelete("/api/v1/admin/clients/{id}/channels/{channelId}", async (string id, string channelId, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, CancellationToken cancellationToken) =>
    {
        if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
        try
        {
            var push = await editor.DeleteAsync(id, channelId, cancellationToken);
            return Results.Ok(new { pushedToAgent = push.PushedToAgent, pushFailureReason = push.FailureReason, message = PushMessage(push, "已删除并下发；服务端监听已停止，该通道原有连接已断开。", "已删除并停止服务端监听；该通道原有连接已断开，客户端将在下次连接时获取配置。", "已删除并停止服务端监听，但下发失败；客户端重连后会取得最新配置。") });
        }
        catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
    });

    await app.RunAsync();
    return 0;
}
catch (Exception exception)
{
    logger!.Fatal(exception, "Server host terminated unexpectedly.");
    return 1;
}

static string PushMessage(AgentPushReport push, string sent, string offline, string failed) =>
    push.FailureReason is not null ? failed : push.PushedToAgent ? sent : offline;

static async Task<IResult> LoginThrottled(HttpResponse response, AuditService audit, Microsoft.Extensions.Logging.ILogger logger, AdminLoginGuard guard, string actor, string? remoteIp, TimeSpan retryAfter, bool capacityExhausted, CancellationToken cancellationToken)
{
    response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
    var reason = capacityExhausted ? "login_state_full" : "rate_limited";
    if (capacityExhausted)
        logger.LogWarning("Admin login state table is full ({TrackedStates} addresses). Refusing a new address until an unexpired entry is idle. Direct connection address {RemoteIp}; forwarding headers are not used.", guard.TrackedStateCount, remoteIp);
    try { await audit.RecordAsync(new AuditEvent("admin_login_failure", "denied") { Actor = actor, RemoteIp = remoteIp, ReasonCode = reason }, cancellationToken); }
    catch (AuditUnavailableException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
    return capacityExhausted
        ? Results.Problem("Admin login is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.StatusCode(StatusCodes.Status429TooManyRequests);
}

static IResult GetMappingStatus(string id, ServerRuntime runtime)
{
    if (!runtime.Configuration.Clients.TryGetValue(id, out var client)) return Results.NotFound();
    runtime.Sessions.TryGet(id, out var agentSession);
    return Results.Ok(new { mappings = client.OutboundMappings.Select(mapping =>
    {
        var address = agentSession?.PeerAddresses.FirstOrDefault(item => item.MappingId == mapping.MappingId);
        var target = runtime.Configuration.Clients.GetValueOrDefault(mapping.TargetClientId)?.Channels.SingleOrDefault(c => c.ChannelId == mapping.TargetChannelId);
        runtime.Sessions.TryGet(mapping.TargetClientId, out var targetSession);
        return new { mapping.MappingId, mapping.Enabled, localAddress = address?.LocalAddress, localPort = address?.LocalPort, available = ChannelAvailability.IsOutboundMappingAvailable(mapping, address, target, agentSession, targetSession), applicationProtocol = target?.ApplicationProtocol ?? "raw", accessUrl = address is not null && target?.Http is not null ? $"{target.Http.PeerEntryScheme}://{target.Http.PeerAuthorityHost}:{address.LocalPort}" : null, mapping.TargetClientId, mapping.TargetChannelId, transportProtocol = runtime.Configuration.Clients.GetValueOrDefault(mapping.TargetClientId)?.Channels.SingleOrDefault(c => c.ChannelId == mapping.TargetChannelId)?.TransportProtocol ?? "tcp" };
    }).ToArray() });
}

static IResult GetDashboardSnapshot(HttpRequest request, ServerRuntime runtime, MetricsRegistry metrics)
{
    if (!TryPositiveQuery(request, "page", 1, int.MaxValue, out var page) ||
        !TryPositiveQuery(request, "pageSize", 100, 100, out var pageSize))
        return Results.BadRequest(new { error = "Invalid page or pageSize." });

    var snapshotTimeUtc = DateTimeOffset.UtcNow;
    var allClients = runtime.Configuration.Clients.Values
        .OrderBy(client => client.ClientId, StringComparer.Ordinal)
        .ToArray();
    var offset = (long)(page - 1) * pageSize;
    var pageClients = (offset >= allClients.Length ? Enumerable.Empty<ClientConfiguration>() : allClients.Skip((int)offset))
        .Take(pageSize)
        .Select(client =>
        {
            var online = runtime.Sessions.TryGet(client.ClientId, out var session);
            var heartbeat = session?.Heartbeat;
            var channels = client.Channels.Select(channel =>
            {
                var metric = metrics.For(client.ClientId, channel.ChannelId).Snapshot();
                return new
                {
                    channelId = channel.ChannelId,
                    displayName = channel.DisplayName,
                    tags = channel.Tags,
                    listenAddress = channel.ListenAddress,
                    listenPort = channel.ListenPort,
                    applicationProtocol = channel.ApplicationProtocol,
                    httpState = session?.HttpStates.FirstOrDefault(state => state.ChannelId == channel.ChannelId)?.State,
                    entryScheme = channel.Http is null ? null : channel.AuthorizedClientsOnly ? channel.Http.PeerEntryScheme : channel.Http.PublicEntryScheme,
                    transportProtocol = channel.TransportProtocol,
                    udpIdleTimeoutSeconds = channel.UdpIdleTimeoutSeconds,
                    securityGroupId = channel.SecurityGroupId,
                    targetHost = channel.TargetHost,
                    targetPort = channel.TargetPort,
                    enabled = channel.Enabled,
                    authorizedClientsOnly = channel.AuthorizedClientsOnly,
                    endToEndEncryptionEnabled = channel.EndToEndEncryptionEnabled,
                    maxConnections = channel.MaxConnections,
                    targetConnectTimeoutSeconds = channel.TargetConnectTimeoutSeconds,
                    listenerState = channel.AuthorizedClientsOnly ? "peer-only" : channel.Enabled ? "listening" : "stopped",
                    available = ChannelAvailability.IsPublicChannelAvailable(channel, session),
                    pendingConnections = channel.AuthorizedClientsOnly ? metric.PeerPendingConnections : metric.PendingConnections,
                    activeConnections = channel.AuthorizedClientsOnly ? metric.PeerActiveConnections : metric.ActiveConnections,
                    acceptedTotal = channel.AuthorizedClientsOnly ? metric.PeerAcceptedTotal : metric.AcceptedTotal,
                    openedTotal = channel.AuthorizedClientsOnly ? metric.PeerOpenedTotal : metric.OpenedTotal,
                    openFailedTotal = channel.AuthorizedClientsOnly ? metric.PeerOpenFailedTotal : metric.OpenFailedTotal,
                    metric.NormalClosedTotal,
                    metric.AbortedTotal,
                    metric.BytesToTarget,
                    metric.BytesToCaller,
                    metric.PeerCiphertextToTarget,
                    metric.PeerCiphertextToCaller,
                    udpDroppedTotal = metric.UdpDroppedTotal,
                    targetLastResult = metric.TargetLastResult?.Result,
                    targetLastResultTimeUtc = metric.TargetLastResult?.TimeUtc
                };
            }).ToArray();
            var mappings = client.OutboundMappings.Select(mapping =>
            {
                var address = session?.PeerAddresses.FirstOrDefault(item => item.MappingId == mapping.MappingId);
                var target = runtime.Configuration.Clients.GetValueOrDefault(mapping.TargetClientId)?.Channels.SingleOrDefault(c => c.ChannelId == mapping.TargetChannelId);
                runtime.Sessions.TryGet(mapping.TargetClientId, out var targetSession);
                return new
                {
                    mapping.MappingId,
                    mapping.Enabled,
                    localAddress = address?.LocalAddress,
                    localPort = address?.LocalPort,
                    available = ChannelAvailability.IsOutboundMappingAvailable(mapping, address, target, session, targetSession),
                    applicationProtocol = target?.ApplicationProtocol ?? "raw",
                    accessUrl = address is not null && target?.Http is not null ? $"{target.Http.PeerEntryScheme}://{target.Http.PeerAuthorityHost}:{address.LocalPort}" : null,
                    mapping.TargetClientId,
                    mapping.TargetChannelId,
                    transportProtocol = runtime.Configuration.Clients.GetValueOrDefault(mapping.TargetClientId)?.Channels.SingleOrDefault(c => c.ChannelId == mapping.TargetChannelId)?.TransportProtocol ?? "tcp"
                };
            }).ToArray();
            return new
            {
                clientId = client.ClientId,
                displayName = client.DisplayName,
                tags = client.Tags,
                enabled = client.Enabled,
                client.E2eCertificateSha256,
                client.MaxConnections,
                client.MaxPendingConnections,
                online,
                connectedAtUtc = session?.ConnectedAtUtc,
                lastHeartbeatUtc = heartbeat?.Utc,
                heartbeatRttMs = heartbeat?.Rtt?.TotalMilliseconds,
                agentVersion = session?.AgentVersion,
                channels,
                mappings
            };
        }).ToArray();
    var channelMetrics = allClients
        .SelectMany(client => client.Channels.Select(channel => metrics.For(client.ClientId, channel.ChannelId).Snapshot()))
        .ToArray();
    var overview = new
    {
        serverInstanceId = runtime.InstanceId,
        statsSinceUtc = runtime.StartedAtUtc,
        snapshotTimeUtc,
        clientsOnline = runtime.Sessions.Count,
        clientsTotal = allClients.Length,
        channelsTotal = allClients.Sum(client => client.Channels.Count),
        channelsAvailable = ChannelAvailability.CountAvailablePublicChannels(allClients, runtime.Sessions),
        activeConnections = channelMetrics.Sum(snapshot => snapshot.ActiveConnections + snapshot.PeerActiveConnections),
        bytesToTarget = channelMetrics.Sum(snapshot => snapshot.BytesToTarget),
        bytesToCaller = channelMetrics.Sum(snapshot => snapshot.BytesToCaller),
        peerCiphertextToTarget = channelMetrics.Sum(snapshot => snapshot.PeerCiphertextToTarget),
        peerCiphertextToCaller = channelMetrics.Sum(snapshot => snapshot.PeerCiphertextToCaller),
        tcpIdleTimeoutSeconds = runtime.Configuration.Server.Limits.TcpIdleTimeoutSeconds,
        halfCloseDrainTimeoutSeconds = runtime.Configuration.Server.Limits.HalfCloseDrainTimeoutSeconds
    };
    return Results.Ok(new { snapshotTimeUtc, page, pageSize, total = allClients.Length, overview, clients = pageClients });
}

static (string ConfigurationPath, bool CheckOnly, string? LogDirectory) ParseArguments(string[] arguments)
{
    string? path = null;
    var checkOnly = false;
    string? logDirectory = null;
    for (var index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--config" when index + 1 < arguments.Length:
                path = arguments[++index];
                break;
            case "--check-config":
                checkOnly = true;
                break;
            case "--log-directory" when index + 1 < arguments.Length:
                logDirectory = Path.GetFullPath(arguments[++index]);
                break;
            default:
                Console.Error.WriteLine("Usage: RelayLink.Server --config <server.json> [--check-config] [--log-directory <directory>]");
                Environment.Exit(2);
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(path))
    {
        Console.Error.WriteLine("--config is required.");
        Environment.Exit(2);
    }

    return (path!, checkOnly, logDirectory);
}

static bool TryPositiveQuery(HttpRequest request, string name, int defaultValue, int maximum, out int value)
{
    var raw = request.Query[name].ToString();
    if (string.IsNullOrEmpty(raw))
    {
        value = defaultValue;
        return true;
    }

    return int.TryParse(raw, out value) && value > 0 && value <= maximum;
}

sealed record AdminLoginRequest(string Username, string Password);
