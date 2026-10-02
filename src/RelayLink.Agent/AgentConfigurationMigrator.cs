using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RelayLink.Agent;

public static class AgentConfigurationMigrator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static void MigrateInstalled(string path) => MigrateInstalled(path, null);

    internal static void MigrateInstalled(string path, Action<MigrationStage>? checkpoint)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        using var document = ReadDocument(fullPath);
        if (IsNewFormat(document.RootElement))
        {
            AgentProcessConfigurationLoader.Parse(document.RootElement);
            return;
        }

        var legacy = AgentConfigurationLoader.Load(fullPath);
        var candidate = CreateNewConfiguration(legacy);
        var newJson = JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions);
        using (var parsed = JsonDocument.Parse(newJson)) AgentProcessConfigurationLoader.Parse(parsed.RootElement);

        var stateDirectory = Path.Combine(directory, "state", "primary");
        CreateProtectedDirectory(stateDirectory);
        PrepareState(legacy.E2eIdentityPath, Path.Combine(stateDirectory, "identity.pfx"), isIdentity: true);
        checkpoint?.Invoke(MigrationStage.IdentityPrepared);
        PrepareState(legacy.PortStatePath, Path.Combine(stateDirectory, "ports.json"), isIdentity: false);
        checkpoint?.Invoke(MigrationStage.PortsPrepared);
        PrepareProtectedCopy(fullPath, fullPath + ".legacy");
        checkpoint?.Invoke(MigrationStage.BackupPrepared);
        AtomicWrite(fullPath, newJson, overwrite: true);
        checkpoint?.Invoke(MigrationStage.ConfigurationCommitted);
    }

    public static void ConvertFile(string inputPath, string outputPath)
    {
        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (string.Equals(input, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new AgentConfigurationException("Input and output paths must be different.");
        using var document = ReadDocument(input);
        if (IsNewFormat(document.RootElement))
            throw new AgentConfigurationException("Input is already in the new Agent format.");
        var legacy = AgentConfigurationLoader.Load(input);
        var candidate = CreateNewConfiguration(legacy);
        var newJson = JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions);
        using (var parsed = JsonDocument.Parse(newJson)) AgentProcessConfigurationLoader.Parse(parsed.RootElement);
        if (File.Exists(output)) throw new AgentConfigurationException("Output configuration already exists.");
        AtomicWrite(output, newJson, overwrite: false);
    }

    private static AgentProcessConfiguration CreateNewConfiguration(AgentConfiguration legacy)
    {
        var ca = legacy.UseTls ? legacy.TrustedCaPemBase64 : null;
        if (legacy.UseTls && ca is null)
            ca = Convert.ToBase64String(File.ReadAllBytes(legacy.TrustedCaPemPath!));
        return new AgentProcessConfiguration
        {
            DashboardPort = legacy.DashboardPort,
            OutboundPortRangeStart = legacy.OutboundPortRangeStart,
            OutboundPortRangeEnd = legacy.OutboundPortRangeEnd,
            Servers =
            [
                new AgentServerProfile
                {
                    ProfileId = "primary", ServerHost = legacy.ServerHost, ServerPort = legacy.ServerPort,
                    DataPort = legacy.EffectiveDataPort, UseTls = legacy.UseTls, ClientId = legacy.ClientId,
                    Secret = legacy.Secret, TrustedCaPemBase64 = ca, Reconnect = legacy.Reconnect
                }
            ]
        };
    }

    private static JsonDocument ReadDocument(string path)
    {
        try
        {
            ReadOnlyMemory<byte> content = File.ReadAllBytes(path);
            // Windows PowerShell 5.1 wrote installed JSON with a UTF-8 BOM.
            if (content.Length >= 3 && content.Span[0] == 0xEF && content.Span[1] == 0xBB && content.Span[2] == 0xBF)
                content = content[3..];
            return JsonDocument.Parse(content);
        }
        catch (JsonException) { throw new AgentConfigurationException("Invalid Agent JSON configuration."); }
        catch (IOException) { throw new AgentConfigurationException("Unable to read Agent configuration."); }
        catch (UnauthorizedAccessException) { throw new AgentConfigurationException("Unable to read Agent configuration."); }
    }

    private static bool IsNewFormat(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("servers", out _);

    private static void PrepareState(string sourcePath, string destinationPath, bool isIdentity)
    {
        if (!File.Exists(sourcePath))
        {
            if (File.Exists(destinationPath)) throw new AgentConfigurationException("Prepared Agent state has no matching legacy source.");
            return;
        }
        var source = File.ReadAllBytes(sourcePath);
        ValidateState(source, isIdentity);
        if (File.Exists(destinationPath))
        {
            var destination = File.ReadAllBytes(destinationPath);
            ValidateState(destination, isIdentity);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(destination)))
                throw new AgentConfigurationException("Prepared Agent state differs from legacy state.");
            return;
        }
        AtomicWrite(destinationPath, source, overwrite: false);
        var prepared = File.ReadAllBytes(destinationPath);
        ValidateState(prepared, isIdentity);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(prepared)))
            throw new AgentConfigurationException("Prepared Agent state verification failed.");
    }

    private static void ValidateState(byte[] content, bool isIdentity)
    {
        if (content.Length is < 1 or > 1048576) throw new AgentConfigurationException("Legacy Agent state has invalid size.");
        if (isIdentity)
        {
            try
            {
                var keyStorage = OperatingSystem.IsMacOS()
                    ? X509KeyStorageFlags.DefaultKeySet
                    : X509KeyStorageFlags.EphemeralKeySet;
                using var certificate = X509CertificateLoader.LoadPkcs12(content, password: null, keyStorage);
                if (!certificate.HasPrivateKey) throw new AgentConfigurationException("Legacy Agent identity has no private key.");
                _ = SHA256.HashData(certificate.RawData);
            }
            catch (CryptographicException) { throw new AgentConfigurationException("Legacy Agent identity is invalid."); }
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new AgentConfigurationException("Legacy port state is invalid.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in document.RootElement.EnumerateObject())
                    if (!keys.Add(entry.Name) || string.IsNullOrWhiteSpace(entry.Name) ||
                        entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt32(out var port) || port is < 1024 or > 65535)
                        throw new AgentConfigurationException("Legacy port state is invalid.");
            }
            catch (JsonException) { throw new AgentConfigurationException("Legacy port state is invalid."); }
        }
    }

    private static void PrepareProtectedCopy(string sourcePath, string backupPath)
    {
        var source = File.ReadAllBytes(sourcePath);
        if (File.Exists(backupPath))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source), SHA256.HashData(File.ReadAllBytes(backupPath))))
                throw new AgentConfigurationException("Protected legacy configuration backup differs from current input.");
            return;
        }
        AtomicWrite(backupPath, source, overwrite: false);
    }

    private static void CreateProtectedDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void AtomicWrite(string path, byte[] content, bool overwrite)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = CreateProtectedFile(temporary))
            {
                file.Write(content);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream CreateProtectedFile(string path)
    {
        // On Windows the service account inherits access from the protected
        // configuration directory even when an administrator runs migration.
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }
}

internal enum MigrationStage { IdentityPrepared, PortsPrepared, BackupPrepared, ConfigurationCommitted }
