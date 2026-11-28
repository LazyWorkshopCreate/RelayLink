using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
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

        public static Task<EntryDescriptor> DiscoverMappingAsync(int dashboardPort, string targetClientId,
            string targetChannelId, CancellationToken cancellationToken = default) =>
            DiscoverMappingAsync(dashboardPort, targetClientId, targetChannelId, null, cancellationToken);

        public static async Task<EntryDescriptor> DiscoverMappingAsync(int dashboardPort, string targetClientId,
            string targetChannelId, string? profileId, CancellationToken cancellationToken = default)
        {
            if (dashboardPort is < 1 or > 65535)
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "dashboardPort 无效。");
            if (string.IsNullOrWhiteSpace(targetClientId) || string.IsNullOrWhiteSpace(targetChannelId))
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "targetClientId 与 targetChannelId 不能为空。");
            cancellationToken.ThrowIfCancellationRequested();

            using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) })
            {
                string payload;
                try
                {
                    using (var response = await http.GetAsync(
                        "http://127.0.0.1:" + dashboardPort.ToString(CultureInfo.InvariantCulture) + "/api/v2/mappings",
                        cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new RelayLinkException(RelayLinkErrorCodes.EntryUnavailable,
                                "Agent 只读 API 返回 " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "。");
                        payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (RelayLinkException)
                {
                    throw;
                }
                catch (HttpRequestException exception)
                {
                    throw new RelayLinkException(RelayLinkErrorCodes.EntryUnavailable,
                        "无法连接本机 Agent 只读 API。", exception);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    throw new RelayLinkException(RelayLinkErrorCodes.Timeout, "查询本机 Agent 只读 API 超时。", exception);
                }

                Dictionary<string, object?>? root;
                try
                {
                    root = MiniJson.Parse(payload) as Dictionary<string, object?>;
                }
                catch (FormatException exception)
                {
                    throw new RelayLinkException(RelayLinkErrorCodes.ConfigurationDrift, "Agent 返回了无法解析的内容。", exception);
                }

                if (root is null || !root.TryGetValue("servers", out var serversObj) || !(serversObj is List<object?> servers))
                    throw new RelayLinkException(RelayLinkErrorCodes.EntryNotFound, "Agent 未返回 servers。");

                foreach (var serverObj in servers)
                {
                    if (!(serverObj is Dictionary<string, object?> server)) continue;
                    if (!string.IsNullOrEmpty(profileId))
                    {
                        if (!server.TryGetValue("profileId", out var profileObj) || !(profileObj is string serverProfile) ||
                            !string.Equals(serverProfile, profileId, StringComparison.Ordinal))
                            continue;
                    }
                    if (!server.TryGetValue("mappings", out var mappingsObj) || !(mappingsObj is List<object?> mappings)) continue;
                    foreach (var mappingObj in mappings)
                    {
                        if (!(mappingObj is Dictionary<string, object?> mapping)) continue;
                        if (mapping.TryGetValue("enabled", out var enabledObj) && enabledObj is bool enabled && !enabled) continue;
                        if (!Matches(mapping, "targetClientId", targetClientId)) continue;
                        if (!Matches(mapping, "targetChannelId", targetChannelId)) continue;
                        if (!mapping.TryGetValue("localAddress", out var addressObj) || !(addressObj is string value) ||
                            string.IsNullOrWhiteSpace(value)) continue;
                        if (!TryParseEndpoint(value, out var host, out var port)) continue;
                        var transport = mapping.TryGetValue("transportProtocol", out var transportObj) &&
                            transportObj is string tp && !string.IsNullOrWhiteSpace(tp)
                            ? tp : "tcp";
                        return new EntryDescriptor(host, port, transport);
                    }
                }
            }

            throw new RelayLinkException(RelayLinkErrorCodes.EntryNotFound,
                "本机 Agent 没有匹配的授权互访入口；Agent 离线或尚未建立该映射。");
        }

        private static bool TryParseEndpoint(string value, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            var trimmed = value.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                var close = trimmed.IndexOf(']');
                if (close <= 1 || close + 2 >= trimmed.Length || trimmed[close + 1] != ':') return false;
                host = trimmed.Substring(1, close - 1);
                return int.TryParse(trimmed.Substring(close + 2), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                       port is >= 1 and <= 65535;
            }
            var separator = trimmed.LastIndexOf(':');
            if (separator <= 0 || trimmed.IndexOf(':') != separator) return false;
            host = trimmed.Substring(0, separator);
            return int.TryParse(trimmed.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                   port is >= 1 and <= 65535;
        }

        private static bool Matches(Dictionary<string, object?> element, string name, string expected) =>
            element.TryGetValue(name, out var value) && value is string text &&
            string.Equals(text, expected, StringComparison.Ordinal);

        private sealed class MiniJson
        {
            private readonly string text;
            private int index;

            private MiniJson(string text) => this.text = text;

            public static object? Parse(string input)
            {
                if (input is null) throw new FormatException("Empty JSON.");
                var parser = new MiniJson(input);
                var value = parser.ReadValue();
                parser.SkipWhitespace();
                if (parser.index != parser.text.Length)
                    throw new FormatException("Trailing characters after JSON value.");
                return value;
            }

            private object? ReadValue()
            {
                SkipWhitespace();
                if (index >= text.Length) throw new FormatException("Unexpected end of JSON.");
                return text[index] switch
                {
                    '{' => ReadObject(),
                    '[' => ReadArray(),
                    '"' => ReadString(),
                    't' => ReadLiteral("true", true),
                    'f' => ReadLiteral("false", false),
                    'n' => ReadLiteral("null", null),
                    _ => ReadNumber()
                };
            }

            private Dictionary<string, object?> ReadObject()
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                index++; // {
                SkipWhitespace();
                if (index < text.Length && text[index] == '}') { index++; return dict; }
                while (true)
                {
                    SkipWhitespace();
                    if (index >= text.Length || text[index] != '"') throw new FormatException("Expected string key.");
                    var key = ReadString();
                    SkipWhitespace();
                    if (index >= text.Length || text[index] != ':') throw new FormatException("Expected ':'.");
                    index++;
                    dict[key] = ReadValue();
                    SkipWhitespace();
                    if (index >= text.Length) throw new FormatException("Unterminated object.");
                    if (text[index] == '}') { index++; return dict; }
                    if (text[index] != ',') throw new FormatException("Expected ',' or '}'.");
                    index++;
                }
            }

            private List<object?> ReadArray()
            {
                var list = new List<object?>();
                index++; // [
                SkipWhitespace();
                if (index < text.Length && text[index] == ']') { index++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWhitespace();
                    if (index >= text.Length) throw new FormatException("Unterminated array.");
                    if (text[index] == ']') { index++; return list; }
                    if (text[index] != ',') throw new FormatException("Expected ',' or ']'.");
                    index++;
                }
            }

            private string ReadString()
            {
                index++; // "
                var sb = new StringBuilder();
                while (index < text.Length)
                {
                    var ch = text[index++];
                    if (ch == '"') return sb.ToString();
                    if (ch < 0x20) throw new FormatException("Unescaped control character.");
                    if (ch == '\\')
                    {
                        if (index >= text.Length) throw new FormatException("Unterminated escape.");
                        var esc = text[index++];
                        switch (esc)
                        {
                            case '"': case '\\': case '/': sb.Append(esc); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (index + 4 > text.Length) throw new FormatException("Invalid unicode escape.");
                                if (!int.TryParse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                    throw new FormatException("Invalid hex escape.");
                                sb.Append((char)code);
                                index += 4;
                                break;
                            default: throw new FormatException("Unknown escape.");
                        }
                    }
                    else sb.Append(ch);
                }
                throw new FormatException("Unterminated string.");
            }

            private object? ReadLiteral(string literal, object? result)
            {
                if (index + literal.Length > text.Length || string.Compare(text, index, literal, 0, literal.Length, StringComparison.Ordinal) != 0)
                    throw new FormatException("Invalid JSON literal.");
                index += literal.Length;
                return result;
            }

            private object ReadNumber()
            {
                var start = index;
                if (text[index] == '-') index++;
                if (index >= text.Length || text[index] < '0' || text[index] > '9')
                    throw new FormatException("Invalid JSON number.");
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
                var isFloat = false;
                if (index < text.Length && text[index] == '.')
                {
                    isFloat = true;
                    index++;
                    if (index >= text.Length || text[index] < '0' || text[index] > '9') throw new FormatException("Invalid fraction.");
                    while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
                }
                if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
                {
                    isFloat = true;
                    index++;
                    if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                    if (index >= text.Length || text[index] < '0' || text[index] > '9') throw new FormatException("Invalid exponent.");
                    while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
                }
                var slice = text.Substring(start, index - start);
                if (!isFloat && long.TryParse(slice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw new FormatException("Invalid number.");
            }

            private void SkipWhitespace()
            {
                while (index < text.Length && text[index] is ' ' or '\t' or '\r' or '\n') index++;
            }
        }
    }
}
