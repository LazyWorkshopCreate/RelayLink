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

    public async Task<(ChannelConfiguration Channel, AgentPushReport Push)> UpdateAsync(string clientId, string channelId, ChannelUpdateRequest request, CancellationToken cancellationToken)
    {
        var (channel, push) = await ChangeAsync(clientId, (client, _) =>
        {
            var existing = client.Channels.SingleOrDefault(channel => channel.ChannelId == channelId) ?? throw new ChannelUpdateException("Channel was not found.");
            var updated = ToChannel(channelId, request) with
            {
                ApplicationProtocol = existing.ApplicationProtocol,
                Http = existing.Http,
                TransportProtocol = request.TransportProtocol ?? existing.TransportProtocol,
                UdpIdleTimeoutSeconds = request.UdpIdleTimeoutSeconds ?? existing.UdpIdleTimeoutSeconds,
                AuthorizedClientsOnly = request.AuthorizedClientsOnly ?? existing.AuthorizedClientsOnly,
                EndToEndEncryptionEnabled = request.EndToEndEncryptionEnabled ?? existing.EndToEndEncryptionEnabled,
                AccessSecret = request.AccessSecret ?? existing.AccessSecret,
                SecurityGroupId = request.SecurityGroupId is null ? existing.SecurityGroupId : string.IsNullOrWhiteSpace(request.SecurityGroupId) ? null : request.SecurityGroupId,
                Tags = NormalizeTags(request.Tags)
            };
            if (updated.AuthorizedClientsOnly) updated = updated with { SecurityGroupId = null };
            else updated = updated with { EndToEndEncryptionEnabled = true };
            if (updated.AuthorizedClientsOnly && string.IsNullOrWhiteSpace(updated.AccessSecret)) updated = updated with { AccessSecret = NewAccessSecret() };
            return (client with { Channels = client.Channels.Select(channel => channel.ChannelId == existing.ChannelId ? updated : channel).ToArray() }, updated);
        }, cancellationToken);
        return (channel ?? throw new ChannelUpdateException("Channel update did not produce a channel."), push);
    }

    public async Task<(ChannelConfiguration Channel, AgentPushReport Push)> CreateAsync(string clientId, ChannelCreateRequest request, CancellationToken cancellationToken)
    {
        var (channel, push) = await ChangeAsync(clientId, (client, _) =>
        {
            if (client.Channels.Any(channel => channel.ChannelId == request.ChannelId)) throw new ChannelUpdateException("Channel ID already exists.");
            var created = new ChannelConfiguration(request.ChannelId, request.DisplayName, request.Enabled, request.ListenAddress, request.ListenPort, request.TargetHost, request.TargetPort, request.MaxConnections, request.TargetConnectTimeoutSeconds)
            {
                TransportProtocol = request.TransportProtocol ?? "tcp",
                UdpIdleTimeoutSeconds = request.UdpIdleTimeoutSeconds ?? 60,
                AuthorizedClientsOnly = request.AuthorizedClientsOnly ?? false,
                EndToEndEncryptionEnabled = request.AuthorizedClientsOnly == true ? request.EndToEndEncryptionEnabled ?? true : true,
                AccessSecret = (request.AuthorizedClientsOnly ?? false) && string.IsNullOrWhiteSpace(request.AccessSecret) ? NewAccessSecret() : request.AccessSecret,
                SecurityGroupId = request.AuthorizedClientsOnly == true || string.IsNullOrWhiteSpace(request.SecurityGroupId) ? null : request.SecurityGroupId,
                Tags = NormalizeTags(request.Tags)
            };
            return (client with { Channels = client.Channels.Append(created).ToArray() }, created);
        }, cancellationToken);
        return (channel ?? throw new ChannelUpdateException("Channel creation did not produce a channel."), push);
    }

    public async Task<AgentPushReport> DeleteAsync(string clientId, string channelId, CancellationToken cancellationToken)
    {
        var (_, push) = await ChangeAsync(clientId, (client, _) =>
        {
            if (!client.Channels.Any(channel => channel.ChannelId == channelId)) throw new ChannelUpdateException("Channel was not found.");
            return (client with { Channels = client.Channels.Where(channel => channel.ChannelId != channelId).ToArray() }, (ChannelConfiguration?)null);
        }, cancellationToken);
        return push;
    }

    public async Task<(OutboundMappingConfiguration Mapping, AgentPushReport Push)> CreateMappingAsync(string clientId, MappingCreateRequest request, CancellationToken cancellationToken)
    {
        var (mapping, push) = await ChangeAsync(clientId, (client, current) =>
        {
            var mappingId = $"{request.TargetClientId}-{request.TargetChannelId}";
            if (client.OutboundMappings.Any(mapping => mapping.MappingId == mappingId)) throw new ChannelUpdateException("Generated access entry ID already exists.");
            var mapping = ToMapping(mappingId, request, current);
            return (client with { OutboundMappings = client.OutboundMappings.Append(mapping).ToArray() }, mapping);
        }, cancellationToken);
        return (mapping ?? throw new ChannelUpdateException("Mapping creation failed."), push);
    }

    public async Task<AgentPushReport> DeleteMappingAsync(string clientId, string mappingId, CancellationToken cancellationToken)
    {
        var (_, push) = await ChangeAsync(clientId, (client, _) =>
        {
            if (!client.OutboundMappings.Any(mapping => mapping.MappingId == mappingId)) throw new ChannelUpdateException("Mapping was not found.");
            return (client with { OutboundMappings = client.OutboundMappings.Where(mapping => mapping.MappingId != mappingId).ToArray() }, (OutboundMappingConfiguration?)null);
        }, cancellationToken);
        return push;
    }

    private async Task<(T? Result, AgentPushReport Push)> ChangeAsync<T>(string clientId, Func<ClientConfiguration, LoadedConfiguration, (ClientConfiguration Updated, T? Result)> change, CancellationToken cancellationToken)
    {
        await writeLock.Gate.WaitAsync(cancellationToken);
        try
        {
            var current = loader.Load(configurationPath);
            if (!current.Clients.TryGetValue(clientId, out var client)) throw new ChannelUpdateException("Client was not found.");
            var (updatedClient, result) = change(client, current);
            loader.ValidateChannelUpdate(current, updatedClient);
            var serialized = JsonSerializer.Serialize(updatedClient, JsonOptions);
            var updatedConfiguration = new LoadedConfiguration(current.Server, current.Clients.ToDictionary(pair => pair.Key, pair => pair.Key == clientId ? updatedClient : pair.Value, StringComparer.Ordinal));
            var clientPath = Path.Combine(current.Server.ClientsDirectory, $"{clientId}.json");
            var previous = await File.ReadAllTextAsync(clientPath, cancellationToken);
            await SecretFilePermissions.WriteTextAsync(clientPath, serialized, overwrite: true, cancellationToken);
            try
            {
                await proxyListeners.ApplyConfigurationAsync(updatedConfiguration, cancellationToken);
            }
            catch
            {
                await SecretFilePermissions.WriteTextAsync(clientPath, previous, overwrite: true, CancellationToken.None);
                throw;
            }

            runtime.ReplaceConfiguration(updatedConfiguration);
            proxyListeners.RevokeChangedConnections(client, updatedClient);
            peerRelays.RevokeChangedConnections(client, updatedClient);
            var onlyTagsChanged = client.Channels.Count == updatedClient.Channels.Count &&
                client.Channels.Zip(updatedClient.Channels).All(pair =>
                    pair.First.HasSameRuntimeSettings(pair.Second)) &&
                client.Channels.Zip(updatedClient.Channels).Any(pair =>
                    !pair.First.Tags.SequenceEqual(pair.Second.Tags, StringComparer.Ordinal));
            var failures = new List<string>();
            var attempted = false;
            var primaryOnline = sessions.TryGet(clientId, out var session) && session is not null;
            if (!onlyTagsChanged && primaryOnline)
            {
                var (snapshot, version) = ConfigurationSnapshotFactory.Create(updatedClient, updatedConfiguration, session!.ProtocolVersion);
                attempted = true;
                var failure = await ConfigurationPush.TrySendAsync(session, snapshot, version, cancellationToken);
                if (failure is not null) failures.Add(failure);
            }
            foreach (var dependent in updatedConfiguration.Clients.Values.Where(c => c.ClientId != clientId &&
                c.OutboundMappings.Any(m => m.TargetClientId == clientId)))
            {
                if (!sessions.TryGet(dependent.ClientId, out var dependentSession) || dependentSession is null) continue;
                var before = ConfigurationSnapshotFactory.Create(dependent, current, dependentSession.ProtocolVersion);
                var after = ConfigurationSnapshotFactory.Create(dependent, updatedConfiguration, dependentSession.ProtocolVersion);
                if (before.Version == after.Version) continue;
                attempted = true;
                var failure = await ConfigurationPush.TrySendAsync(dependentSession, after.Snapshot, after.Version, cancellationToken);
                if (failure is not null) failures.Add(dependent.ClientId + ": " + failure);
            }
            return (result, AgentPushReport.From(attempted, failures, primaryOnline && onlyTagsChanged));
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
    private static string[] NormalizeTags(IReadOnlyList<string>? tags) => (tags ?? [])
        .Select(tag => tag.Trim())
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record ChannelUpdateRequest(string DisplayName, bool Enabled, string ListenAddress, int ListenPort, string TargetHost, int TargetPort, int MaxConnections, int TargetConnectTimeoutSeconds)
{
    public string? TransportProtocol { get; init; }
    public int? UdpIdleTimeoutSeconds { get; init; }
    public bool? AuthorizedClientsOnly { get; init; }
    public bool? EndToEndEncryptionEnabled { get; init; }
    public string? AccessSecret { get; init; }
    public string? SecurityGroupId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
public sealed record ChannelCreateRequest(string ChannelId, string DisplayName, bool Enabled, string ListenAddress, int ListenPort, string TargetHost, int TargetPort, int MaxConnections, int TargetConnectTimeoutSeconds)
{
    public string? TransportProtocol { get; init; }
    public int? UdpIdleTimeoutSeconds { get; init; }
    public bool? AuthorizedClientsOnly { get; init; }
    public bool? EndToEndEncryptionEnabled { get; init; }
    public string? AccessSecret { get; init; }
    public string? SecurityGroupId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
public sealed class ChannelUpdateException(string message) : Exception(message);
public sealed record MappingCreateRequest(string TargetClientId, string TargetChannelId);
