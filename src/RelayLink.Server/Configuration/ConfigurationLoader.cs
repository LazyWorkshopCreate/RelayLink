using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RelayLink.Protocol;

namespace RelayLink.Server.Configuration;

public sealed class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public LoadedConfiguration Load(string serverConfigurationPath)
    {
        if (string.IsNullOrWhiteSpace(serverConfigurationPath))
        {
            throw new ConfigurationException("The server configuration path is required.");
        }

        var serverPath = Path.GetFullPath(serverConfigurationPath);
        var server = Deserialize<ServerConfiguration>(serverPath);
        if (server.Tunnel is null)
        {
            throw new ConfigurationException($"Unsupported or incomplete server configuration: {serverPath}");
        }
        var configurationDirectory = Path.GetDirectoryName(serverPath)!;
        server = server with
        {
            Tunnel = server.Tunnel with
            {
                CertificatePemPath = string.IsNullOrWhiteSpace(server.Tunnel.CertificatePemPath) ? string.Empty : Path.GetFullPath(server.Tunnel.CertificatePemPath, configurationDirectory),
                PrivateKeyPemPath = string.IsNullOrWhiteSpace(server.Tunnel.PrivateKeyPemPath) ? string.Empty : Path.GetFullPath(server.Tunnel.PrivateKeyPemPath, configurationDirectory),
                TrustedCaPemPath = string.IsNullOrWhiteSpace(server.Tunnel.TrustedCaPemPath) ? null : Path.GetFullPath(server.Tunnel.TrustedCaPemPath, configurationDirectory)
            }
        };
        if (server.History is not null)
        {
            server = server with { History = server.History with { FilePath = Path.GetFullPath(server.History.FilePath, configurationDirectory) } };
        }
        ValidateServer(server, serverPath);

        var clientsDirectory = Path.GetFullPath(server.ClientsDirectory, configurationDirectory);
        if (!Directory.Exists(clientsDirectory))
        {
            throw new ConfigurationException($"Clients directory does not exist: {clientsDirectory}");
        }

        var clients = new Dictionary<string, ClientConfiguration>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(clientsDirectory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            var client = Deserialize<ClientConfiguration>(path);
            ValidateClient(client, path, server);
            if (!Path.GetFileNameWithoutExtension(path).Equals(client.ClientId, StringComparison.Ordinal))
            {
                throw new ConfigurationException($"Client file name must equal clientId: {path}");
            }

            if (!clients.TryAdd(client.ClientId, client))
            {
                throw new ConfigurationException($"Duplicate clientId: {client.ClientId}");
            }
        }

        ValidateGlobalConflicts(server, clients.Values);
        return new LoadedConfiguration(server with { ClientsDirectory = clientsDirectory }, clients);
    }

    private static T Deserialize<T>(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            RejectDuplicateProperties(json, path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new ConfigurationException($"Configuration is empty: {path}");
        }
        catch (JsonException exception)
        {
            throw new ConfigurationException($"Invalid JSON in {path}: {exception.Message}");
        }
        catch (IOException exception)
        {
            throw new ConfigurationException($"Unable to read {path}: {exception.Message}");
        }
    }

    private static void RejectDuplicateProperties(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        ValidateObject(document.RootElement, path);
    }

    private static void ValidateObject(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ConfigurationException($"Duplicate JSON property '{property.Name}' in {path}.");
                }

                ValidateObject(property.Value, path);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateObject(item, path);
            }
        }
    }

    private static void ValidateServer(ServerConfiguration server, string path)
    {
        if (server.SchemaVersion != 1 || server.Tunnel is null || server.Dashboard is null || server.Dashboard.Admin is null || server.Limits is null)
        {
            throw new ConfigurationException($"Unsupported or incomplete server configuration: {path}");
        }

        ValidateAddressAndPort(server.Tunnel.ListenAddress, server.Tunnel.Port, "tunnel endpoint");
        ValidateAddressAndPort(server.Tunnel.ListenAddress, server.Tunnel.EffectiveDataPort, "data endpoint");
        if (server.Tunnel.Port == server.Tunnel.EffectiveDataPort)
            throw new ConfigurationException("Control and data endpoints must use different ports.");
        if (server.Tunnel.AgentServerHost is { } agentServerHost &&
            (string.IsNullOrWhiteSpace(agentServerHost) ||
             Uri.CheckHostName(agentServerHost) == UriHostNameType.Unknown ||
             agentServerHost is "0.0.0.0" or "::"))
        {
            throw new ConfigurationException("Tunnel agentServerHost must be a DNS name or a concrete IP address.");
        }
        ValidateAddressAndPort(server.Dashboard.ListenAddress, server.Dashboard.Port, "dashboard endpoint");
        if ((server.Tunnel.Port == server.Dashboard.Port || server.Tunnel.EffectiveDataPort == server.Dashboard.Port) &&
            EndpointsOverlap(server.Tunnel.ListenAddress, server.Dashboard.ListenAddress))
        {
            throw new ConfigurationException("Control/data and dashboard endpoints conflict.");
        }

        if (!IsIdentifier(server.Dashboard.Admin.Username) || string.IsNullOrWhiteSpace(server.Dashboard.Admin.PasswordHash) || server.Dashboard.Admin.SessionLifetimeMinutes is < 5 or > 720)
        {
            throw new ConfigurationException("Dashboard administrator configuration is invalid.");
        }

        if (server.Tunnel.HandshakeTimeoutSeconds is < 1 or > 300 || server.Tunnel.HeartbeatIntervalSeconds is < 1 or > 300 || server.Tunnel.HeartbeatTimeoutSeconds < server.Tunnel.HeartbeatIntervalSeconds)
        {
            throw new ConfigurationException("Tunnel timeout values are invalid.");
        }

        if (server.Tunnel.TlsEnabled) try
        {
            using var certificate = X509Certificate2.CreateFromPemFile(server.Tunnel.CertificatePemPath, server.Tunnel.PrivateKeyPemPath);
            if (!certificate.HasPrivateKey) throw new CryptographicException("Certificate did not load a private key.");
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException)
        {
            throw new ConfigurationException($"Unable to load tunnel certificate and private key: {exception.Message}");
        }

        if (server.Tunnel.TlsEnabled && server.Tunnel.TrustedCaPemPath is { } caPath)
        {
            try
            {
                if (new FileInfo(caPath).Length > 262144) throw new ConfigurationException("Trusted CA PEM is too large.");
                var pem = File.ReadAllText(caPath);
                if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) throw new ConfigurationException("Trusted CA PEM must not contain a private key.");
                var certificates = new X509Certificate2Collection();
                certificates.ImportFromPem(pem);
                if (certificates.Count == 0 || certificates.Cast<X509Certificate2>().Any(certificate => certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true }))
                    throw new ConfigurationException("Trusted CA PEM must contain only CA certificates.");
                foreach (var certificate in certificates) certificate.Dispose();
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException)
            {
                throw new ConfigurationException($"Unable to load tunnel trusted CA PEM: {exception.Message}");
            }
        }

        if (server.Limits.MaxConnections < 1 || server.Limits.MaxPendingConnections < 1 || server.Limits.MaxPendingConnections > server.Limits.MaxConnections || server.Limits.MaxUnauthenticatedConnections < 1 || server.Limits.MaxChannelsPerClient < 1 || server.Limits.OpenTimeoutSeconds < 1 || server.Limits.BlockedWriteTimeoutSeconds < 1 || server.Limits.HalfCloseDrainTimeoutSeconds < 1)
        {
            throw new ConfigurationException("Server limits are invalid.");
        }

        if (server.History is { Enabled: true } history && (string.IsNullOrWhiteSpace(history.FilePath) || history.SampleIntervalSeconds is < 1 or > 3600 || history.RetentionDays is < 1 or > 3650))
        {
            throw new ConfigurationException("History configuration is invalid.");
        }
    }

    private static void ValidateClient(ClientConfiguration client, string path, ServerConfiguration server)
    {
        if (!IsIdentifier(client.ClientId))
        {
            throw new ConfigurationException("Client ID must be 1–64 characters: lowercase a–z, digits, '_' or '-', starting with a letter or digit.");
        }

        if (client.SchemaVersion != 1 || string.IsNullOrWhiteSpace(client.DisplayName) || client.Channels is null || client.OutboundMappings is null)
        {
            throw new ConfigurationException($"Invalid client configuration: {path}");
        }

        if (client.MaxConnections < 1 || client.MaxConnections > server.Limits.MaxConnections || client.MaxPendingConnections < 1 || client.MaxPendingConnections > client.MaxConnections || client.Channels.Count > server.Limits.MaxChannelsPerClient || client.OutboundMappings.Count > server.Limits.MaxChannelsPerClient)
        {
            throw new ConfigurationException($"Invalid client limits: {client.ClientId}");
        }

        var secret = DecodeSecret(client.Secret, client.ClientId);
        if (secret.Length < 32)
        {
            throw new ConfigurationException($"Secret is shorter than 32 bytes for {client.ClientId}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in client.Channels)
        {
            if (!IsIdentifier(channel.ChannelId) || !ids.Add(channel.ChannelId) || string.IsNullOrWhiteSpace(channel.DisplayName))
            {
                throw new ConfigurationException($"Invalid or duplicate channelId for {client.ClientId}.");
            }

            ValidateAddressAndPort(channel.ListenAddress, channel.ListenPort, $"channel {channel.ChannelId} listener");
            ValidatePort(channel.TargetPort, $"channel {channel.ChannelId} target");
            if (string.IsNullOrWhiteSpace(channel.TargetHost) || channel.MaxConnections < 1 || channel.MaxConnections > client.MaxConnections || channel.TargetConnectTimeoutSeconds is < 1 or > 300)
            {
                throw new ConfigurationException($"Invalid channel values for {client.ClientId}/{channel.ChannelId}.");
            }
            if (channel.AuthorizedClientsOnly)
            {
                ValidateAccessSecret(channel.AccessSecret, $"{client.ClientId}/{channel.ChannelId}");
                ValidateFingerprint(channel.E2eCertificateSha256, $"{client.ClientId}/{channel.ChannelId}");
            }
        }

        var mappingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in client.OutboundMappings)
        {
            if (!IsIdentifier(mapping.MappingId) || !mappingIds.Add(mapping.MappingId) || mapping.LocalAddress != "127.0.0.1" || !IsIdentifier(mapping.TargetClientId) || !IsIdentifier(mapping.TargetChannelId))
                throw new ConfigurationException($"Invalid outbound mapping for {client.ClientId}.");
            ValidateAccessSecret(mapping.AccessSecret, $"{client.ClientId}/{mapping.MappingId}");
            ValidateFingerprint(mapping.TargetCertificateSha256, $"{client.ClientId}/{mapping.MappingId}");
        }
    }

    private static void ValidateGlobalConflicts(ServerConfiguration server, IEnumerable<ClientConfiguration> clients)
    {
        if (!server.Tunnel.TlsEnabled && clients.Any(client => client.Channels.Any(channel => channel.AuthorizedClientsOnly) || client.OutboundMappings.Count > 0))
            throw new ConfigurationException("Agent-to-Agent authorization requires tunnel TLS to protect access-secret distribution.");
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in clients)
        {
            if (!secrets.Add(Convert.ToHexString(DecodeSecret(client.Secret, client.ClientId))))
            {
                throw new ConfigurationException("Client secrets must be unique.");
            }

            foreach (var channel in client.Channels.Where(channel => channel.Enabled && !channel.AuthorizedClientsOnly))
            {
                var endpoint = $"{channel.ListenAddress}:{channel.ListenPort}";
                if (!endpoints.Add(endpoint))
                {
                    throw new ConfigurationException($"Duplicate enabled channel endpoint: {endpoint}");
                }

                if ((channel.ListenPort == server.Tunnel.Port && EndpointsOverlap(channel.ListenAddress, server.Tunnel.ListenAddress)) ||
                    (channel.ListenPort == server.Tunnel.EffectiveDataPort && EndpointsOverlap(channel.ListenAddress, server.Tunnel.ListenAddress)) ||
                    (channel.ListenPort == server.Dashboard.Port && EndpointsOverlap(channel.ListenAddress, server.Dashboard.ListenAddress)))
                {
                    throw new ConfigurationException($"Channel endpoint conflicts with a server endpoint: {endpoint}");
                }
            }
        }

        var byId = clients.ToDictionary(client => client.ClientId, StringComparer.Ordinal);
        foreach (var client in clients)
        foreach (var mapping in client.OutboundMappings.Where(mapping => mapping.Enabled))
        {
            if (mapping.TargetClientId == client.ClientId || !byId.TryGetValue(mapping.TargetClientId, out var targetClient) || !targetClient.Enabled)
                throw new ConfigurationException($"Unavailable outbound target for {client.ClientId}/{mapping.MappingId}.");
            var target = targetClient.Channels.SingleOrDefault(channel => channel.ChannelId == mapping.TargetChannelId);
            if (target is not { Enabled: true, AuthorizedClientsOnly: true } || !FixedSecretEquals(mapping.AccessSecret, target.AccessSecret!) || !string.Equals(mapping.TargetCertificateSha256, target.E2eCertificateSha256, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException($"Outbound mapping is not authorized for {client.ClientId}/{mapping.MappingId}.");
        }
    }

    public void ValidateChannelUpdate(LoadedConfiguration current, ClientConfiguration updatedClient)
    {
        ValidateClient(updatedClient, "dashboard update", current.Server);
        ValidateGlobalConflicts(current.Server, current.Clients.Values.Select(client => client.ClientId == updatedClient.ClientId ? updatedClient : client));
    }

    public void ValidateClientUpdate(LoadedConfiguration configuration)
    {
        foreach (var client in configuration.Clients.Values) ValidateClient(client, "dashboard update", configuration.Server);
        ValidateGlobalConflicts(configuration.Server, configuration.Clients.Values);
    }

    public static byte[] DecodeSecret(string secret, string clientId)
    {
        try { return Convert.FromBase64String(secret); }
        catch (FormatException) { throw new ConfigurationException($"Secret is not Base64 for {clientId}."); }
    }

    private static void ValidateAccessSecret(string? secret, string label)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ConfigurationException($"Access secret is required for {label}.");
        var bytes = DecodeSecret(secret, label);
        if (bytes.Length < 32) throw new ConfigurationException($"Access secret must contain at least 32 random bytes for {label}.");
    }

    private static void ValidateFingerprint(string? fingerprint, string label)
    {
        if (fingerprint is not { Length: 64 } || !fingerprint.All(Uri.IsHexDigit))
            throw new ConfigurationException($"E2E certificate SHA-256 fingerprint is required for {label}.");
    }

    private static bool FixedSecretEquals(string first, string second)
    {
        var left = Convert.FromBase64String(first);
        var right = Convert.FromBase64String(second);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 64 } && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9_-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static bool EndpointsOverlap(string first, string second) => first == second || first is "0.0.0.0" or "::" || second is "0.0.0.0" or "::";
    private static void ValidateAddressAndPort(string? address, int port, string label)
    {
        if (!IPAddress.TryParse(address, out _) || port is < 1 or > 65535) throw new ConfigurationException($"Invalid {label}.");
    }
    private static void ValidatePort(int port, string label)
    {
        if (port is < 1 or > 65535) throw new ConfigurationException($"Invalid {label} port.");
    }
}
