using System.Net;
using System.Text.Json.Serialization;

namespace RelayLink.Server.Configuration;

/// <summary>
/// 自动化管理面配置。缺省即关闭：未配置该段时整个自动化入口不存在。
/// 该入口只允许绑定 loopback，且只提供 TLS；不提供关闭 TLS 的开关。
/// </summary>
public sealed record AutomationConfiguration(
    bool Enabled,
    string ListenAddress,
    int Port,
    string CertificatePemPath,
    string PrivateKeyPemPath)
{
    public const string LoopbackAddress = "127.0.0.1";
    public const int DefaultPort = 18082;
    public const int DefaultSessionLifetimeMinutes = 120;
    public const int DefaultPendingPairings = 8;
    public const int DefaultAuthorizations = 16;

    public int SessionLifetimeMinutes { get; init; } = DefaultSessionLifetimeMinutes;
    public int MaxPendingPairings { get; init; } = DefaultPendingPairings;
    public int MaxAuthorizations { get; init; } = DefaultAuthorizations;

    // 已解析的绝对路径，运行时填充，不写入配置文件。
    [JsonIgnore]
    public string? ResolvedCertificatePath { get; init; }

    [JsonIgnore]
    public string? ResolvedPrivateKeyPath { get; init; }

    [JsonIgnore]
    public IPEndPoint ListenEndPoint => new(IPAddress.Parse(ListenAddress), Port);

    /// <summary>是否声明为回环绑定（唯一允许的形式）。</summary>
    [JsonIgnore]
    public bool IsLoopback => ListenAddress == LoopbackAddress;
}
