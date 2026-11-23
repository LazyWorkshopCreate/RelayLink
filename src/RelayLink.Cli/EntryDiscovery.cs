using System.Text.Json;

namespace RelayLink.Cli;

/// <summary>
/// 入口发现。优先级：显式 --entry / profile 配置 → 本机 Agent v2 只读 API。
/// 重要限制：Agent v2 视图只上报授权互访的 loopback 端口，
/// AgentChannelView / AgentMappingView 不含通道的 ListenAddress/ListenPort，
/// 因此“普通代理入口”无法自动发现，必须显式配置。
/// </summary>
public static class EntryDiscovery
{
    public sealed record Result(string? Entry, string? Reason);

    public static async Task<Result> DiscoverAsync(CliProfile profile, CliOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Entry)) return new Result(options.Entry, null);
        if (!string.IsNullOrWhiteSpace(profile.Entry)) return new Result(profile.Entry, null);

        var dashboardPort = options.DashboardPort ?? profile.DashboardPort;
        if (dashboardPort <= 0) return new Result(null, "profile 未配置 dashboardPort，且没有显式 --entry。");

        // 普通代理入口无法从 v2 API 发现：这里只查授权互访映射。
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = $"http://127.0.0.1:{dashboardPort}/api/v2/mappings";
            var text = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("servers", out var servers) || servers.GetArrayLength() == 0)
                return new Result(null, "本机 Agent 未配置任何服务端连接项。");

            var candidates = new List<(string ProfileId, string MappingId, string Address)>();
            foreach (var server in servers.EnumerateArray())
            {
                var profileId = server.TryGetProperty("profileId", out var pid) ? pid.GetString() ?? "(unknown)" : "(unknown)";
                if (!server.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.Array) continue;
                foreach (var mapping in mappings.EnumerateArray())
                {
                    if (!mapping.TryGetProperty("localAddress", out var address) || address.ValueKind != JsonValueKind.String) continue;
                    var value = address.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var mappingId = mapping.TryGetProperty("mappingId", out var mid) ? mid.GetString() ?? string.Empty : string.Empty;
                    candidates.Add((profileId, mappingId, value));
                }
            }

            if (candidates.Count == 0)
                return new Result(null, "本机 Agent 在线但没有已建立的授权互访入口。");
            if (candidates.Count == 1) return new Result(candidates[0].Address, null);

            // 多个候选取 profile 名匹配的那一个；仍无法消歧时要求显式指定，不按“第一个”猜。
            var matched = candidates.Where(item => string.Equals(item.ProfileId, profile.Name, StringComparison.Ordinal)).ToArray();
            return matched.Length == 1
                ? new Result(matched[0].Address, null)
                : new Result(null, $"本机 Agent 有 {candidates.Count} 个入口，无法确定使用哪一个；请用 --entry 显式指定。");
        }
        catch (HttpRequestException)
        {
            return new Result(null, $"无法连接本机 Agent 的只读 API（127.0.0.1:{dashboardPort}）。");
        }
        catch (TaskCanceledException)
        {
            return new Result(null, "查询本机 Agent 只读 API 超时。");
        }
        catch (JsonException)
        {
            return new Result(null, "本机 Agent 只读 API 返回了无法解析的内容。");
        }
    }
}
