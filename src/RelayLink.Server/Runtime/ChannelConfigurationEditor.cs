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
    SessionRegistry sessions)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly SemaphoreSlim updateLock = new(1, 1);
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
                E2eCertificateSha256 = request.E2eCertificateSha256 ?? existing.E2eCertificateSha256
            };
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
                E2eCertificateSha256 = request.E2eCertificateSha256
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
            if (client.OutboundMappings.Any(mapping => mapping.MappingId == request.MappingId)) throw new ChannelUpdateException("Mapping ID already exists.");
            var mapping = ToMapping(request, runtime.Configuration);
            return (client with { OutboundMappings = client.OutboundMappings.Append(mapping).ToArray() }, mapping);
        }, cancellationToken) ?? throw new ChannelUpdateException("Mapping creation failed.");

    public async Task<OutboundMappingConfiguration> UpdateMappingAsync(string clientId, string mappingId, MappingUpdateRequest request, CancellationToken cancellationToken) =>
        await ChangeAsync(clientId, client =>
        {
            if (!client.OutboundMappings.Any(mapping => mapping.MappingId == mappingId)) throw new ChannelUpdateException("Mapping was not found.");
            var mapping = ToMapping(new MappingCreateRequest(mappingId, request.Enabled, request.TargetClientId, request.TargetChannelId), runtime.Configuration);
            return (client with { OutboundMappings = client.OutboundMappings.Select(existing => existing.MappingId == mappingId ? mapping : existing).ToArray() }, mapping);
        }, cancellationToken) ?? throw new ChannelUpdateException("Mapping update failed.");

    public Task DeleteMappingAsync(string clientId, string mappingId, CancellationToken cancellationToken) =>
        ChangeAsync(clientId, client =>
        {
            if (!client.OutboundMappings.Any(mapping => mapping.MappingId == mappingId)) throw new ChannelUpdateException("Mapping was not found.");
            return (client with { OutboundMappings = client.OutboundMappings.Where(mapping => mapping.MappingId != mappingId).ToArray() }, (OutboundMappingConfiguration?)null);
        }, cancellationToken);

    private async Task<T?> ChangeAsync<T>(string clientId, Func<ClientConfiguration, (ClientConfiguration Updated, T? Result)> change, CancellationToken cancellationToken)
    {
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            var current = loader.Load(configurationPath);
            if (!current.Clients.TryGetValue(clientId, out var client)) throw new ChannelUpdateException("Client was not found.");
            var (updatedClient, result) = change(client);
            loader.ValidateChannelUpdate(current, updatedClient);

            var updatedConfiguration = new LoadedConfiguration(current.Server, current.Clients.ToDictionary(pair => pair.Key, pair => pair.Key == clientId ? updatedClient : pair.Value, StringComparer.Ordinal));
            await proxyListeners.ApplyConfigurationAsync(updatedConfiguration, cancellationToken);

            var clientPath = Path.Combine(current.Server.ClientsDirectory, $"{clientId}.json");
            var temporaryPath = $"{clientPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(updatedClient, JsonOptions), new UTF8Encoding(false), cancellationToken);
                File.Move(temporaryPath, clientPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            runtime.ReplaceConfiguration(updatedConfiguration);
            if (sessions.TryGet(clientId, out var session) && session is not null)
            {
                var (snapshot, version) = ConfigurationSnapshotFactory.Create(updatedClient);
                await session.SendConfigurationUpdateAsync(snapshot, version, cancellationToken);
            }
            return result;
        }
        catch (ConfigurationException exception) { throw new ChannelUpdateException(exception.Message); }
        finally { updateLock.Release(); }
    }

    private static ChannelConfiguration ToChannel(string channelId, ChannelUpdateRequest request) =>
        new(channelId, request.DisplayName, request.Enabled, request.ListenAddress, request.ListenPort, request.TargetHost, request.TargetPort, request.MaxConnections, request.TargetConnectTimeoutSeconds);

    private static OutboundMappingConfiguration ToMapping(MappingCreateRequest request, LoadedConfiguration configuration)
    {
        if (!configuration.Clients.TryGetValue(request.TargetClientId, out var targetClient) || !targetClient.Enabled ||
            targetClient.Channels.SingleOrDefault(channel => channel.ChannelId == request.TargetChannelId) is not { Enabled: true, AuthorizedClientsOnly: true } target ||
            string.IsNullOrWhiteSpace(target.AccessSecret) || string.IsNullOrWhiteSpace(target.E2eCertificateSha256))
            throw new ChannelUpdateException("Target authorized channel was not found.");
        return new OutboundMappingConfiguration(request.MappingId, request.Enabled, "127.0.0.1", request.TargetClientId, request.TargetChannelId, target.AccessSecret, target.E2eCertificateSha256);
    }

    private static string NewAccessSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed record ChannelUpdateRequest(string DisplayName, bool Enabled, string ListenAddress, int ListenPort, string TargetHost, int TargetPort, int MaxConnections, int TargetConnectTimeoutSeconds)
{
    public bool? AuthorizedClientsOnly { get; init; }
    public string? AccessSecret { get; init; }
    public string? E2eCertificateSha256 { get; init; }
}
public sealed record ChannelCreateRequest(string ChannelId, string DisplayName, bool Enabled, string ListenAddress, int ListenPort, string TargetHost, int TargetPort, int MaxConnections, int TargetConnectTimeoutSeconds)
{
    public bool? AuthorizedClientsOnly { get; init; }
    public string? AccessSecret { get; init; }
    public string? E2eCertificateSha256 { get; init; }
}
public sealed class ChannelUpdateException(string message) : Exception(message);
public sealed record MappingCreateRequest(string MappingId, bool Enabled, string TargetClientId, string TargetChannelId);
public sealed record MappingUpdateRequest(bool Enabled, string TargetClientId, string TargetChannelId);
