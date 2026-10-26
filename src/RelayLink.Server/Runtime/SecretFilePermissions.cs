using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

internal static class SecretFilePermissions
{
    private static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode LooseBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public static async Task WriteTextAsync(string path, string contents, bool overwrite, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = CreatePrivate(temporary))
            {
                var bytes = new UTF8Encoding(false).GetBytes(contents);
                await stream.WriteAsync(bytes, cancellationToken);
            }

            File.Move(temporary, path, overwrite);
            RestrictFile(path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Creates a missing database as a private file before SQLite opens it.
    // A directory RelayLink itself creates is private from the start, so later WAL/SHM
    // files inherit that ACL. An existing shared, temporary, or other-writable directory
    // is refused and is not chmod'd. Symbolic links are resolved first.
    public static void PrepareDatabase(string path)
    {
        var file = ResolveDatabaseFile(path);
        var parent = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(parent)) EnsureDatabaseDirectory(parent, path);
        if (!File.Exists(file))
        {
            try
            {
                using var created = CreatePrivate(file);
            }
            catch (IOException) when (File.Exists(file) && !IsLink(file))
            {
            }
        }

        ProtectFile(file, path);
    }

    public static void RestrictDatabase(string path)
    {
        ProtectIfExists(path);
        ProtectIfExists(path + "-wal");
        ProtectIfExists(path + "-shm");
    }

    public static IEnumerable<string> SensitivePaths(string serverConfigurationPath, LoadedConfiguration loaded)
    {
        yield return serverConfigurationPath;
        if (!string.IsNullOrWhiteSpace(loaded.Server.Tunnel.PrivateKeyPemPath))
            yield return loaded.Server.Tunnel.PrivateKeyPemPath;
        yield return loaded.Server.ClientsDirectory;
        if (Directory.Exists(loaded.Server.ClientsDirectory))
        {
            foreach (var client in Directory.EnumerateFiles(loaded.Server.ClientsDirectory, "*.json"))
                yield return client;
        }

        if (loaded.Server.Audit?.FilePath is { Length: > 0 } audit)
        {
            yield return audit;
            yield return audit + "-wal";
            yield return audit + "-shm";
            yield return Path.GetDirectoryName(audit)!;
        }

        if (loaded.Server.History?.FilePath is { Length: > 0 } history)
        {
            var database = TrafficHistoryService.DatabasePath(history);
            yield return database;
            yield return database + "-wal";
            yield return database + "-shm";
            yield return Path.GetDirectoryName(database)!;
        }
    }

    public static IReadOnlyList<string> FindLoose(IEnumerable<string> paths)
    {
        var loose = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (IsLoose(path)) loose.Add(path);
        }

