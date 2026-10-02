using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayLink.Agent;

namespace RelayLink.UnitTests;

public sealed class AgentConfigurationMigratorTests
{
    [Fact]
    public void Installed_legacy_config_with_windows_powershell_utf8_bom_migrates()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-migration-bom-");
        try
        {
            var configurationPath = Path.Combine(directory.FullName, "agent.json");
            var legacyBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetPreamble().Concat(Encoding.UTF8.GetBytes(LegacyJson(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))))).ToArray();
            File.WriteAllBytes(configurationPath, legacyBytes);

            AgentConfigurationMigrator.MigrateInstalled(configurationPath);

            Assert.Equal(legacyBytes, File.ReadAllBytes(configurationPath + ".legacy"));
            Assert.Equal("primary", Assert.Single(AgentProcessConfigurationLoader.Load(configurationPath).Servers).ProfileId);
            AgentConfigurationMigrator.MigrateInstalled(configurationPath);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Installed_legacy_config_preserves_identity_ports_and_credentials_on_retry()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-migration-");
        try
        {
            var configurationPath = Path.Combine(directory.FullName, "agent.json");
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var legacy = LegacyJson(secret);
            File.WriteAllText(configurationPath, legacy);
            var identity = CreateIdentity();
            File.WriteAllBytes(Path.Combine(directory.FullName, "same.e2e.pfx"), identity);
            File.WriteAllText(Path.Combine(directory.FullName, "same.ports.json"), """{"mapping":20123}""");

            AgentConfigurationMigrator.MigrateInstalled(configurationPath);

            var migrated = AgentProcessConfigurationLoader.Load(configurationPath);
            var profile = Assert.Single(migrated.Servers);
            Assert.Equal("primary", profile.ProfileId);
            Assert.Equal("same", profile.ClientId);
            Assert.Equal(secret, profile.Secret);
            Assert.Equal(7444, profile.DataPort);
            Assert.Equal(20123, JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "state", "primary", "ports.json"))).RootElement.GetProperty("mapping").GetInt32());
            Assert.Equal(identity, File.ReadAllBytes(Path.Combine(directory.FullName, "state", "primary", "identity.pfx")));
            Assert.Equal(legacy, File.ReadAllText(configurationPath + ".legacy"));

            var newBytes = File.ReadAllBytes(configurationPath);
            AgentConfigurationMigrator.MigrateInstalled(configurationPath);
            Assert.Equal(newBytes, File.ReadAllBytes(configurationPath));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Conflicting_prepared_state_keeps_legacy_config_untouched()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-migration-");
        try
        {
            var configurationPath = Path.Combine(directory.FullName, "agent.json");
            var legacy = LegacyJson(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            File.WriteAllText(configurationPath, legacy);
            File.WriteAllText(Path.Combine(directory.FullName, "same.ports.json"), """{"mapping":20123}""");
            var state = Directory.CreateDirectory(Path.Combine(directory.FullName, "state", "primary"));
            File.WriteAllText(Path.Combine(state.FullName, "ports.json"), """{"mapping":20124}""");

            Assert.Throws<AgentConfigurationException>(() => AgentConfigurationMigrator.MigrateInstalled(configurationPath));
            Assert.Equal(legacy, File.ReadAllText(configurationPath));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Single_file_conversion_does_not_touch_installed_state()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-migration-");
        try
        {
            var input = Path.Combine(directory.FullName, "download.json");
            var output = Path.Combine(directory.FullName, "converted.json");
            var legacy = LegacyJson(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            File.WriteAllText(input, legacy);

            AgentConfigurationMigrator.ConvertFile(input, output);

            Assert.Equal(legacy, File.ReadAllText(input));
            Assert.Equal("primary", Assert.Single(AgentProcessConfigurationLoader.Load(output).Servers).ProfileId);
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "state")));
            Assert.Throws<AgentConfigurationException>(() => AgentConfigurationMigrator.ConvertFile(input, output));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Interrupted_migration_can_retry_without_losing_legacy_identity(int stageNumber)
    {
        var failureStage = (MigrationStage)stageNumber;
        var directory = Directory.CreateTempSubdirectory("relaylink-migration-fault-");
        try
        {
            var configurationPath = Path.Combine(directory.FullName, "agent.json");
            var legacy = LegacyJson(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var identity = CreateIdentity();
            File.WriteAllText(configurationPath, legacy);
            File.WriteAllBytes(Path.Combine(directory.FullName, "same.e2e.pfx"), identity);
            File.WriteAllText(Path.Combine(directory.FullName, "same.ports.json"), """{"mapping":20123}""");

            Assert.Throws<IOException>(() => AgentConfigurationMigrator.MigrateInstalled(configurationPath, stage =>
            {
                if (stage == failureStage) throw new IOException("Injected interruption.");
            }));
            Assert.Equal(identity, File.ReadAllBytes(Path.Combine(directory.FullName, "same.e2e.pfx")));
            if (failureStage != MigrationStage.ConfigurationCommitted)
                Assert.Equal(legacy, File.ReadAllText(configurationPath));
            else
                Assert.Single(AgentProcessConfigurationLoader.Load(configurationPath).Servers);

            AgentConfigurationMigrator.MigrateInstalled(configurationPath);
            var migrated = AgentProcessConfigurationLoader.Load(configurationPath);
            Assert.Equal("primary", Assert.Single(migrated.Servers).ProfileId);
            Assert.Equal(identity, File.ReadAllBytes(Path.Combine(directory.FullName, "state", "primary", "identity.pfx")));
            Assert.Equal(20123, JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "state", "primary", "ports.json")))
                .RootElement.GetProperty("mapping").GetInt32());
            Assert.Equal(legacy, File.ReadAllText(configurationPath + ".legacy"));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static string LegacyJson(string secret) => JsonSerializer.Serialize(new
    {
        serverHost = "127.0.0.1", serverPort = 7443, clientId = "same", useTls = false, secret,
        reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 30, permanentErrorDelaySeconds = 60 }
    });

    private static byte[] CreateIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=RelayLink-same", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.Export(X509ContentType.Pkcs12);
    }
}
