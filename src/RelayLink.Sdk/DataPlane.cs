using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RelayLink.Sdk
{
    /// <summary>
    /// SDK 稳定错误码。各语言实现必须保持一致语义。
    /// </summary>
    public static class RelayLinkErrorCodes
    {
        public const string EntryUnavailable = "entry_unavailable";
        public const string EntryNotFound = "entry_not_found";
        public const string TunnelRejected = "tunnel_rejected";
        public const string TargetConnectFailed = "target_connect_failed";
        public const string Timeout = "timeout";
        public const string ConfigurationDrift = "configuration_drift";
        public const string InvalidUsage = "invalid_usage";
    }

    public sealed class RelayLinkException : Exception
    {
        public RelayLinkException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
        public string Code { get; }
    }

    /// <summary>
    /// 入口描述。SDK 只面向入口，不实现中继协议，也不持有任何中继密钥。
    /// 数据面链路的加密强度由通道配置决定；SDK 不关闭也不降级授权互访的内层 TLS。
    /// </summary>
    public sealed class EntryDescriptor
    {
        public EntryDescriptor(string host, int port, string transport = "tcp")
        {
            if (string.IsNullOrWhiteSpace(host)) throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "host 不能为空。");
            if (port is < 1 or > 65535) throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "port 必须在 1–65535。");
            var normalizedTransport = string.IsNullOrEmpty(transport) ? "tcp" : transport;
            if (!string.Equals(normalizedTransport, "tcp", StringComparison.Ordinal) &&
                !string.Equals(normalizedTransport, "udp", StringComparison.Ordinal))
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "transport 必须为 tcp 或 udp。");

            var cleanHost = host.Trim();
            if (cleanHost.StartsWith("[", StringComparison.Ordinal) && cleanHost.EndsWith("]", StringComparison.Ordinal) && cleanHost.Length > 2)
                cleanHost = cleanHost.Substring(1, cleanHost.Length - 2);
            Host = cleanHost;
            Port = port;
            Transport = normalizedTransport;
        }

        public string Host { get; }
        public int Port { get; }
        public string Transport { get; }

        public string ToEndpointString() =>
            Host.IndexOf(':') >= 0
                ? "[" + Host + "]:" + Port.ToString()
                : Host + ":" + Port.ToString();
    }

    /// <summary>
    /// TCP 数据面。一次业务流一条连接，支持单向半关闭；不对连接池与重连做隐藏处理，
    /// 因为通道有连接上限与撤销语义，隐藏重试会造成配额冲突与难以诊断的半可用状态。
    /// </summary>
    public static class TcpEntry
    {
        public static async Task<TcpClient> ConnectAsync(EntryDescriptor entry, TimeSpan? connectTimeout = null, CancellationToken cancellationToken = default)
        {
            if (entry is null)
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "entry 不能为空。");
            if (entry.Transport != "tcp")
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "该入口不是 TCP。");
            if (connectTimeout is { } requestedTimeout && requestedTimeout <= TimeSpan.Zero)
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "connectTimeout 必须大于 0。");
            cancellationToken.ThrowIfCancellationRequested();

            var client = new TcpClient();
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(20));
#if NET8_0_OR_GREATER
                    await client.ConnectAsync(entry.Host, entry.Port, timeout.Token).ConfigureAwait(false);
#else
                    using (timeout.Token.Register(static state => ((TcpClient)state!).Dispose(), client))
                    {
                        try
                        {
                            await client.ConnectAsync(entry.Host, entry.Port).ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            throw new RelayLinkException(RelayLinkErrorCodes.Timeout, "连接入口超时。", exception);
                        }
                        catch (SocketException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            throw new RelayLinkException(RelayLinkErrorCodes.Timeout, "连接入口超时。", exception);
                        }
                    }
#endif
                }

                return client;
            }
            catch (RelayLinkException)
            {
                client.Dispose();
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw;
            }
            catch (OperationCanceledException exception)
            {
                client.Dispose();
                throw new RelayLinkException(RelayLinkErrorCodes.Timeout, "连接入口超时。", exception);
            }
            catch (SocketException exception)
            {
                client.Dispose();
                throw new RelayLinkException(MapSocketError(exception), "连接入口失败：" + exception.Message, exception);
            }
        }

        /// <summary>单向半关闭：只关闭发送方向，仍可继续接收对端回包。</summary>
        public static void ShutdownWrite(TcpClient client)
        {
            try { client.Client.Shutdown(SocketShutdown.Send); }
            catch (SocketException exception)
            {
                throw new RelayLinkException(RelayLinkErrorCodes.TunnelRejected, "半关闭失败：" + exception.Message, exception);
            }
        }

        private static string MapSocketError(SocketException exception) => exception.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => RelayLinkErrorCodes.EntryUnavailable,
            SocketError.HostNotFound or SocketError.NoData => RelayLinkErrorCodes.EntryNotFound,
            SocketError.TimedOut => RelayLinkErrorCodes.Timeout,
            _ => RelayLinkErrorCodes.TargetConnectFailed
        };
    }

    /// <summary>
    /// UDP 数据面。单 socket 会话，保留报文边界；重传、去重与顺序由应用负责。
    /// </summary>
    public static class UdpEntry
    {
        public static async Task<UdpClient> BindAsync(EntryDescriptor entry, CancellationToken cancellationToken = default)
        {
            if (entry is null)
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "entry 不能为空。");
            if (entry.Transport != "udp")
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "该入口不是 UDP。");
            cancellationToken.ThrowIfCancellationRequested();

            AddressFamily family;
            if (IPAddress.TryParse(entry.Host, out var ip))
            {
                family = ip.AddressFamily;
            }
            else
            {
                try
                {
#if NET8_0_OR_GREATER
                    var addresses = await Dns.GetHostAddressesAsync(entry.Host, cancellationToken).ConfigureAwait(false);
#else
                    var addresses = await Dns.GetHostAddressesAsync(entry.Host).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
#endif
                    if (addresses.Length == 0)
                        throw new RelayLinkException(RelayLinkErrorCodes.EntryNotFound, "无法解析 UDP 入口。");
                    family = addresses[0].AddressFamily;
                }
                catch (SocketException exception)
                {
                    throw new RelayLinkException(RelayLinkErrorCodes.EntryNotFound, "无法解析 UDP 入口：" + exception.Message, exception);
                }
            }

            var client = new UdpClient(family);
            try
            {
                client.Connect(entry.Host, entry.Port);
                return client;
            }
            catch (SocketException exception)
            {
                client.Dispose();
                throw new RelayLinkException(RelayLinkErrorCodes.EntryUnavailable, "绑定 UDP 入口失败：" + exception.Message, exception);
            }
        }

        public static async Task SendAsync(UdpClient client, byte[] datagram, CancellationToken cancellationToken = default)
        {
            if (datagram is null)
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "报文不能为空。");
            if (datagram.Length > 65507)
                throw new RelayLinkException(RelayLinkErrorCodes.InvalidUsage, "报文超过 UDP 上限，SDK 不静默截断。");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
#if NET8_0_OR_GREATER
                await client.SendAsync(datagram.AsMemory(), cancellationToken).ConfigureAwait(false);
#else
                await client.SendAsync(datagram, datagram.Length).ConfigureAwait(false);
#endif
            }
            catch (SocketException exception)
            {
                throw new RelayLinkException(RelayLinkErrorCodes.TargetConnectFailed, "发送 UDP 报文失败：" + exception.Message, exception);
            }
        }
    }
}
