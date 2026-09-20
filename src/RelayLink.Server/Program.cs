using System.Net;
using Microsoft.Data.Sqlite;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

var parsed = ParseArguments(args);
var loader = new ConfigurationLoader();
LoadedConfiguration loaded;
try
{
    loaded = loader.Load(parsed.ConfigurationPath);
}
catch (ConfigurationException exception)
{
    Console.Error.WriteLine($"Configuration validation failed: {exception.Message}");
    return 2;
}

if (parsed.CheckOnly)
{
    Console.WriteLine("Configuration validation succeeded.");
    return 0;
}

var options = new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory };
var builder = WebApplication.CreateBuilder(options);
builder.Host.UseWindowsService();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(console => console.IncludeScopes = true);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(40));
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Parse(loaded.Server.Dashboard.ListenAddress), loaded.Server.Dashboard.Port));
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
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next(context);
});

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
    var port = ChannelPortSuggestion.Find(runtime.Configuration);
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
app.MapPost("/api/v1/admin/session", async (HttpRequest request, HttpResponse response, AdminLoginRequest login, AdminSessionService sessions, AuditService audit, CancellationToken cancellationToken) =>
{
    var actor = new string((login.Username ?? string.Empty).Where(c => !char.IsControl(c)).Take(128).ToArray());
    var remoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString();
    if (!sessions.TryLogin(login.Username, login.Password, out var session))
    {
        try { await audit.RecordAsync(new AuditEvent("admin_login_failure", "denied") { Actor = actor, RemoteIp = remoteIp, ReasonCode = "invalid_credentials" }, cancellationToken); }
        catch (AuditUnavailableException) { return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
        return Results.Unauthorized();
    }
    try { await audit.RecordAsync(new AuditEvent("admin_login_success", "success") { Actor = actor, RemoteIp = remoteIp }, cancellationToken); }
    catch (AuditUnavailableException) { sessions.Revoke(session!); return Results.Problem("Audit storage is unavailable.", statusCode: 503); }
    response.Cookies.Append(AdminSessionService.CookieName, session!.Id, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = request.IsHttps,
        MaxAge = session.ExpiresAtUtc - DateTimeOffset.UtcNow,
        Path = "/"
    });
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
    response.Cookies.Delete(AdminSessionService.CookieName, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = request.IsHttps, Path = "/" });
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
    channelsAvailable = runtime.Configuration.Clients.Values.Sum(client => client.Channels.Count(channel => channel.Enabled && !channel.AuthorizedClientsOnly && runtime.Sessions.TryGet(client.ClientId, out _))),
    activeConnections = snapshots.Sum(snapshot => snapshot.ActiveConnections + snapshot.PeerActiveConnections),
    bytesToTarget = snapshots.Sum(snapshot => snapshot.BytesToTarget),
    bytesToCaller = snapshots.Sum(snapshot => snapshot.BytesToCaller),
    peerCiphertextToTarget = snapshots.Sum(snapshot => snapshot.PeerCiphertextToTarget),
    peerCiphertextToCaller = snapshots.Sum(snapshot => snapshot.PeerCiphertextToCaller)
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
        .Select(client => new { clientId = client.ClientId, displayName = client.DisplayName, enabled = client.Enabled, client.E2eCertificateSha256, client.MaxConnections, client.MaxPendingConnections, online = runtime.Sessions.TryGet(client.ClientId, out var session), connectedAtUtc = session?.ConnectedAtUtc, lastHeartbeatUtc = session?.LastHeartbeatUtc, heartbeatRttMs = session?.LastHeartbeatRtt?.TotalMilliseconds, agentVersion = session?.AgentVersion })
        .ToArray();
    return Results.Ok(new { snapshotTimeUtc = DateTimeOffset.UtcNow, page, pageSize, total = matches.Length, clients });
});
app.MapGet("/api/v1/clients/{id}/channels", (string id, ServerRuntime runtime, MetricsRegistry metrics) =>
{
    if (!runtime.Configuration.Clients.TryGetValue(id, out var client)) return Results.NotFound();
    var online = runtime.Sessions.TryGet(id, out _);
    return Results.Ok(new
    {
        snapshotTimeUtc = DateTimeOffset.UtcNow,
        channels = client.Channels.Select(channel =>
        {
            var metric = metrics.For(client.ClientId, channel.ChannelId).Snapshot();
            return new { channelId = channel.ChannelId, displayName = channel.DisplayName, listenAddress = channel.ListenAddress, listenPort = channel.ListenPort, securityGroupId = channel.SecurityGroupId, targetHost = channel.TargetHost, targetPort = channel.TargetPort, enabled = channel.Enabled, authorizedClientsOnly = channel.AuthorizedClientsOnly, maxConnections = channel.MaxConnections, targetConnectTimeoutSeconds = channel.TargetConnectTimeoutSeconds, listenerState = channel.AuthorizedClientsOnly ? "peer-only" : channel.Enabled ? "listening" : "stopped", available = channel.Enabled && !channel.AuthorizedClientsOnly && online, pendingConnections = channel.AuthorizedClientsOnly ? metric.PeerPendingConnections : metric.PendingConnections, activeConnections = channel.AuthorizedClientsOnly ? metric.PeerActiveConnections : metric.ActiveConnections, acceptedTotal = channel.AuthorizedClientsOnly ? metric.PeerAcceptedTotal : metric.AcceptedTotal, openedTotal = channel.AuthorizedClientsOnly ? metric.PeerOpenedTotal : metric.OpenedTotal, openFailedTotal = channel.AuthorizedClientsOnly ? metric.PeerOpenFailedTotal : metric.OpenFailedTotal, metric.NormalClosedTotal, metric.AbortedTotal, metric.BytesToTarget, metric.BytesToCaller, metric.PeerCiphertextToTarget, metric.PeerCiphertextToCaller, targetLastResult = metric.TargetLastResult?.Result, targetLastResultTimeUtc = metric.TargetLastResult?.TimeUtc };
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
        var client = await editor.CreateAsync(create with { AgentServerHost = agentServerHost }, cancellationToken);
        return Results.Created($"/api/v1/clients/{client.ClientId}", new { clientId = client.ClientId, client.DisplayName, client.Enabled, client.MaxConnections, client.MaxPendingConnections, message = "客户端已创建；请下载并安全部署 Agent 配置文件。" });
    }
    catch (ClientUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPut("/api/v1/admin/clients/{id}", async (string id, ClientUpdateRequest update, HttpRequest request, AdminSessionService sessions, ClientConfigurationEditor editor, CancellationToken cancellationToken) =>
{
    if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
    try { var client = await editor.UpdateAsync(id, update, cancellationToken); return Results.Ok(new { clientId = client.ClientId, client.DisplayName, client.Enabled, client.MaxConnections, client.MaxPendingConnections }); }
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
    try { var mapping = await editor.CreateMappingAsync(id, create, cancellationToken); return Results.Created($"/api/v1/admin/clients/{id}/mappings", new { mapping.MappingId, mapping.Enabled, localAddress = (string?)null, localPort = (int?)null, available = false, mapping.TargetClientId, mapping.TargetChannelId }); }
    catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapDelete("/api/v1/admin/clients/{id}/mappings/{mappingId}", async (string id, string mappingId, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, CancellationToken cancellationToken) =>
{
    if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
    try { await editor.DeleteMappingAsync(id, mappingId, cancellationToken); return Results.NoContent(); }
    catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPut("/api/v1/admin/clients/{id}/channels/{channelId}", async (string id, string channelId, ChannelUpdateRequest update, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, ServerRuntime runtime, CancellationToken cancellationToken) =>
{
    if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
    try
    {
        var saved = await editor.UpdateAsync(id, channelId, update, cancellationToken);
        var pushed = runtime.Sessions.TryGet(id, out _);
        return Results.Ok(new { channel = new { saved.ChannelId, saved.DisplayName, saved.Enabled, saved.ListenAddress, saved.ListenPort, saved.TargetHost, saved.TargetPort, saved.MaxConnections, saved.TargetConnectTimeoutSeconds, saved.AuthorizedClientsOnly }, pushedToAgent = pushed, message = pushed ? "已保存并下发；若通道配置发生变化，该通道原有连接已断开。" : "已保存；若通道配置发生变化，该通道原有连接已断开，客户端将在下次连接时获取配置。" });
    }
    catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapPost("/api/v1/admin/clients/{id}/channels", async (string id, ChannelCreateRequest create, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, ServerRuntime runtime, CancellationToken cancellationToken) =>
{
    if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
    try
    {
        var saved = await editor.CreateAsync(id, create, cancellationToken);
        var pushed = runtime.Sessions.TryGet(id, out _);
        return Results.Created($"/api/v1/clients/{id}/channels", new { channel = new { saved.ChannelId, saved.DisplayName, saved.Enabled, saved.ListenAddress, saved.ListenPort, saved.TargetHost, saved.TargetPort, saved.MaxConnections, saved.TargetConnectTimeoutSeconds, saved.AuthorizedClientsOnly }, pushedToAgent = pushed, message = pushed ? "已新增并下发给在线客户端；服务端监听已更新。" : "已新增并更新服务端监听；客户端离线，将在下次连接时下发。" });
    }
    catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapDelete("/api/v1/admin/clients/{id}/channels/{channelId}", async (string id, string channelId, HttpRequest request, AdminSessionService sessions, ChannelConfigurationEditor editor, ServerRuntime runtime, CancellationToken cancellationToken) =>
{
    if (!sessions.TryAuthorize(request, requireCsrf: true, out _)) return Results.Unauthorized();
    try
    {
        await editor.DeleteAsync(id, channelId, cancellationToken);
        var pushed = runtime.Sessions.TryGet(id, out _);
        return Results.Ok(new { pushedToAgent = pushed, message = pushed ? "已删除并下发；服务端监听已停止，该通道原有连接已断开。" : "已删除并停止服务端监听；该通道原有连接已断开，客户端将在下次连接时获取配置。" });
    }
    catch (ChannelUpdateException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

await app.RunAsync();
return 0;

static IResult GetMappingStatus(string id, ServerRuntime runtime)
{
    if (!runtime.Configuration.Clients.TryGetValue(id, out var client)) return Results.NotFound();
    runtime.Sessions.TryGet(id, out var agentSession);
    return Results.Ok(new { mappings = client.OutboundMappings.Select(mapping =>
    {
        var address = agentSession?.PeerAddresses.FirstOrDefault(item => item.MappingId == mapping.MappingId);
        return new { mapping.MappingId, mapping.Enabled, localAddress = address?.LocalAddress, localPort = address?.LocalPort, available = mapping.Enabled && address is not null, mapping.TargetClientId, mapping.TargetChannelId };
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
            var channels = client.Channels.Select(channel =>
            {
                var metric = metrics.For(client.ClientId, channel.ChannelId).Snapshot();
                return new
                {
                    channelId = channel.ChannelId,
                    displayName = channel.DisplayName,
                    listenAddress = channel.ListenAddress,
                    listenPort = channel.ListenPort,
                    securityGroupId = channel.SecurityGroupId,
                    targetHost = channel.TargetHost,
                    targetPort = channel.TargetPort,
                    enabled = channel.Enabled,
                    authorizedClientsOnly = channel.AuthorizedClientsOnly,
                    maxConnections = channel.MaxConnections,
                    targetConnectTimeoutSeconds = channel.TargetConnectTimeoutSeconds,
                    listenerState = channel.AuthorizedClientsOnly ? "peer-only" : channel.Enabled ? "listening" : "stopped",
                    available = channel.Enabled && !channel.AuthorizedClientsOnly && online,
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
                    targetLastResult = metric.TargetLastResult?.Result,
                    targetLastResultTimeUtc = metric.TargetLastResult?.TimeUtc
                };
            }).ToArray();
            var mappings = client.OutboundMappings.Select(mapping =>
            {
                var address = session?.PeerAddresses.FirstOrDefault(item => item.MappingId == mapping.MappingId);
                return new
                {
                    mapping.MappingId,
                    mapping.Enabled,
                    localAddress = address?.LocalAddress,
                    localPort = address?.LocalPort,
                    available = mapping.Enabled && address is not null,
                    mapping.TargetClientId,
                    mapping.TargetChannelId
                };
            }).ToArray();
            return new
            {
                clientId = client.ClientId,
                displayName = client.DisplayName,
                enabled = client.Enabled,
                client.E2eCertificateSha256,
                client.MaxConnections,
                client.MaxPendingConnections,
                online,
                connectedAtUtc = session?.ConnectedAtUtc,
                lastHeartbeatUtc = session?.LastHeartbeatUtc,
                heartbeatRttMs = session?.LastHeartbeatRtt?.TotalMilliseconds,
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
        channelsAvailable = allClients.Sum(client => client.Channels.Count(channel =>
            channel.Enabled && !channel.AuthorizedClientsOnly && runtime.Sessions.TryGet(client.ClientId, out _))),
        activeConnections = channelMetrics.Sum(snapshot => snapshot.ActiveConnections + snapshot.PeerActiveConnections),
        bytesToTarget = channelMetrics.Sum(snapshot => snapshot.BytesToTarget),
        bytesToCaller = channelMetrics.Sum(snapshot => snapshot.BytesToCaller),
        peerCiphertextToTarget = channelMetrics.Sum(snapshot => snapshot.PeerCiphertextToTarget),
        peerCiphertextToCaller = channelMetrics.Sum(snapshot => snapshot.PeerCiphertextToCaller)
    };
    return Results.Ok(new { snapshotTimeUtc, page, pageSize, total = allClients.Length, overview, clients = pageClients });
}

static (string ConfigurationPath, bool CheckOnly) ParseArguments(string[] arguments)
{
    string? path = null;
    var checkOnly = false;
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
            default:
                Console.Error.WriteLine("Usage: RelayLink.Server --config <server.json> [--check-config]");
                Environment.Exit(2);
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(path))
    {
        Console.Error.WriteLine("--config is required.");
        Environment.Exit(2);
    }

    return (path!, checkOnly);
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
