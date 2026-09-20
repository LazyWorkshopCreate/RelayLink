using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class ChannelConfigurationEditor(
    string serverConfigurationPath,
    ConfigurationLoader loader,
    ServerRuntime runtime,
    ProxyListenerService proxyListeners,
    PeerRelayRegistry peerRelays,
    SessionRegistry sessions,
    ConfigurationWriteLock writeLock)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string configurationPath = Path.GetFullPath(serverConfigurationPath);

    public async Task<ChannelConfiguration> UpdateAsync(string clientId, string channelId, ChannelUpdateRequest request, CancellationToken cancellationToken)
    {
        return await ChangeAsync(clientId, client =>
        {
            var existing = client.Channels.SingleOrDefault(channel => channel.ChannelId == channelId) ?? throw new ChannelUpdateException("Channel was not found.");
            var updated = ToChannel(channelId, request) with
            {
                AuthorizedClientsOnly = request.AuthorizedClientsOnly ?? existing.AuthorizedClientsOnly,
                AccessSecret = request.AccessSecret ?? existing.AccessSecret,
                SecurityGroupId = request.SecurityGroupId is null ? existing.SecurityGroupId : string.IsNullOrWhiteSpace(request.SecurityGroupId) ? null : request.SecurityGroupId
            };
            if (updated.AuthorizedClientsOnly) updated = updated with { SecurityGroupId = null };
            if (updated.AuthorizedClientsOnly && string.IsNullOrWhiteSpace(updated.AccessSecret)) updated = updated with { AccessSecret = NewAccessSecret() };
            return (client with { Channels = client.Channels.Select(channel => channel.ChannelId == existing.ChannelId ? updated : channel).ToArray() }, updated);
        }, cancellationToken) ?? throw new ChannelUpdateException("Channel update did not produce a channel.");
    }

    public async Task<ChannelConfiguration> CreateAsync(string clientId, ChannelCreateRequest request, CancellationToken cancellationToken)
    {
        return await ChangeAsync(clientId, client =>
        {
            if (client.Channels.Any(channel => channel.ChannelId == request.ChannelId)) throw new ChannelUpdateException("Channel ID already exists.");
            var created = new ChannelConfiguration(request.ChannelId, request.DisplayName, request.Enabled, request.ListenAddress, request.ListenPort, request.TargetHost, request.TargetPort, request.MaxConnections, request.TargetConnectTimeoutSeconds)
            {
                AuthorizedClientsOnly = request.AuthorizedClientsOnly ?? false,
                AccessSecret = (request.AuthorizedClientsOnly ?? false) && string.IsNullOrWhiteSpace(request.AccessSecret) ? NewAccessSecret() : request.AccessSecret,
                SecurityGroupId = request.AuthorizedClientsOnly == true || string.IsNullOrWhiteSpace(request.SecurityGroupId) ? null : request.SecurityGroupId
            };
            return (client with { Channels = client.Channels.Append(created).ToArray() }, created);
        }, cancellationToken) ?? throw new ChannelUpdateException("Channel creation did not produce a channel.");
    }

    public Task DeleteAsync(string clientId, string channelId, CancellationToken cancellationToken) =>
        ChangeAsync(clientId, client =>
        {
            if (!client.Channels.Any(channel => channel.ChannelId == channelId)) throw new ChannelUpdateException("Channel was not found.");
            return (client with { Channels = client.Channels.Where(channel => channel.ChannelId != channelId).ToArray() }, (ChannelConfiguration?)null);
        }, cancellationToken);

    public async Task<OutboundMappingConfiguration> CreateMappingAsync(string clientId, MappingCreateRequest request, CancellationToken cancellationToken) =>
        await ChangeAsync(clientId, client =>
        {
            var mappingId = $"{request.TargetClientId}-{request.TargetChannelId}";
            if (client.OutboundMappings.Any(mapping => mapping.MappingId == mappingId)) throw new ChannelUpdateException("Generated access entry ID already exists.");
            var mapping = ToMapping(mappingId, request, runtime.Configuration);
            return (client with { OutboundMappings = client.OutboundMappings.Append(mapping).ToArray() }, mapping);
        }, cancellationToken) ?? throw new ChannelUpdateException("Mapping creation failed.");

    public Task DeleteMappingAsync(string clientId, string mappingId, CancellationToken cancellationToken) =>
        ChangeAsync(clientId, client =>
        {
            if (!client.OutboundMappings.Any(mapping => mapping.MappingId == mappingId)) throw new ChannelUpdateException("Mapping was not found.");
            return (client with { OutboundMappings = client.OutboundMappings.Where(mapping => mapping.MappingId != mappingId).ToArray() }, (OutboundMappingConfiguration?)null);
        }, cancellationToken);

    private async Task<T?> ChangeAsync<T>(string clientId, Func<ClientConfiguration, (ClientConfiguration Updated, T? Result)> change, CancellationToken cancellationToken)
    {
        await writeLock.Gate.WaitAsync(cancellationToken);
        try
        {
            var current = loader.Load(configurationPath);
            if (!current.Clients.TryGetValue(clientId, out var client)) throw new ChannelUpdateException("Client was not found.");
            var (updatedClient, result) = change(client);
            loader.ValidateChannelUpdate(current, updatedClient);
            var serialized = JsonSerializer.Serialize(updatedClient, JsonOptions);

            var updatedConfiguration = new LoadedConfiguration(current.Server, current.Clients.ToDictionary(pair => pair.Key, pair => pair.Key == clientId ? updatedClient : pair.Value, StringComparer.Ordinal));
            await proxyListeners.ApplyConfigurationAsync(updatedConfiguration, cancellationToken);

            var clientPath = Path.Combine(current.Server.ClientsDirectory, $"{clientId}.json");
            var temporaryPath = $"{clientPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, serialized, new UTF8Encoding(false), cancellationToken);
                File.Move(temporaryPath, clientPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            runtime.ReplaceConfiguration(updatedConfiguration);
            proxyListeners.RevokeChangedConnections(client, updatedClient);
            peerRelays.RevokeChangedConnections(client, updatedClient);
            if (sessions.TryGet(clientId, out var session) && session is not null)
            {
                var (snapshot, version) = ConfigurationSnapshotFactory.Create(updatedClient);
                await session.SendConfigurationUpdateAsync(snapshot, version, cancellationToken);
            }
            return result;
        }
        catch (ConfigurationException exception) { throw new ChannelUpdateException(exception.Message); }
        finally { writeLock.Gate.Release(); }
    }

    private static ChannelConfiguration ToChannel(string channelId, ChannelUpdateRequest request) =>
        new(channelId, request.DisplayName, request.Enabled, request.ListenAddress, request.ListenPort, request.TargetHost, request.TargetPort, request.MaxConnections, request.TargetConnectTimeoutSeconds);

    private static OutboundMappingConfiguration ToMapping(string mappingId, MappingCreateRequest request, LoadedConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(request.TargetClientId) || string.IsNullOrWhiteSpace(request.TargetChannelId))
            throw new ChannelUpdateException("Target client ID and channel ID are required.");
        if (!configuration.Clients.TryGetValue(request.TargetClientId, out var targetClient) || !targetClient.Enabled ||
            targetClient.Channels.SingleOrDefault(channel => channel.ChannelId == request.TargetChannelId) is not { Enabled: true, AuthorizedClientsOnly: true } target ||
            string.IsNullOrWhiteSpace(target.AccessSecret))
            throw new ChannelUpdateException("Target authorized channel was not found.");
        if (string.IsNullOrWhiteSpace(targetClient.E2eCertificateSha256))
            throw new ChannelUpdateException("Target client has not registered an E2E certificate yet.");
        return new OutboundMappingConfiguration(mappingId, true, "127.0.0.1", request.TargetClientId, request.TargetChannelId, target.AccessSecret, targetClient.E2eCertificateSha256);
    }

    private static string NewAccessSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed record ChannelUpdateRequest(string DisplayName, bool Enabled, string ListenAddress, int ListenPort, string TargetHost, int TargetPort, int MaxConnections, int TargetConnectTimeoutSeconds)
{
    public bool? AuthorizedClientsOnly { get; init; }
    public string? AccessSecret { get; init; }
    public string? SecurityGroupId { get; init; }
}
public sealed record ChannelCreateRequest(string ChannelId, string DisplayName, bool Enabled, string ListenAddress, int ListenPort, string TargetHost, int TargetPort, int MaxConnections, int TargetConnectTimeoutSeconds)
{
    public bool? AuthorizedClientsOnly { get; init; }
    public string? AccessSecret { get; init; }
    public string? SecurityGroupId { get; init; }
}
public sealed class ChannelUpdateException(string message) : Exception(message);
public sealed record MappingCreateRequest(string TargetClientId, string TargetChannelId);
