using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class SecurityGroupEditor(string serverConfigurationPath, ConfigurationLoader loader, ServerRuntime runtime, ProxyListenerService proxyListeners, ConfigurationWriteLock writeLock)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string configurationPath = Path.GetFullPath(serverConfigurationPath);

    public Task<SecurityGroupConfiguration> CreateAsync(SecurityGroupCreateRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(groups =>
        {
            if (groups.Any(group => group.Id == request.Id)) throw new SecurityGroupUpdateException("Security group ID already exists.");
            var created = new SecurityGroupConfiguration(request.Id, request.Name, request.Entries);
            return (groups.Append(created).ToArray(), created);
        }, null, cancellationToken);

    public Task<SecurityGroupConfiguration> UpdateAsync(string id, SecurityGroupUpdateRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(groups =>
        {
            if (!groups.Any(group => group.Id == id)) throw new SecurityGroupUpdateException("Security group was not found.");
            var updated = new SecurityGroupConfiguration(id, request.Name, request.Entries);
            return (groups.Select(group => group.Id == id ? updated : group).ToArray(), updated);
        }, id, cancellationToken);

    public Task DeleteAsync(string id, CancellationToken cancellationToken) => ChangeAsync(groups =>
    {
        if (!groups.Any(group => group.Id == id)) throw new SecurityGroupUpdateException("Security group was not found.");
        if (runtime.Configuration.Clients.Values.Any(client => client.Channels.Any(channel => channel.SecurityGroupId == id)))
            throw new SecurityGroupUpdateException("Security group is in use; remove it from channels first.");
        return (groups.Where(group => group.Id != id).ToArray(), (SecurityGroupConfiguration?)null);
    }, id, cancellationToken);

    private async Task<T> ChangeAsync<T>(Func<IReadOnlyList<SecurityGroupConfiguration>, (IReadOnlyList<SecurityGroupConfiguration> Groups, T Result)> change, string? changedId, CancellationToken cancellationToken)
    {
        await writeLock.Gate.WaitAsync(cancellationToken);
        try
        {
            var current = loader.Load(configurationPath);
            var (groups, result) = change(current.Server.SecurityGroups);
            var updated = current with { Server = current.Server with { SecurityGroups = groups } };
            loader.ValidateServerUpdate(updated);

            var document = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath, cancellationToken)) as JsonObject
                ?? throw new SecurityGroupUpdateException("Server configuration must be a JSON object.");
            document["securityGroups"] = JsonSerializer.SerializeToNode(groups, JsonOptions);
            var temporaryPath = $"{configurationPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, document.ToJsonString(JsonOptions), new UTF8Encoding(false), cancellationToken);
                File.Move(temporaryPath, configurationPath, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }

            runtime.ReplaceConfiguration(updated);
            if (changedId is not null) proxyListeners.RevokeSecurityGroupConnections(changedId, updated.Server);
            return result;
        }
        catch (ConfigurationException exception) { throw new SecurityGroupUpdateException(exception.Message); }
        finally { writeLock.Gate.Release(); }
    }
}

public sealed record SecurityGroupCreateRequest(string Id, string Name, IReadOnlyList<string> Entries);
public sealed record SecurityGroupUpdateRequest(string Name, IReadOnlyList<string> Entries);
public sealed class SecurityGroupUpdateException(string message) : Exception(message);
