using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayLink.Server.Runtime;

namespace RelayLink.Server.Configuration;

public sealed record InspectionPolicy
{
    public int ApprovalTimeoutSeconds { get; init; } = 60;
    public bool ApproveUnsafeMethods { get; init; }
    public IReadOnlyList<InspectionRule> Rules { get; init; } = [];
    public HttpsInspectionMaterial? Https { get; init; }
    public SshInspectionPolicy? Ssh { get; init; }

    public static bool Same(InspectionPolicy? left, InspectionPolicy? right) =>
        JsonSerializer.Serialize(left, SerializerOptions) == JsonSerializer.Serialize(right, SerializerOptions);

    public static InspectionPolicy? ResolvePaths(InspectionPolicy? policy, string directory)
    {
        if (policy?.Https is not { } https) return policy;
        return policy with
        {
            Https = https with
            {
                CertificatePemPath = Absolute(https.CertificatePemPath, directory),
                PrivateKeyPemPath = Absolute(https.PrivateKeyPemPath, directory),
                UpstreamCaPemPath = string.IsNullOrWhiteSpace(https.UpstreamCaPemPath) ? null : Absolute(https.UpstreamCaPemPath, directory)
            }
        };
    }

    public static void Validate(ChannelConfiguration channel, string owner, string directory)
    {
        var policy = channel.Inspection;
        if (policy is null)
        {
            if (channel.InspectionMode != "none" && channel.Application == "https")
                throw new ConfigurationException($"HTTPS inspection channel {owner} requires a certificate.");
            return;
        }
        if (channel.InspectionMode == "none" || channel.Application is not ("http" or "https" or "ssh"))
            throw new ConfigurationException($"Inspection policy is only valid on HTTP, HTTPS or SSH inspection channels for {owner}.");
        if (policy.ApprovalTimeoutSeconds is < 1 or > 300)
            throw new ConfigurationException($"Invalid inspection approval timeout for {owner}.");
        if (policy.Rules is null)
            throw new ConfigurationException($"Inspection rules cannot be null for {owner}.");
        if (channel.Application == "ssh")
        {
            if (policy.Https is not null || policy.Rules.Count != 0 || policy.ApproveUnsafeMethods)
                throw new ConfigurationException($"SSH inspection channel {owner} cannot carry HTTP policy fields.");
            var ssh = policy.Ssh;
            if (ssh is null) return;
            if (ssh.AllowTcpForwarding || ssh.AllowAgentForwarding || ssh.AllowX11Forwarding)
                throw new ConfigurationException($"SSH forwarding is not supported for {owner}; forwarding options must be false.");
            if (!string.IsNullOrWhiteSpace(ssh.TargetHostKeySha256) && !SshInspectionPolicy.TryParseHostKeyHash(ssh.TargetHostKeySha256, out _))
                throw new ConfigurationException($"SSH target host key for {owner} must be an OpenSSH SHA256 fingerprint.");
            if (ssh.DeniedCommandPrefixes is null || ssh.DeniedCommandPrefixes.Count > 64 || ssh.DeniedCommandPrefixes.Any(prefix =>
                string.IsNullOrWhiteSpace(prefix) || prefix.Length > 1024 || prefix.Any(char.IsControl)))
                throw new ConfigurationException($"Invalid SSH command prefix rules for {owner}.");
            return;
        }
        if (policy.Ssh is not null)
            throw new ConfigurationException($"HTTP inspection channel {owner} cannot carry SSH policy fields.");
        if (policy.Rules.Count > 64)
            throw new ConfigurationException($"Too many inspection rules for {owner}.");
        foreach (var rule in policy.Rules)
        {
            if (rule.Action is not ("allow" or "deny" or "approve"))
                throw new ConfigurationException($"Invalid inspection rule action for {owner}.");
            if (rule.Methods.Count > 16 || rule.Methods.Any(method => method.Length is < 1 or > 16 || method.Any(character => !char.IsAsciiLetter(character))))
                throw new ConfigurationException($"Invalid inspection rule method for {owner}.");
            if (rule.Host is { Length: > 253 } || rule.Host?.Contains(' ') == true)
                throw new ConfigurationException($"Invalid inspection rule host for {owner}.");
            if (rule.PathPrefix is { Length: > 512 } || (rule.PathPrefix is not null && !rule.PathPrefix.StartsWith('/')))
                throw new ConfigurationException($"Invalid inspection rule path for {owner}.");
        }
        if (channel.Application == "http" && policy.Https is not null)
            throw new ConfigurationException($"HTTP inspection channel {owner} cannot carry a TLS certificate.");
        if (channel.Application == "https")
        {
            if (policy.Https is null) throw new ConfigurationException($"HTTPS inspection channel {owner} requires a certificate.");
            var https = ResolvePaths(policy, directory)!.Https!;
            try
            {
                using var certificate = ServerCertificateLoader.Load(https.CertificatePemPath, https.PrivateKeyPemPath);
                if (!certificate.HasPrivateKey) throw new CryptographicException("Certificate did not load a private key.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                throw new ConfigurationException($"HTTPS inspection certificate for {owner} could not be loaded from '{https.CertificatePemPath}': {exception.Message}");
            }
            if (string.IsNullOrWhiteSpace(https.UpstreamCaPemPath)) return;
            try
            {
                using var authority = X509Certificate2.CreateFromPem(File.ReadAllText(https.UpstreamCaPemPath));
                if (authority.HasPrivateKey) throw new CryptographicException("Upstream CA must be a public certificate.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                throw new ConfigurationException($"HTTPS upstream CA for {owner} could not be loaded from '{https.UpstreamCaPemPath}': {exception.Message}");
            }
        }
    }

    private static string Absolute(string path, string directory) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(path, directory);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record InspectionRule
{
    public IReadOnlyList<string> Methods { get; init; } = [];
    public string? Host { get; init; }
    public string? PathPrefix { get; init; }
    public string Action { get; init; } = "allow";
}

public sealed record SshInspectionPolicy
{
    // Missing trust leaves a legacy channel unavailable without preventing Server startup.
    public string? TargetHostKeySha256 { get; init; }
    public bool AllowShell { get; init; } = true;
    public bool AllowExec { get; init; } = true;
    public bool AllowSubsystem { get; init; }
    public bool AllowAgentForwarding { get; init; }
    public bool AllowX11Forwarding { get; init; }
    public bool AllowTcpForwarding { get; init; }
    public bool RequireApproval { get; init; }
    public IReadOnlyList<string> DeniedCommandPrefixes { get; init; } = [];

    internal static bool TryParseHostKeyHash(string? fingerprint, out byte[] hash)
    {
        hash = [];
        if (fingerprint is null || fingerprint.Length != 50 || !fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)) return false;
        try
        {
            var decoded = Convert.FromBase64String(fingerprint[7..] + "=");
            if (decoded.Length != 32 || "SHA256:" + Convert.ToBase64String(decoded).TrimEnd('=') != fingerprint) return false;
            hash = decoded;
            return true;
        }
        catch (FormatException) { return false; }
    }
}

public sealed record HttpsInspectionMaterial
{
    public string CertificatePemPath { get; init; } = "";
    public string PrivateKeyPemPath { get; init; } = "";
    public string? UpstreamName { get; init; }
    public string? UpstreamCaPemPath { get; init; }
}
