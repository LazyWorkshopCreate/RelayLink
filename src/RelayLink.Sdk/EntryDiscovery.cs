using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLink.Sdk
{
    /// <summary>
    /// 入口发现。只读本机 Agent 的 v2 API，只接受 127.0.0.1。
    ///
    /// 重要限制：AgentChannelView / AgentMappingView 只上报授权互访的实际 loopback 端口，
    /// 不含通道的 ListenAddress/ListenPort。因此普通代理入口无法自动发现，必须显式配置。
    /// </summary>
    public static class EntryDiscovery
    {
        public const int DefaultDashboardPort = 18081;

        public static async Task<EntryDescriptor> DiscoverMappingAsync(int dashboardPort, string targetClientId,
            string targetChannelId, CancellationToken cancellationToken = default)
        {
            if (dashboardPort is < 1 or > 65535)
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "dashboardPort 无效。");

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                string payload;
                try
                {
                    payload = await http.GetStringAsync(
                        "http://127.0.0.1:" + dashboardPort.ToString() + "/api/v2/mappings").ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                {
                    throw new RelayLinkException(RelayLinkErrorCodes.EntryUnavailable,
                        "无法连接本机 Agent 只读 API。", exception);
                }
                catch (TaskCanceledException exception)
                {
                    throw new RelayLinkException(RelayLinkErrorCodes.Timeout, "查询本机 Agent 只读 API 超时。", exception);
                }

                using (var document = JsonDocument.Parse(payload))
                {
                    if (!document.RootElement.TryGetProperty("servers", out var servers))
                        throw new RelayLinkException(RelayLinkErrorCodes.EntryNotFound, "Agent 未返回 servers。");

                    foreach (var server in servers.EnumerateArray())
                    {
                        if (!server.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.Array) continue;
                        foreach (var mapping in mappings.EnumerateArray())
                        {
                            if (!Matches(mapping, "targetClientId", targetClientId)) continue;
                            if (!Matches(mapping, "targetChannelId", targetChannelId)) continue;
                            if (!mapping.TryGetProperty("localAddress", out var address) || address.ValueKind != JsonValueKind.String) continue;
                            var value = address.GetString();
                            if (string.IsNullOrWhiteSpace(value)) continue;
                            var separator = value.LastIndexOf(':');
                            if (separator <= 0) continue;
                            var host = value.Substring(0, separator);
                            if (!int.TryParse(value.Substring(separator + 1), out var port)) continue;
                            return new EntryDescriptor(host, port);
                        }
                    }
                }
            }

            throw new RelayLinkException(RelayLinkErrorCodes.EntryNotFound,
                "本机 Agent 没有匹配的授权互访入口；Agent 离线或尚未建立该映射。");
        }

        private static bool Matches(JsonElement element, string name, string expected) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }
}
