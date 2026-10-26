using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelayLink.Testing;

// Test databases must not inherit a shared temp-directory ACL. RelayLink refuses
// to open SQLite files in a directory another principal can read or modify.
public sealed class TestDirectory : IDisposable
{
    public string Path { get; }

    public TestDirectory(string prefix = "relaylink-")
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("A prefix is required.", nameof(prefix));
        Path = Directory.CreateTempSubdirectory(prefix).FullName;
        try
        {
            Restrict(Path);
        }
        catch
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    public static void Restrict(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(directory);
            return;
        }

        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string directory)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Allow(user, inherit));
        security.AddAccessRule(Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), inherit));
        security.AddAccessRule(Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), inherit));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule Allow(IdentityReference identity, InheritanceFlags inherit) =>
        new(identity, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow);
}
