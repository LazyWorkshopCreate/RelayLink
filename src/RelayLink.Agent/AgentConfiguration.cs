using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RelayLink.Agent;

public sealed record AgentConfiguration(string ServerHost, int ServerPort, string ClientId, string Secret, bool UseTls, string? TrustedCaPemPath, ReconnectConfiguration Reconnect)
{
    public int DataPort { get; init; }
    [JsonIgnore]
    public int EffectiveDataPort => DataPort == 0 ? ServerPort + 1 : DataPort;
    public string? TrustedCaPemBase64 { get; init; }
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
            if (string.IsNullOrWhiteSpace(configuration.ServerHost) || configuration.ServerPort is < 1 or > 65535 || configuration.EffectiveDataPort is < 1 or > 65535 || configuration.EffectiveDataPort == configuration.ServerPort || !System.Text.RegularExpressions.Regex.IsMatch(configuration.ClientId ?? string.Empty, "^[a-z0-9][a-z0-9_-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) || configuration.Reconnect is null || configuration.Reconnect.InitialDelaySeconds is < 1 or > 60 || configuration.Reconnect.MaxDelaySeconds < configuration.Reconnect.InitialDelaySeconds || configuration.Reconnect.MaxDelaySeconds > 300 || configuration.Reconnect.PermanentErrorDelaySeconds is < 1 or > 3600 || configuration.OutboundPortRangeStart is < 1024 or > 65535 || configuration.OutboundPortRangeEnd < configuration.OutboundPortRangeStart || configuration.OutboundPortRangeEnd > 65535 || configuration.DashboardPort is < 0 or > 65535)
            {
                throw new AgentConfigurationException("Agent configuration contains invalid values.");
            }

            var secret = Convert.FromBase64String(configuration.Secret);
            if (secret.Length < 32) throw new AgentConfigurationException("Agent secret must be at least 32 bytes.");
            var configurationDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            configuration = configuration with { E2eIdentityPath = Path.Combine(configurationDirectory, $"{configuration.ClientId}.e2e.pfx"), PortStatePath = Path.Combine(configurationDirectory, $"{configuration.ClientId}.ports.json") };
            if (!configuration.UseTls) return configuration;
            var inlineCa = !string.IsNullOrWhiteSpace(configuration.TrustedCaPemBase64);
            var fileCa = !string.IsNullOrWhiteSpace(configuration.TrustedCaPemPath);
            if (inlineCa == fileCa) throw new AgentConfigurationException("Exactly one of trustedCaPemBase64 or legacy trustedCaPemPath is required; operating-system trust stores are not used.");
            if (fileCa)
            {
                var trustedCaPath = Path.GetFullPath(configuration.TrustedCaPemPath!, configurationDirectory);
                if (!File.Exists(trustedCaPath)) throw new AgentConfigurationException("Trusted CA certificate file does not exist.");
                configuration = configuration with { TrustedCaPemPath = trustedCaPath };
            }
            foreach (var certificate in LoadTrustedRoots(configuration).Cast<X509Certificate2>()) certificate.Dispose();
            return configuration;
        }
        catch (JsonException exception) { throw new AgentConfigurationException($"Invalid agent JSON: {exception.Message}"); }
        catch (FormatException) { throw new AgentConfigurationException("Agent secret must be Base64."); }
        catch (IOException exception) { throw new AgentConfigurationException($"Unable to read agent configuration: {exception.Message}"); }
    }

    internal static X509Certificate2Collection LoadTrustedRoots(AgentConfiguration configuration)
    {
        string pem;
        if (!string.IsNullOrWhiteSpace(configuration.TrustedCaPemBase64))
        {
            try
            {
                var bytes = Convert.FromBase64String(configuration.TrustedCaPemBase64);
                if (bytes.Length > 262144) throw new AgentConfigurationException("Trusted CA PEM is too large.");
                pem = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (FormatException) { throw new AgentConfigurationException("trustedCaPemBase64 must be valid Base64."); }
            catch (DecoderFallbackException) { throw new AgentConfigurationException("trustedCaPemBase64 must contain UTF-8 PEM text."); }
        }
        else
        {
            pem = File.ReadAllText(configuration.TrustedCaPemPath!);
            if (Encoding.UTF8.GetByteCount(pem) > 262144) throw new AgentConfigurationException("Trusted CA PEM is too large.");
        }

        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) throw new AgentConfigurationException("Trusted CA PEM must not contain a private key.");
        try
        {
            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPem(pem);
            if (certificates.Count == 0 || certificates.Cast<X509Certificate2>().Any(certificate => certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true }))
            {
                foreach (var certificate in certificates) certificate.Dispose();
                throw new AgentConfigurationException("Trusted CA PEM must contain only CA certificates.");
            }
            return certificates;
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            throw new AgentConfigurationException($"Unable to load trusted CA certificate: {exception.Message}");
        }
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
