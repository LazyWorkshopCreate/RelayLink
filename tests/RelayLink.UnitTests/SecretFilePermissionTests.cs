using System.Security.AccessControl;
using System.Security.Principal;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class SecretFilePermissionTests
{
    [Fact]
    public async Task Written_secret_file_is_owner_only_and_a_loose_file_is_reported()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var directory = Directory.CreateTempSubdirectory("relaylink-secrets-").FullName;
        try
        {
            var path = Path.Combine(directory, "client.json");
            await SecretFilePermissions.WriteTextAsync(path, """{"clientId":"agent"}""", overwrite: false, CancellationToken.None);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Empty(SecretFilePermissions.FindLoose([path]));

            var databaseDirectory = Path.Combine(directory, "data");
            Directory.CreateDirectory(databaseDirectory);
            var directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(databaseDirectory, directoryMode);
            var database = Path.Combine(databaseDirectory, "audit.db");
            SecretFilePermissions.PrepareDatabase(database);
            Assert.True(File.Exists(database));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database));
            Assert.Equal(directoryMode, File.GetUnixFileMode(databaseDirectory));
            Assert.Empty(SecretFilePermissions.FindLoose([database]));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var loose = SecretFilePermissions.FindLoose([path, Path.Combine(directory, "missing.json")]);
            Assert.Equal(path, Assert.Single(loose));
            Assert.Contains("0600", SecretFilePermissions.LooseWarning(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void New_database_directory_is_created_private_without_changing_its_parent()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("relaylink-newdir-").FullName;
        var rootMode = File.GetUnixFileMode(root);
        try
        {
            var database = Path.Combine(root, "dedicated", "audit.db");
            SecretFilePermissions.PrepareDatabase(database);
            var created = Path.GetDirectoryName(database)!;
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(created));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database));
            Assert.Equal(rootMode, File.GetUnixFileMode(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Shared_or_temporary_directory_is_refused_without_a_permission_change()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var temporary = Path.GetTempPath();
        var temporaryMode = File.GetUnixFileMode(temporary);
        var temporaryDatabase = Path.Combine(temporary, $"relaylink-audit-{Guid.NewGuid():N}.db");
        try
        {
            var refused = Assert.Throws<InvalidOperationException>(() => SecretFilePermissions.PrepareDatabase(temporaryDatabase));
            Assert.Contains("SECURITY", refused.Message);
            Assert.Contains("not changed", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(temporaryDatabase));
            Assert.Equal(temporaryMode, File.GetUnixFileMode(temporary));

            var shared = Directory.CreateTempSubdirectory("relaylink-open-").FullName;
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
                | UnixFileMode.StickyBit;
            File.SetUnixFileMode(shared, mode);
            var database = Path.Combine(shared, "audit.db");
            var exception = Assert.Throws<InvalidOperationException>(() => SecretFilePermissions.PrepareDatabase(database));
            Assert.Contains("SECURITY", exception.Message);
            Assert.Contains("not changed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(database));
            Assert.Equal(mode, File.GetUnixFileMode(shared));
            Directory.Delete(shared, recursive: true);
        }
        finally
        {
            if (File.Exists(temporaryDatabase)) File.Delete(temporaryDatabase);
        }
    }

    [Fact]
    public async Task Sidecar_link_fails_closed_and_releases_the_connection()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("relaylink-sidecar-").FullName;
        try
        {
            var database = Path.Combine(root, "dedicated", "history.db");
            SecretFilePermissions.PrepareDatabase(database);
            var target = Path.Combine(root, "target");
            await File.WriteAllTextAsync(target, "keep");
            var targetMode = File.GetUnixFileMode(target);
            File.CreateSymbolicLink(database + "-wal", target);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TrafficHistoryService.OpenAsync(new HistoryConfiguration(true, database), CancellationToken.None));
            Assert.Contains("symbolic link", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(targetMode, File.GetUnixFileMode(target));
            using var probe = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(probe.CanRead);
        }
        finally
        {
            foreach (var link in new[] { Path.Combine(root, "dedicated", "history.db-wal") })
            {
                if (new FileInfo(link).LinkTarget is not null) File.Delete(link);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Group_writable_directory_is_not_made_private()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var shared = Directory.CreateTempSubdirectory("relaylink-group-").FullName;
        try
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.SetGroup;
            File.SetUnixFileMode(shared, mode);
            var database = Path.Combine(shared, "audit.db");
            SecretFilePermissions.PrepareDatabase(database);
            Assert.Equal(mode, File.GetUnixFileMode(shared));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database));
        }
        finally
        {
            Directory.Delete(shared, recursive: true);
        }
    }

    [Fact]
    public void Unwritable_parent_fails_closed_without_a_permission_change()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var parent = Directory.CreateTempSubdirectory("relaylink-nowrite-").FullName;
        var mode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        try
        {
            File.SetUnixFileMode(parent, mode);
            var database = Path.Combine(parent, "audit.db");
            Assert.ThrowsAny<Exception>(() => SecretFilePermissions.PrepareDatabase(database));
            Assert.False(File.Exists(database));
            Assert.Equal(mode, File.GetUnixFileMode(parent));
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Symbolic_links_are_resolved_without_changing_the_target_directory()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Directory.CreateTempSubdirectory("relaylink-links-").FullName;
        var rootMode = File.GetUnixFileMode("/");
        try
        {
            var real = Path.Combine(root, "real");
            Directory.CreateDirectory(real);
            var realMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(real, realMode);
            var alias = Path.Combine(root, "alias");
            Directory.CreateSymbolicLink(alias, real);
            var database = Path.Combine(alias, "audit.db");
            SecretFilePermissions.PrepareDatabase(database);
            Assert.Equal(realMode, File.GetUnixFileMode(real));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(real, "audit.db")));

            var other = Path.Combine(root, "other-file");
            File.WriteAllText(other, "keep");
            File.SetUnixFileMode(other, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var linkedFile = Path.Combine(root, "audit.db");
            File.CreateSymbolicLink(linkedFile, other);
            var refused = Assert.Throws<InvalidOperationException>(() => SecretFilePermissions.PrepareDatabase(linkedFile));
            Assert.Contains("symbolic link", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not changed", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(other));

            var rootAlias = Path.Combine(root, "root-alias");
            Directory.CreateSymbolicLink(rootAlias, "/");
            var rootDatabase = Path.Combine(rootAlias, "relaylink-audit-should-not-exist.db");
            var rootRefusal = Assert.Throws<InvalidOperationException>(() => SecretFilePermissions.PrepareDatabase(rootDatabase));
            Assert.Contains("filesystem root", rootRefusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not changed", rootRefusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(rootMode, File.GetUnixFileMode("/"));
            Assert.False(File.Exists("/relaylink-audit-should-not-exist.db"));
        }
        finally
        {
            if (File.GetUnixFileMode("/") != rootMode) File.SetUnixFileMode("/", rootMode);
            foreach (var link in new[] { Path.Combine(root, "alias"), Path.Combine(root, "root-alias"), Path.Combine(root, "audit.db") })
            {
                if (new FileInfo(link).LinkTarget is not null) File.Delete(link);
                else if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Windows_acl_reports_another_principal_and_restriction_is_repeatable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("relaylink-secrets-win-").FullName;
        try
        {
            var path = Path.Combine(directory, "server.json");
            File.WriteAllText(path, "{}");
            var other = new SecurityIdentifier("S-1-5-21-397955417-626881126-188441444-1001");
            var security = new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(other, FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            Assert.Contains(path, SecretFilePermissions.FindLoose([path]));

            SecretFilePermissions.RestrictDatabase(path);
            SecretFilePermissions.RestrictDatabase(path);
            Assert.DoesNotContain(path, SecretFilePermissions.FindLoose([path]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Windows_non_inherited_directory_grant_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var rights in new[]
        {
            FileSystemRights.CreateFiles,
            FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.Delete,
            FileSystemRights.ChangePermissions
        })
        {
            var directory = Directory.CreateTempSubdirectory("relaylink-dir-ace-").FullName;
            try
            {
                var nested = Path.Combine(directory, "data");
                Directory.CreateDirectory(nested);
                var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows identity has no SID.");
                var other = new SecurityIdentifier("S-1-5-21-397955417-626881126-188441444-3003");
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(other, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(nested).SetAccessControl(security);
                var before = new DirectoryInfo(nested).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                var database = Path.Combine(nested, "audit.db");
                var exception = Assert.Throws<InvalidOperationException>(() => SecretFilePermissions.PrepareDatabase(database));
                Assert.Contains("SECURITY", exception.Message);
                Assert.False(File.Exists(database));
                Assert.Equal(before, new DirectoryInfo(nested).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Windows_metadata_read_is_not_reported_as_secret_content_access()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("relaylink-secrets-meta-").FullName;
        try
        {
            var path = Path.Combine(directory, "server.json");
            File.WriteAllText(path, "{}");
            SecretFilePermissions.RestrictDatabase(path);
            var other = new SecurityIdentifier("S-1-5-21-397955417-626881126-188441444-2002");
            var security = new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(other, FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            Assert.DoesNotContain(path, SecretFilePermissions.FindLoose([path]));

            security = new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(other, FileSystemRights.ReadData, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            Assert.Contains(path, SecretFilePermissions.FindLoose([path]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
