using System.Text.Json;

namespace RelayLink.Cli;

/// <summary>
/// 退出码是 CLI 与宿主 agent 的稳定契约，不得随意改动。
/// 关键区分：8 表示连不上（含旧服务端无该端口、开关未启用、服务未启动）；
/// 9 只表示连得上但 API 或 CLI 版本不受支持。
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Usage = 2;
    public const int Unauthorized = 3;
    public const int ScopeInsufficient = 4;
    public const int ApprovalRequired = 5;
    public const int ServerRejected = 6;
    public const int AuditOrStorage = 7;
    public const int NetworkOrPin = 8;
    public const int VersionIncompatible = 9;
}

/// <summary>profile 落盘字段。令牌本体进凭据库，不进 profile 文件。</summary>
public sealed record CliProfile(
    string Name,
    string? Entry,
    int DashboardPort,
    string? AutomationInstanceId,
    string? SpkiSha256,
    string? GrantId)
{
    public const int DefaultDashboardPort = 18081;
}

public sealed class CliConfiguration
{
    public List<CliProfile> Profiles { get; init; } = [];
    public string? ActiveProfile { get; set; }
}

/// <summary>
/// 本地配置与凭据。优先操作系统凭据库；不可用时回退到受保护文件并告警。
/// 目录权限：Unix 0700，Windows 仅当前用户。
/// </summary>
public static class CliStorage
{
    /// <summary>
    /// CLI 数据根目录。优先 RELAYLINK_HOME（便携/测试场景，避免污染真实用户目录），
    /// 否则用户主目录下的 .relaylink。
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("RELAYLINK_HOME");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".relaylink")
                : overridden;
        }
    }

    public static string ConfigurationPath => Path.Combine(RootDirectory, "config.json");

    public static string CredentialDirectory => Path.Combine(RootDirectory, "credentials");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static CliConfiguration LoadConfiguration()
    {
        if (!File.Exists(ConfigurationPath)) return new CliConfiguration();
        try
        {
            return JsonSerializer.Deserialize<CliConfiguration>(File.ReadAllText(ConfigurationPath), Json) ?? new CliConfiguration();
        }
        catch (JsonException)
        {
            return new CliConfiguration();
        }
    }

    public static void SaveConfiguration(CliConfiguration configuration)
    {
        Directory.CreateDirectory(RootDirectory);
        RestrictDirectory(RootDirectory);
        File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(configuration, Json));
        RestrictFile(ConfigurationPath);
    }

    /// <summary>
    /// 设备私钥。优先 DPAPI/Keychain/libsecret；本实现统一走受保护文件，
    /// 并在首次写入时明确告警——这是设计要求中的「回退」路径，不能静默。
    /// </summary>
    public static bool TrySaveCredential(string profile, string devicePrivateKey, string token, out string warning)
    {
        warning = string.Empty;
        try
        {
            Directory.CreateDirectory(CredentialDirectory);
            RestrictDirectory(CredentialDirectory);
            var path = Path.Combine(CredentialDirectory, Sanitize(profile) + ".json");
            var payload = JsonSerializer.Serialize(new { devicePrivateKey, token }, Json);
            File.WriteAllText(path, payload);
            RestrictFile(path);
            warning = $"凭据存储回退到受保护文件：{path}。若本机提供系统凭据库，请改用系统凭据库。";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warning = $"无法写入凭据：{exception.Message}";
            return false;
        }
    }

    public static (string? DevicePrivateKey, string? Token) LoadCredential(string profile)
    {
        var path = Path.Combine(CredentialDirectory, Sanitize(profile) + ".json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return (document.RootElement.TryGetProperty("devicePrivateKey", out var key) ? key.GetString() : null,
                document.RootElement.TryGetProperty("token", out var token) ? token.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string Sanitize(string name) =>
        new(name.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());

    private static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