        return loose;
    }

    public static string LooseWarning(string path) =>
        $"SECURITY: {path} is readable by users other than the service account. Client secrets, access secrets, and audit data must be mode 0600, and their directories mode 0700.";

    private static void ProtectIfExists(string path)
    {
        if (!File.Exists(path) && !IsLink(path)) return;
        ProtectFile(ResolveDatabaseFile(path), path);
    }

    private static void ProtectFile(string file, string requested)
    {
        if (IsLink(file))
            throw Refusal(requested, file, "the path is a symbolic link");
        if (Directory.Exists(file))
            throw Refusal(requested, file, "the path is a directory");
        RestrictFile(file);
    }

    internal static string ResolveDatabaseFile(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            throw Refusal(path, full, "the path has no root");
        var rest = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rest.Length == 0)
            throw Refusal(path, full, "the path is the filesystem root");
        var segments = rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0 || segment == ".") continue;
            var next = Path.Combine(current, segment);
            var final = index == segments.Length - 1;
            if (IsLink(next))
            {
                if (final)
                    throw Refusal(path, next, "the database path is a symbolic link");
                next = FollowDirectoryLink(next, path);
            }

            if (!final)
            {
                if (File.Exists(next) && !Directory.Exists(next))
                    throw Refusal(path, next, "a parent path is a file");
                current = next;
                continue;
            }

            if (IsFilesystemRoot(current))
                throw Refusal(path, next, "the database resolves under the filesystem root");
            if (Directory.Exists(next) && !File.Exists(next))
                throw Refusal(path, next, "the database path is a directory");
            return next;
        }

        throw Refusal(path, full, "the path has no file name");
    }

    private static string FollowDirectoryLink(string link, string requested)
    {
        var directory = new DirectoryInfo(link);
        var resolved = directory.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw Refusal(requested, link, "a parent symbolic link cannot be resolved");
        if (!resolved.Exists || (resolved.Attributes & FileAttributes.Directory) == 0)
            throw Refusal(requested, link, "a parent symbolic link does not resolve to a directory");
        return resolved.FullName;
    }

    private static bool IsLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) return true;
        return new DirectoryInfo(path).LinkTarget is not null;
    }

    private static bool IsFilesystemRoot(string path)
    {
        var normalized = NormalizeDirectory(path);
        var root = Path.GetPathRoot(normalized);
        return !string.IsNullOrEmpty(root)
            && string.Equals(normalized, NormalizeDirectory(root), PathComparison);
    }

    private static void EnsureDatabaseDirectory(string parent, string requested)
    {
        if (Directory.Exists(parent))
        {
            if (IsUnsafeDatabaseDirectory(parent))
                throw new InvalidOperationException($"SECURITY: Refusing to open database '{requested}' because '{parent}' is a shared or temporary directory. Move the audit or history file into a directory used only by RelayLink. Directory permissions were not changed.");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            CreatePrivateWindowsDirectory(parent);
            return;
        }

        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, PrivateDirectory);
    }

    private static bool IsUnsafeDatabaseDirectory(string path)
    {
        var full = NormalizeDirectory(path);
        if (IsFilesystemRoot(full) || IsKnownSharedDirectory(full)) return true;
        if (!Directory.Exists(full)) return false;
        if (!OperatingSystem.IsWindows())
            return (File.GetUnixFileMode(full) & UnixFileMode.OtherWrite) != 0;
        return WindowsHasLooseDirectoryAccess(full);
    }

    private static bool IsKnownSharedDirectory(string normalized)
    {
        if (string.Equals(normalized, NormalizeDirectory(Path.GetTempPath()), PathComparison)) return true;
        if (OperatingSystem.IsWindows()) return false;
        foreach (var candidate in new[] { "/tmp", "/var/tmp", "/usr/tmp", "/dev/shm", "/private/tmp", "/private/var/tmp" })
        {
            if (string.Equals(normalized, candidate, PathComparison)) return true;
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsHasLooseDirectoryAccess(string path)
    {
        AuthorizationRuleCollection rules;
        try
        {
            rules = new DirectoryInfo(path).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        var current = WindowsIdentity.GetCurrent().User;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || IsServiceOrAdministrator(sid, current)) continue;
            var appliesToThisFolder = (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0;
            var appliesToNewFiles = (rule.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0;
            if (appliesToNewFiles && (rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData)) != 0)
                return true;
            const FileSystemRights dangerousDirectoryRights =
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Write
                | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions;
            if (appliesToThisFolder && (rule.FileSystemRights & dangerousDirectoryRights) != 0)
                return true;
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static void CreatePrivateWindowsDirectory(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(path);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static InvalidOperationException Refusal(string requested, string resolved, string reason) =>
        new($"SECURITY: Refusing to open database '{requested}' ({resolved}): {reason}. Directory permissions were not changed.");

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFile);
            return;
        }

        RestrictWindows(path);
    }

    private static bool IsLoose(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetUnixFileMode(path) & LooseBits) != 0;
        }

        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        return WindowsAllowsBroadRead(path);
    }

    private static FileStream CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows()) return CreatePrivateWindows(path);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = PrivateFile
        });
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreatePrivateWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsAllowsBroadRead(string path)
    {
        AuthorizationRuleCollection rules;
        try
        {
            FileSystemSecurity security = Directory.Exists(path) && !File.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var current = WindowsIdentity.GetCurrent().User;
        var directory = Directory.Exists(path) && !File.Exists(path);
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            // Read also includes attribute and DACL metadata. Only content access
            // (ReadData, the same bit as ListDirectory) means the secret is readable.
            if ((rule.FileSystemRights & FileSystemRights.ReadData) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || IsServiceOrAdministrator(sid, current)) continue;
            var inheritOnly = (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0;
            var appliesToChildren = rule.InheritanceFlags != InheritanceFlags.None;
            if (inheritOnly && !(directory && appliesToChildren)) continue;
            return true;
        }

        return false;
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [SupportedOSPlatform("windows")]
    private static bool IsServiceOrAdministrator(SecurityIdentifier sid, SecurityIdentifier? current) =>
        (current is not null && sid == current)
        || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
}
