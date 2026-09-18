using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayLink.Agent;

public sealed record AgentConfiguration(string ServerHost, int ServerPort, string ClientId, string Secret, bool UseTls, string? TrustedCaPemPath, ReconnectConfiguration Reconnect)
{
    public int OutboundPortRangeStart { get; init; } = 20000;
    public int OutboundPortRangeEnd { get; init; } = 59999;
    public int DashboardPort { get; init; } = 18081;
    [JsonIgnore]
    public string E2eIdentityPath { get; init; } = string.Empty;
    [JsonIgnore]
    public string PortStatePath { get; init; } = string.Empty;
}
public sealed record ReconnectConfiguration(int InitialDelaySeconds, int MaxDelaySeconds, int PermanentErrorDelaySeconds);

public static class AgentConfigurationLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AgentConfiguration Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            RejectDuplicates(document.RootElement);
            var configuration = document.RootElement.Deserialize<AgentConfiguration>(Options)
                ?? throw new AgentConfigurationException("Agent configuration is empty.");
            if (string.IsNullOrWhiteSpace(configuration.ServerHost) || configuration.ServerPort is < 1 or > 65535 || !System.Text.RegularExpressions.Regex.IsMatch(configuration.ClientId ?? string.Empty, "^[a-z0-9][a-z0-9_-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) || configuration.Reconnect is null || configuration.Reconnect.InitialDelaySeconds is < 1 or > 60 || configuration.Reconnect.MaxDelaySeconds < configuration.Reconnect.InitialDelaySeconds || configuration.Reconnect.MaxDelaySeconds > 300 || configuration.Reconnect.PermanentErrorDelaySeconds is < 1 or > 3600 || configuration.OutboundPortRangeStart is < 1024 or > 65535 || configuration.OutboundPortRangeEnd < configuration.OutboundPortRangeStart || configuration.OutboundPortRangeEnd > 65535 || configuration.DashboardPort is < 0 or > 65535)
            {
                throw new AgentConfigurationException("Agent configuration contains invalid values.");
            }

            var secret = Convert.FromBase64String(configuration.Secret);
            if (secret.Length < 32) throw new AgentConfigurationException("Agent secret must be at least 32 bytes.");
            var configurationDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            configuration = configuration with { E2eIdentityPath = Path.Combine(configurationDirectory, $"{configuration.ClientId}.e2e.pfx"), PortStatePath = Path.Combine(configurationDirectory, $"{configuration.ClientId}.ports.json") };
            if (!configuration.UseTls) return configuration;
            if (string.IsNullOrWhiteSpace(configuration.TrustedCaPemPath))
            {
                throw new AgentConfigurationException("trustedCaPemPath is required; operating-system trust stores are not used.");
            }

            var trustedCaPath = Path.GetFullPath(configuration.TrustedCaPemPath, configurationDirectory);
            if (!File.Exists(trustedCaPath)) throw new AgentConfigurationException("Trusted CA certificate file does not exist.");
            try
            {
                var certificates = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
                certificates.ImportFromPemFile(trustedCaPath);
                if (certificates.Count == 0 || certificates.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>().Any(certificate => certificate.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true }))
                {
                    throw new AgentConfigurationException("Trusted CA PEM must contain only CA certificates.");
                }
            }
            catch (System.Security.Cryptography.CryptographicException exception)
            {
                throw new AgentConfigurationException($"Unable to load trusted CA certificate: {exception.Message}");
            }

            return configuration with { TrustedCaPemPath = trustedCaPath };
        }
        catch (JsonException exception) { throw new AgentConfigurationException($"Invalid agent JSON: {exception.Message}"); }
        catch (FormatException) { throw new AgentConfigurationException("Agent secret must be Base64."); }
        catch (IOException exception) { throw new AgentConfigurationException($"Unable to read agent configuration: {exception.Message}"); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new AgentConfigurationException($"Duplicate JSON property: {property.Name}");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
        }
    }
}

public sealed class AgentConfigurationException(string message) : Exception(message);
