using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayLink.Protocol;

namespace RelayLink.Agent;

public sealed class AgentStatus(AgentConfiguration configuration)
{
    private AgentStatusSnapshot current = new(configuration.ClientId, false, null, [], []);
    public AgentStatusSnapshot Snapshot => Volatile.Read(ref current);

    public void SetOnline(ClientConfigSnapshot configurationSnapshot, IReadOnlyList<PeerMappingAddress> addresses)
    {
        var byId = addresses.ToDictionary(address => address.MappingId, StringComparer.Ordinal);
        var channels = configurationSnapshot.Channels.Select(channel => new AgentChannelView(
            channel.ChannelId, channel.DisplayName, channel.Enabled, channel.AuthorizedClientsOnly,
            channel.EndToEndEncryptionEnabled, channel.TargetHost, channel.TargetPort)).ToArray();
        var mappings = configurationSnapshot.OutboundMappings.Select(mapping => new AgentMappingView(
            mapping.MappingId, mapping.Enabled, mapping.TargetClientId, mapping.TargetChannelId,
            byId.TryGetValue(mapping.MappingId, out var address) ? $"127.0.0.1:{address.LocalPort}" : null)).ToArray();
        Volatile.Write(ref current, new AgentStatusSnapshot(configuration.ClientId, true, DateTimeOffset.UtcNow, channels, mappings));
    }

    public void SetOffline() => Volatile.Write(ref current, new AgentStatusSnapshot(configuration.ClientId, false, null, [], []));
}

public sealed record AgentStatusSnapshot(string ClientId, bool Online, DateTimeOffset? UpdatedAtUtc, IReadOnlyList<AgentChannelView> Channels, IReadOnlyList<AgentMappingView> OutboundMappings);
public sealed record AgentChannelView(string ChannelId, string DisplayName, bool Enabled, bool AuthorizedClientsOnly, bool EndToEndEncryptionEnabled, string TargetHost, int TargetPort);
public sealed record AgentMappingView(string MappingId, bool Enabled, string TargetClientId, string TargetChannelId, string? LocalAddress);

public sealed class AgentLocalDashboard : BackgroundService
{
    private readonly int dashboardPort;
    private readonly Func<IReadOnlyList<AgentServerRuntimeSnapshot>> snapshots;
    private readonly AgentProcessRuntime? runtime;
    private readonly AgentLocalWriteSession writeSession;
    private static readonly JsonSerializerOptions AdminJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private string AdminCookie => $"relaylink-agent-local-write-{dashboardPort}";

    public AgentLocalDashboard(AgentConfiguration configuration, AgentStatus status)
    {
        writeSession = new AgentLocalWriteSession();
        dashboardPort = configuration.DashboardPort;
        snapshots = () => [new AgentServerRuntimeSnapshot(configuration.ProfileId, configuration.ServerHost, configuration.UseTls, true, status.Snapshot)];
    }

