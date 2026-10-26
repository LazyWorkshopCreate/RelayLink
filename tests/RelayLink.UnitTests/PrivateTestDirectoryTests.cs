using System.Security.AccessControl;
using System.Security.Principal;
using RelayLink.Server.Runtime;
using RelayLink.Testing;

namespace RelayLink.UnitTests;

public sealed class PrivateTestDirectoryTests
{
    [Fact]
    public void Private_fixture_directory_can_hold_a_database_and_a_broader_grant_is_refused()
    {
        using var directory = new TestDirectory("relaylink-db-fixture-");
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectoryInfo(directory.Path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var current = WindowsIdentity.GetCurrent().User;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = Assert.IsType<SecurityIdentifier>(rule.IdentityReference);
                Assert.True(sid == current
                    || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
            }
        }
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory.Path));

        var database = Path.Combine(directory.Path, "history.db");
        SecretFilePermissions.PrepareDatabase(database);
        Assert.True(File.Exists(database));

        using var shared = new TestDirectory("relaylink-db-shared-");
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectoryInfo(shared.Path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(shared.Path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(shared.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SecretFilePermissions.PrepareDatabase(Path.Combine(shared.Path, "audit.db")));
        Assert.Contains("SECURITY", exception.Message);
        Assert.Contains("not changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(shared.Path, "audit.db")));
    }
}
