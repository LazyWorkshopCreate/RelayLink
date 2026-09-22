using System.Net;
using System.Text;
using System.Text.Encodings.Web;
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

public sealed class AgentLocalDashboard(AgentConfiguration configuration, AgentStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.DashboardPort == 0) return;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, configuration.DashboardPort));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next();
        });
        app.MapGet("/api/v1/status", () => Results.Ok(status.Snapshot));
        app.MapGet("/api/v1/channels", () =>
        {
            var snapshot = status.Snapshot;
            return Results.Ok(new AgentChannelsResponse(snapshot.ClientId, snapshot.Online, snapshot.UpdatedAtUtc, snapshot.Channels));
        });
        app.MapGet("/api/v1/mappings", () =>
        {
            var snapshot = status.Snapshot;
            return Results.Ok(new AgentMappingsResponse(snapshot.ClientId, snapshot.Online, snapshot.UpdatedAtUtc, snapshot.OutboundMappings));
        });
        // Kept for compatibility with builds that exposed the original unversioned aggregate endpoint.
        app.MapGet("/api/status", () => Results.Ok(status.Snapshot));
        app.MapGet("/", () => Results.Content(Render(status.Snapshot), "text/html; charset=utf-8"));
        await app.RunAsync(stoppingToken);
    }

    private static string Render(AgentStatusSnapshot snapshot)
    {
        static string E(string? text) => HtmlEncoder.Default.Encode(text ?? "");
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"refresh\" content=\"5\"><title>RelayLink Agent</title><style>body{font:15px system-ui,sans-serif;background:#f6f8fb;color:#182337;margin:0;padding:32px}main{max-width:900px;margin:auto}h1{font-size:24px}section{background:white;border:1px solid #e4e9f0;border-radius:12px;margin:18px 0;padding:20px}table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:10px;border-bottom:1px solid #eef1f5}th{color:#526078}.ok{color:#087f5b}.muted{color:#718096}code{background:#edf2f7;padding:3px 6px;border-radius:4px}</style></head><body><main>");
        html.Append("<h1>RelayLink Agent · ").Append(E(snapshot.ClientId)).Append("</h1><p class=\"" ).Append(snapshot.Online ? "ok" : "muted").Append("\">● ").Append(snapshot.Online ? "已连接服务端" : "未连接服务端").Append(" · 每 5 秒刷新</p>");
        html.Append("<section><h2>被访问通道</h2><table><thead><tr><th>通道</th><th>模式</th><th>目标</th><th>状态</th></tr></thead><tbody>");
        foreach (var channel in snapshot.Channels)
            html.Append("<tr><td>").Append(E(channel.DisplayName)).Append(" <small>").Append(E(channel.ChannelId)).Append("</small></td><td>").Append(channel.AuthorizedClientsOnly ? channel.EndToEndEncryptionEnabled ? "仅授权客户端（加密）" : "仅授权客户端（明文）" : "普通代理").Append("</td><td><code>").Append(E(channel.TargetHost)).Append(':').Append(channel.TargetPort).Append("</code></td><td>").Append(channel.Enabled ? "启用" : "禁用").Append("</td></tr>");
        if (snapshot.Channels.Count == 0) html.Append("<tr><td colspan=\"4\" class=\"muted\">当前无在线通道</td></tr>");
        html.Append("</tbody></table></section><section><h2>端到端访问入口</h2><table><thead><tr><th>入口 ID</th><th>访问目标</th><th>本机地址</th></tr></thead><tbody>");
        foreach (var mapping in snapshot.OutboundMappings)
            html.Append("<tr><td>").Append(E(mapping.MappingId)).Append("</td><td>").Append(E(mapping.TargetClientId)).Append('/').Append(E(mapping.TargetChannelId)).Append("</td><td>").Append(mapping.LocalAddress is null ? "<span class=\"muted\">不可用</span>" : $"<code>{E(mapping.LocalAddress)}</code>").Append("</td></tr>");
        if (snapshot.OutboundMappings.Count == 0) html.Append("<tr><td colspan=\"3\" class=\"muted\">当前无端到端访问入口</td></tr>");
        html.Append("</tbody></table></section><p class=\"muted\">此页面仅在本机 127.0.0.1 提供，只读且不展示密钥。</p></main></body></html>");
        return html.ToString();
    }
}

public sealed record AgentChannelsResponse(string ClientId, bool Online, DateTimeOffset? UpdatedAtUtc, IReadOnlyList<AgentChannelView> Channels);
public sealed record AgentMappingsResponse(string ClientId, bool Online, DateTimeOffset? UpdatedAtUtc, IReadOnlyList<AgentMappingView> Mappings);
