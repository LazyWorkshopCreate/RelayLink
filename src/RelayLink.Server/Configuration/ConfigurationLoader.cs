using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RelayLink.Protocol;
using RelayLink.Server.Runtime;

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
        var auditPath = server.Audit?.FilePath ?? (server.History is not null
            ? TrafficHistoryService.DatabasePath(server.History.FilePath)
            : Path.Combine(configurationDirectory, "audit.db"));
        server = server with { Audit = new AuditConfiguration(Path.GetFullPath(auditPath, configurationDirectory), server.Audit?.RetentionDays ?? 90) };
        if (server.Automation is { } automation)
        {
            server = server with
            {
                Automation = automation with
                {
                    ResolvedCertificatePath = string.IsNullOrWhiteSpace(automation.CertificatePemPath) ? null : Path.GetFullPath(automation.CertificatePemPath, configurationDirectory),
                    ResolvedPrivateKeyPath = string.IsNullOrWhiteSpace(automation.PrivateKeyPemPath) ? null : Path.GetFullPath(automation.PrivateKeyPemPath, configurationDirectory)
                }
            };
        }
        ValidateServer(server, serverPath);

        var clientsDirectory = Path.GetFullPath(server.ClientsDirectory, configurationDirectory);
        if (!Directory.Exists(clientsDirectory))
        {
            throw new ConfigurationException($"Clients directory does not exist: {clientsDirectory}");
        }

        var clients = new Dictionary<string, ClientConfiguration>(StringComparer.Ordinal);
        string[] clientFiles;
        try
        {
            clientFiles = Directory.EnumerateFiles(clientsDirectory, "*.json", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ConfigurationException($"Unable to read {clientsDirectory}: {exception.Message}", exception);
        }

        foreach (var path in clientFiles.OrderBy(path => path, StringComparer.Ordinal))
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
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var prefix = exception is JsonException ? $"Invalid JSON in {path}" : $"Unable to read {path}";
            throw new ConfigurationException($"{prefix}: {exception.Message}", exception);
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

        if (server.SecurityGroups is null || server.SecurityGroups.Count > 100)
            throw new ConfigurationException("Security groups are invalid.");
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in server.SecurityGroups)
        {
            if (!IsIdentifier(group.Id) || !groupIds.Add(group.Id) || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 100 ||
                group.Entries is null || group.Entries.Count is < 1 or > 256 || group.Entries.Any(entry => !SecurityGroupMatcher.IsValidEntry(entry)))
                throw new ConfigurationException($"Invalid security group: {group.Id}.");
        }

        ValidateAddressAndPort(server.Tunnel.ListenAddress, server.Tunnel.Port, "tunnel endpoint");
        ValidateAddressAndPort(server.Tunnel.ListenAddress, server.Tunnel.EffectiveDataPort, "data endpoint");
        if (server.Tunnel.Port == server.Tunnel.EffectiveDataPort)
            throw new ConfigurationException("Control and data endpoints must use different ports.");
        if (server.Tunnel.AgentServerHost is { } agentServerHost && !IsValidAgentServerHost(agentServerHost))
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
        catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
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
            catch (Exception exception) when (exception is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new ConfigurationException($"Unable to load tunnel trusted CA PEM: {exception.Message}");
            }
        }

        if (server.Limits.MaxConnections < 1 || server.Limits.MaxPendingConnections < 1 || server.Limits.MaxPendingConnections > server.Limits.MaxConnections || server.Limits.MaxUnauthenticatedConnections < 1 || server.Limits.MaxChannelsPerClient < 1 || server.Limits.OpenTimeoutSeconds is < 1 or > 300 || server.Limits.BlockedWriteTimeoutSeconds < 1 || server.Limits.HalfCloseDrainTimeoutSeconds < 1 || server.Limits.TcpIdleTimeoutSeconds is < 0 or > 86_400)
        {
            throw new ConfigurationException("Server limits are invalid.");
        }

        if (server.History is { Enabled: true } history && (string.IsNullOrWhiteSpace(history.FilePath) || history.SampleIntervalSeconds is < 1 or > 3600 || history.RetentionDays is < 1 or > 3650))
        {
            throw new ConfigurationException("History configuration is invalid.");
        }
        if (server.Audit is not null && server.Audit is not { FilePath: { Length: > 0 }, RetentionDays: >= 1 and <= 3650 })
            throw new ConfigurationException("Audit configuration is invalid.");
        ValidateAutomation(server);
    }

    /// <summary>
    /// 自动化入口只允许绑定 loopback 且必须提供 TLS。证书缺失、过期或私钥不匹配不在加载期拒绝，
    /// 由运行时失败关闭，避免证书轮换期间整台服务起不来。
    /// </summary>
    private static void ValidateAutomation(ServerConfiguration server)
    {
        if (server.Automation is not { } automation) return;

        // 仅接受 127.0.0.1。0.0.0.0、::、具体网卡地址与 localhost 一律拒绝。
        if (!automation.IsLoopback)
        {
            throw new ConfigurationException(
                $"Automation listenAddress must be exactly '{AutomationConfiguration.LoopbackAddress}'; '{automation.ListenAddress}' would expose the management surface beyond loopback.");
        }

        ValidatePort(automation.Port, "automation endpoint");
        if (automation.SessionLifetimeMinutes is < 15 or > 480)
            throw new ConfigurationException("Automation sessionLifetimeMinutes must be between 15 and 480.");
        if (automation.MaxPendingPairings is < 1 or > 256)
            throw new ConfigurationException("Automation maxPendingPairings must be between 1 and 256.");
        if (automation.MaxAuthorizations is < 1 or > 1024)
            throw new ConfigurationException("Automation maxAuthorizations must be between 1 and 1024.");
        if (automation.MaxAuthorizations < automation.MaxPendingPairings)
            throw new ConfigurationException("Automation maxAuthorizations must not be smaller than maxPendingPairings.");

        // 端口不得与现有服务端端点冲突。逐项比较，不复用 ChannelPortSuggestion：
        // 那个接口按传输协议分别计算，UDP 分支还会清空服务端保留集，不能用于这里的判定。
        if (automation.Port == server.Tunnel.Port || automation.Port == server.Tunnel.EffectiveDataPort || automation.Port == server.Dashboard.Port)
            throw new ConfigurationException($"Automation port {automation.Port} conflicts with a server endpoint.");

        if (automation.Enabled)
        {
            if (string.IsNullOrWhiteSpace(automation.ResolvedCertificatePath) || string.IsNullOrWhiteSpace(automation.ResolvedPrivateKeyPath))
                throw new ConfigurationException("Automation certificatePemPath and privateKeyPemPath are required when automation is enabled.");
        }
    }

    /// <summary>
    /// 回环回指校验：任何通道若把目标指向回环地址且端口等于 automation.port，必须是授权互访形态。
    /// 只有 authorizedClientsOnly=false 会在服务端监听公网端口，因此要挡住的正是这种形态。
    /// 该校验在三个入口执行：通道保存、启动加载、automation.port 变更。
    /// </summary>
    public static void ValidateAutomationLoopbackTargets(ServerConfiguration server, IEnumerable<ClientConfiguration> clients, string context)
    {
        if (server.Automation is not { } automation) return;
        foreach (var client in clients)
        foreach (var channel in client.Channels)
        {
            if (!IsLoopbackOrUnspecifiedHost(channel.TargetHost) || channel.TargetPort != automation.Port) continue;
            if (channel.AuthorizedClientsOnly && channel.EndToEndEncryptionEnabled && channel.ApplicationProtocol == "raw")
                continue;
            throw new ConfigurationException(
                $"Channel {client.ClientId}/{channel.ChannelId} targets the automation port {automation.Port} via loopback but is not an authorized-peer channel with end-to-end encryption ({context}). Only authorizedClientsOnly=true with endToEndEncryptionEnabled=true and applicationProtocol=raw may reach the automation endpoint.");
        }
    }

    public static bool IsLoopbackOrUnspecifiedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var candidate = host.Trim();
        if (candidate.StartsWith('[') && candidate.EndsWith(']') && candidate.Length > 2)
            candidate = candidate[1..^1];
        candidate = candidate.TrimEnd('.');
        if (candidate.Length == 0) return false;
        if (string.Equals(candidate, "localhost", StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IPAddress.TryParse(candidate, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
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

        if (client.AgentServerHost is { } clientServerHost && !IsValidAgentServerHost(clientServerHost))
        {
            throw new ConfigurationException($"Client agentServerHost must be a DNS name or a concrete IP address: {client.ClientId}");
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

        if (client.E2eCertificateSha256 is not null)
            ValidateFingerprint(client.E2eCertificateSha256, client.ClientId);

        ValidateTags(client.Tags, $"client {client.ClientId}");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in client.Channels)
        {
            if (!IsIdentifier(channel.ChannelId) || !ids.Add(channel.ChannelId) || string.IsNullOrWhiteSpace(channel.DisplayName))
            {
                throw new ConfigurationException($"Invalid or duplicate channelId for {client.ClientId}.");
            }

            if (channel.TransportProtocol is not ("tcp" or "udp") || channel.UdpIdleTimeoutSeconds is < 1 or > 3600)
                throw new ConfigurationException($"Invalid transport or UDP idle timeout for {client.ClientId}/{channel.ChannelId}.");
            try
            {
                RelayLink.Protocol.HttpProxyPolicy.Validate(channel.ApplicationProtocol, channel.TransportProtocol, channel.AuthorizedClientsOnly, channel.EndToEndEncryptionEnabled, channel.Http);
                if (channel.Http is not null)
                    RelayLink.Protocol.HttpProxyPolicy.ResolveWebSocketConnectionCap(channel.MaxConnections, channel.Http.MaxWebSocketConnections);
            }
            catch (ArgumentException exception) { throw new ConfigurationException(exception.Message); }
            if (channel.ApplicationProtocol == "http" && !server.Tunnel.TlsEnabled) throw new ConfigurationException("HTTP requires control TLS.");
            ValidateAddressAndPort(channel.ListenAddress, channel.ListenPort, $"channel {channel.ChannelId} listener");
            ValidatePort(channel.TargetPort, $"channel {channel.ChannelId} target");
            if (string.IsNullOrWhiteSpace(channel.TargetHost) || channel.MaxConnections < 1 || channel.MaxConnections > client.MaxConnections || channel.TargetConnectTimeoutSeconds is < 1 or > 300)
            {
                throw new ConfigurationException($"Invalid channel values for {client.ClientId}/{channel.ChannelId}.");
            }
            if (channel.AuthorizedClientsOnly)
            {
                ValidateAccessSecret(channel.AccessSecret, $"{client.ClientId}/{channel.ChannelId}");
            }
            if (channel.SecurityGroupId is not null &&
                (channel.AuthorizedClientsOnly || !server.SecurityGroups.Any(group => group.Id == channel.SecurityGroupId)))
                throw new ConfigurationException($"Invalid security group for {client.ClientId}/{channel.ChannelId}.");
            ValidateTags(channel.Tags, $"channel {client.ClientId}/{channel.ChannelId}");
        }

        var mappingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in client.OutboundMappings)
        {
            if (!IsMappingIdentifier(mapping.MappingId) || !mappingIds.Add(mapping.MappingId) || mapping.LocalAddress != "127.0.0.1" || !IsIdentifier(mapping.TargetClientId) || !IsIdentifier(mapping.TargetChannelId))
                throw new ConfigurationException($"Invalid outbound mapping for {client.ClientId}.");
            ValidateAccessSecret(mapping.AccessSecret, $"{client.ClientId}/{mapping.MappingId}");
            ValidateFingerprint(mapping.TargetCertificateSha256, $"{client.ClientId}/{mapping.MappingId}");
        }
    }

    private static void ValidateTags(IReadOnlyList<string>? tags, string owner)
    {
        if (tags is null || tags.Count > 32 || tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag != tag.Trim() || tag.Any(char.IsControl)) ||
            tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tags.Count)
            throw new ConfigurationException($"Invalid tags for {owner}; use at most 32 distinct tags of 1-64 characters.");
    }

    private static void ValidateGlobalConflicts(ServerConfiguration server, IEnumerable<ClientConfiguration> clients)
    {
        if (!server.Tunnel.TlsEnabled && clients.Any(client => client.Channels.Any(channel => channel.AuthorizedClientsOnly) || client.OutboundMappings.Count > 0))
            throw new ConfigurationException("Agent-to-Agent authorization requires tunnel TLS to protect access-secret distribution.");
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        var listeners = new List<ChannelConfiguration>();
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in clients)
        {
            if (!secrets.Add(Convert.ToHexString(DecodeSecret(client.Secret, client.ClientId))))
            {
                throw new ConfigurationException("Client secrets must be unique.");
            }

            foreach (var channel in client.Channels.Where(channel => channel.Enabled && !channel.AuthorizedClientsOnly))
            {
                var endpoint = $"{channel.TransportProtocol}:{channel.ListenAddress}:{channel.ListenPort}";
                if (!endpoints.Add(endpoint) || listeners.Any(other => other.TransportProtocol == channel.TransportProtocol &&
                    other.ListenPort == channel.ListenPort && EndpointsOverlap(other.ListenAddress, channel.ListenAddress)))
                {
                    throw new ConfigurationException($"Duplicate enabled channel endpoint: {endpoint}");
                }

                listeners.Add(channel);
                if (channel.TransportProtocol == "tcp" && ((channel.ListenPort == server.Tunnel.Port && EndpointsOverlap(channel.ListenAddress, server.Tunnel.ListenAddress)) ||
                    (channel.ListenPort == server.Tunnel.EffectiveDataPort && EndpointsOverlap(channel.ListenAddress, server.Tunnel.ListenAddress)) ||
                    (channel.ListenPort == server.Dashboard.Port && EndpointsOverlap(channel.ListenAddress, server.Dashboard.ListenAddress)) ||
                    (server.Automation is { } automation && channel.ListenPort == automation.Port && EndpointsOverlap(channel.ListenAddress, automation.ListenAddress))))
                {
                    throw new ConfigurationException($"Channel endpoint conflicts with a server endpoint: {endpoint}");
                }
            }
        }

        var byId = clients.ToDictionary(client => client.ClientId, StringComparer.Ordinal);
        foreach (var client in clients)
        foreach (var mapping in client.OutboundMappings.Where(mapping => mapping.Enabled))
        {
            if (mapping.TargetClientId == client.ClientId || !byId.TryGetValue(mapping.TargetClientId, out var targetClient))
                throw new ConfigurationException($"Unavailable outbound target for {client.ClientId}/{mapping.MappingId}.");
            var target = targetClient.Channels.SingleOrDefault(channel => channel.ChannelId == mapping.TargetChannelId);
            if (target is not { Enabled: true, AuthorizedClientsOnly: true } || !FixedSecretEquals(mapping.AccessSecret, target.AccessSecret!) || !string.Equals(mapping.TargetCertificateSha256, targetClient.E2eCertificateSha256, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException($"Outbound mapping is not authorized for {client.ClientId}/{mapping.MappingId}.");
        }

        // 回环回指校验：覆盖启动加载与全部保存路径。
        ValidateAutomationLoopbackTargets(server, clients, "configuration load or save");
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

    public void ValidateServerUpdate(LoadedConfiguration configuration)
    {
        ValidateServer(configuration.Server, "dashboard update");
        ValidateClientUpdate(configuration);
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

    private static bool IsValidAgentServerHost(string host) =>
        !string.IsNullOrWhiteSpace(host) &&
        Uri.CheckHostName(host) != UriHostNameType.Unknown &&
        !string.Equals(host, "0.0.0.0", StringComparison.Ordinal) &&
        !string.Equals(host, "::", StringComparison.Ordinal);

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 64 } && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9_-]{0,63}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static bool IsMappingIdentifier(string? value) => value is { Length: > 0 and <= 129 } && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9_-]{0,128}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static bool EndpointsOverlap(string first, string second)
    {
        if (!IPAddress.TryParse(first, out var left) || !IPAddress.TryParse(second, out var right))
            return string.Equals(first, second, StringComparison.Ordinal);
        return left.AddressFamily == right.AddressFamily &&
               (left.Equals(right) || left.Equals(IPAddress.Any) || right.Equals(IPAddress.Any) ||
                left.Equals(IPAddress.IPv6Any) || right.Equals(IPAddress.IPv6Any));
    }
    private static void ValidateAddressAndPort(string? address, int port, string label)
    {
        if (!IPAddress.TryParse(address, out _) || port is < 1 or > 65535) throw new ConfigurationException($"Invalid {label}.");
    }
    private static void ValidatePort(int port, string label)
    {
        if (port is < 1 or > 65535) throw new ConfigurationException($"Invalid {label} port.");
    }
}