    public AgentLocalDashboard(AgentProcessConfiguration configuration, AgentProcessRuntime runtime, AgentLocalWriteSession? writeSession = null)
    {
        dashboardPort = configuration.DashboardPort;
        snapshots = () => runtime.Snapshot;
        this.runtime = runtime;
        this.writeSession = writeSession ?? new AgentLocalWriteSession();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (dashboardPort == 0) return;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, dashboardPort);
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var expectedOrigin = $"http://127.0.0.1:{dashboardPort}";
            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != dashboardPort)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if ((!string.IsNullOrEmpty(origin) && !string.Equals(origin, expectedOrigin, StringComparison.Ordinal)) ||
                (context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE" && !string.Equals(origin, expectedOrigin, StringComparison.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        IResult Single(Func<AgentStatusSnapshot, object> response)
        {
            var groups = snapshots();
            return groups.Count == 1 ? Results.Ok(response(groups[0].Status)) : Results.Conflict(new { message = "Use the grouped Agent v2 API.", statusPath = "/api/v2/status" });
        }
        app.MapGet("/api/v1/status", () => Single(snapshot => snapshot));
        app.MapGet("/api/v1/channels", () =>
            Single(snapshot => new AgentChannelsResponse(snapshot.ClientId, snapshot.Online, snapshot.UpdatedAtUtc, snapshot.Channels)));
        app.MapGet("/api/v1/mappings", () =>
            Single(snapshot => new AgentMappingsResponse(snapshot.ClientId, snapshot.Online, snapshot.UpdatedAtUtc, snapshot.OutboundMappings)));
        app.MapGet("/api/v2/status", () => Results.Ok(new { servers = snapshots().Select(group => new
        {
            group.ProfileId, group.ServerHost, group.UseTls, group.Enabled, group.Status.ClientId, group.Status.Online, group.Status.UpdatedAtUtc,
            group.PendingConnections, group.ActiveConnections, group.CapacityRejected,
            group.Status.Channels, mappings = group.Status.OutboundMappings
        }).ToArray() }));
        app.MapGet("/api/v2/channels", () => Results.Ok(new { servers = snapshots().Select(group => new
        {
            group.ProfileId, group.Enabled, group.Status.ClientId, group.Status.Online, group.Status.UpdatedAtUtc, group.Status.Channels
        }).ToArray() }));
        app.MapGet("/api/v2/mappings", () => Results.Ok(new { servers = snapshots().Select(group => new
        {
            group.ProfileId, group.Enabled, group.Status.ClientId, group.Status.Online, group.Status.UpdatedAtUtc, mappings = group.Status.OutboundMappings
        }).ToArray() }));
        // Kept for compatibility with builds that exposed the original unversioned aggregate endpoint.
        app.MapGet("/api/status", () => Single(snapshot => snapshot));
        if (runtime is not null)
        {
            app.MapGet("/api/v2/admin/session", (HttpContext context) =>
            {
                var cookie = context.Request.Cookies[AdminCookie];
                var session = writeSession.GetOrCreate(cookie);
                if (cookie != session.Token)
                    context.Response.Cookies.Append(AdminCookie, session.Token, new CookieOptions
                    {
                        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = false,
                        IsEssential = true, Path = "/api/v2/admin", Expires = session.ExpiresAtUtc
                    });
                return Results.Ok(new
                {
                    csrfToken = session.CsrfToken,
                    version = runtime.ConfigurationVersion
                });
            });
            app.MapDelete("/api/v2/admin/session", (HttpContext context) =>
            {
                var rejection = RequireWriteSession(context);
                if (rejection is not null) return rejection;
                writeSession.Remove(context.Request.Cookies[AdminCookie]);
                context.Response.Cookies.Delete(AdminCookie, new CookieOptions { Path = "/api/v2/admin" });
                return Results.NoContent();
            });
            app.MapPost("/api/v2/admin/servers", async (HttpContext context) =>
            {
                var rejection = RequireWriteSession(context);
                if (rejection is not null) return rejection;
                AgentAddProfileRequest? request;
                try { request = await JsonSerializer.DeserializeAsync<AgentAddProfileRequest>(context.Request.Body, AdminJsonOptions, context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(new { message = "Invalid server profile request." }); }
                if (request?.Server is null || string.IsNullOrWhiteSpace(request.Version)) return Results.BadRequest(new { message = "Invalid server profile request." });
                try
                {
                    var version = await runtime.AddAsync(request.Server, request.Version, request.ConfirmExistingState, context.RequestAborted);
                    return Results.Ok(new { version, profileId = request.Server.ProfileId });
                }
                catch (AgentProfileMutationException exception) { return MutationError(exception); }
                catch (AgentConfigurationException) { return Results.BadRequest(new { message = "Invalid server profile or process limits." }); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Results.Problem("Unable to save Agent configuration.", statusCode: 500); }
            });
            app.MapDelete("/api/v2/admin/servers/{profileId}", async (string profileId, HttpContext context) =>
            {
                var rejection = RequireWriteSession(context);
                if (rejection is not null) return rejection;
                AgentRemoveProfileRequest? request;
                try { request = await JsonSerializer.DeserializeAsync<AgentRemoveProfileRequest>(context.Request.Body, AdminJsonOptions, context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(new { message = "Invalid delete request." }); }
                if (string.IsNullOrWhiteSpace(request?.Version)) return Results.BadRequest(new { message = "Invalid delete request." });
                try
                {
                    var version = await runtime.RemoveAsync(profileId, request.Version, context.RequestAborted);
                    return Results.Ok(new { version });
                }
                catch (AgentProfileMutationException exception) { return MutationError(exception); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Results.Problem("Unable to save Agent configuration.", statusCode: 500); }
            });
            app.MapPut("/api/v2/admin/servers/{profileId}", async (string profileId, HttpContext context) =>
            {
                var rejection = RequireWriteSession(context);
                if (rejection is not null) return rejection;
                AgentUpdateProfileRequest? request;
                try { request = await JsonSerializer.DeserializeAsync<AgentUpdateProfileRequest>(context.Request.Body, AdminJsonOptions, context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(new { message = "Invalid server profile request." }); }
                if (request?.Server is null || string.IsNullOrWhiteSpace(request.Version)) return Results.BadRequest(new { message = "Invalid server profile request." });
                try
                {
                    var version = await runtime.UpdateAsync(profileId, request.Server, request.Version, context.RequestAborted);
                    return Results.Ok(new { version, profileId });
                }
                catch (AgentProfileMutationException exception) { return MutationError(exception); }
                catch (AgentConfigurationException) { return Results.BadRequest(new { message = "Invalid server profile or process limits." }); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Results.Problem("Unable to save Agent configuration.", statusCode: 500); }
            });
            app.MapPut("/api/v2/admin/servers/{profileId}/enabled", async (string profileId, HttpContext context) =>
            {
                var rejection = RequireWriteSession(context);
                if (rejection is not null) return rejection;
                AgentSetProfileEnabledRequest? request;
                try { request = await JsonSerializer.DeserializeAsync<AgentSetProfileEnabledRequest>(context.Request.Body, AdminJsonOptions, context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(new { message = "Invalid profile state request." }); }
                if (request?.Enabled is null || string.IsNullOrWhiteSpace(request.Version)) return Results.BadRequest(new { message = "Invalid profile state request." });
                try
                {
                    var version = await runtime.SetEnabledAsync(profileId, request.Enabled.Value, request.Version, context.RequestAborted);
                    return Results.Ok(new { version, profileId, enabled = request.Enabled.Value });
                }
                catch (AgentProfileMutationException exception) { return MutationError(exception); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Results.Problem("Unable to save Agent configuration.", statusCode: 500); }
            });
        }
        await app.RunAsync(stoppingToken);
    }

    private AgentWriteSession? Session(HttpContext context) => writeSession.Get(context.Request.Cookies[AdminCookie]);

    private IResult? RequireWriteSession(HttpContext context)
    {
        var session = Session(context);
        if (session is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
        if (!writeSession.CheckCsrf(session, context.Request.Headers["X-RelayLink-CSRF"].ToString()))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return null;
    }

    private static IResult MutationError(AgentProfileMutationException exception) => exception.Reason switch
    {
        AgentProfileMutationError.Missing => Results.NotFound(new { message = exception.Message }),
        _ => Results.Conflict(new { message = exception.Message })
    };

}

public sealed record AgentChannelsResponse(string ClientId, bool Online, DateTimeOffset? UpdatedAtUtc, IReadOnlyList<AgentChannelView> Channels);
public sealed record AgentMappingsResponse(string ClientId, bool Online, DateTimeOffset? UpdatedAtUtc, IReadOnlyList<AgentMappingView> Mappings);
public sealed record AgentAddProfileRequest(string Version, AgentServerProfile Server, bool ConfirmExistingState);
public sealed record AgentRemoveProfileRequest(string Version);
public sealed record AgentUpdateProfileRequest(string Version, AgentServerProfile Server);
public sealed record AgentSetProfileEnabledRequest(string Version, bool? Enabled);
