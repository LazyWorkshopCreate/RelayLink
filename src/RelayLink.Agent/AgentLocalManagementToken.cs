using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace RelayLink.Agent;

// The loopback page has no password. Writes require this file, which is created
// on first start and restricted to the service account plus local administrators.
public sealed class AgentLocalManagementToken
{
    public const string FileName = "local-management.token";
    public const string HeaderName = "X-RelayLink-Local-Token";
    public const string InsecureTransportConfirmation = "CONNECT_WITHOUT_TLS";
    private readonly byte[] expected;

    private AgentLocalManagementToken(string path, string value)
    {
        FilePath = path;
        expected = Encoding.ASCII.GetBytes(value);
    }

    public string FilePath { get; }

    public static AgentLocalManagementToken LoadOrCreate(string configurationDirectory)
    {
        Directory.CreateDirectory(configurationDirectory);
        var path = Path.Combine(configurationDirectory, FileName);
        if (!File.Exists(path))
        {
            var created = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            WritePrivate(path, created + "\n");
            return new AgentLocalManagementToken(path, created);
        }

        RestrictFile(path);
        var text = File.ReadAllText(path).Trim();
        if (text.Length != 64 || text.Any(character => !Uri.IsHexDigit(character)))
            throw new AgentConfigurationException("Local management token file is invalid. Replace it with a new 64-character hex token or delete it so Agent can create one.");
        return new AgentLocalManagementToken(path, text);
    }

    public bool Matches(string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided)) return false;
        var actual = Encoding.UTF8.GetBytes(provided.Trim());
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void WritePrivate(string path, string contents)
    {
        var bytes = new UTF8Encoding(false).GetBytes(contents);
        if (OperatingSystem.IsWindows())
        {
            using var stream = CreatePrivateWindows(path);
            stream.Write(bytes);
            return;
        }

        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        file.Write(bytes);
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreatePrivateWindows(string path)
    {
        var security = PrivateSecurity();
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        new FileInfo(path).SetAccessControl(PrivateSecurity());
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity PrivateSecurity()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.Read, AccessControlType.Allow));
        return security;
    }
}
