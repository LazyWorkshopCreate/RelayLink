using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    public static bool IsValidProfileName(string? name) =>
        name is { Length: >= 1 and <= 64 } &&
        !name.Contains("..", StringComparison.Ordinal) &&
        Regex.IsMatch(name, "\\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant);

    public static string CredentialPathFor(string profile)
    {
        if (!IsValidProfileName(profile))
            throw new ArgumentException("Invalid CLI profile name.", nameof(profile));
        return Path.Combine(CredentialDirectory, profile + ".json");
    }

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
        EnsurePrivateDirectory(RootDirectory);
        WritePrivateText(ConfigurationPath, JsonSerializer.Serialize(configuration, Json));
    }

    /// <summary>
    /// 设备私钥。Windows 使用 DPAPI（CurrentUser）加密与仅当前用户 ACL；
    /// Unix 优先受保护 0600 原子创建并提示未启用系统凭据库时的回退状态。
    /// </summary>
    public static bool TrySaveCredential(string profile, string devicePrivateKey, string token, out string warning)
    {
        warning = string.Empty;
        try
        {
            EnsurePrivateDirectory(RootDirectory);
            EnsurePrivateDirectory(CredentialDirectory);
            var path = CredentialPathFor(profile);
            var plainPayload = JsonSerializer.Serialize(new { devicePrivateKey, token }, Json);
            if (OperatingSystem.IsWindows())
            {
                var protectedPayload = ProtectWindowsData(plainPayload);
                WritePrivateText(path, JsonSerializer.Serialize(new { format = "dpapi-current-user", cipher = protectedPayload }, Json));
                return true;
            }

            WritePrivateText(path, plainPayload);
            warning = $"凭据存储回退到受保护文件：{path}。若本机提供系统凭据库，请改用系统凭据库。";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            warning = $"无法写入凭据：{exception.Message}";
            return false;
        }
    }

    public static (string? DevicePrivateKey, string? Token) LoadCredential(string profile)
    {
        if (!IsValidProfileName(profile)) return (null, null);
        var path = CredentialPathFor(profile);
        if (!File.Exists(path)) return (null, null);
        try
        {
            var raw = File.ReadAllText(path);
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("format", out var format) &&
                format.ValueKind == JsonValueKind.String &&
                format.GetString() == "dpapi-current-user" &&
                document.RootElement.TryGetProperty("cipher", out var cipher) &&
                cipher.ValueKind == JsonValueKind.String)
            {
                if (!OperatingSystem.IsWindows()) return (null, null);
                var plain = UnprotectWindowsData(cipher.GetString()!);
                using var inner = JsonDocument.Parse(plain);
                return (inner.RootElement.TryGetProperty("devicePrivateKey", out var k) ? k.GetString() : null,
                    inner.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null);
            }

            return (document.RootElement.TryGetProperty("devicePrivateKey", out var key) ? key.GetString() : null,
                document.RootElement.TryGetProperty("token", out var token) ? token.GetString() : null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    public static void DeleteCredential(string profile)
    {
        var path = CredentialPathFor(profile);
        if (File.Exists(path)) File.Delete(path);
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            RestrictDirectoryWindows(path);
            return;
        }

        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivateText(string path, string content)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            if (OperatingSystem.IsWindows())
            {
                using (var stream = CreatePrivateWindowsFile(tmp))
                {
                    stream.Write(bytes);
                }
                File.Move(tmp, path, overwrite: true);
                RestrictFileWindows(path);
                return;
            }

            using (var stream = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                stream.Write(bytes);
            }
            File.Move(tmp, path, overwrite: true);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreatePrivateWindowsFile(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictDirectoryWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictFileWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindowsData(string plaintext)
    {
        var plainBytes = new UTF8Encoding(false).GetBytes(plaintext);
        var plainHandle = GCHandle.Alloc(plainBytes, GCHandleType.Pinned);
        var input = new DataBlob { Size = plainBytes.Length, Data = plainHandle.AddrOfPinnedObject() };
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            var cipherBytes = new byte[output.Size];
            Marshal.Copy(output.Data, cipherBytes, 0, output.Size);
            return Convert.ToBase64String(cipherBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            plainHandle.Free();
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindowsData(string cipherBase64)
    {
        var cipherBytes = Convert.FromBase64String(cipherBase64);
        var cipherHandle = GCHandle.Alloc(cipherBytes, GCHandleType.Pinned);
        var input = new DataBlob { Size = cipherBytes.Length, Data = cipherHandle.AddrOfPinnedObject() };
        var output = default(DataBlob);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            var plainBytes = new byte[output.Size];
            try
            {
                Marshal.Copy(output.Data, plainBytes, 0, output.Size);
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            cipherHandle.Free();
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? dataDescr, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr dataDescr, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
