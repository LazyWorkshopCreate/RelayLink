using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class ClientConfigurationEditor(string serverConfigurationPath, ConfigurationLoader loader, ServerRuntime runtime, ProxyListenerService proxyListeners, SessionRegistry sessions)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string configurationPath = Path.GetFullPath(serverConfigurationPath);
    private readonly SemaphoreSlim updateLock = new(1, 1);

    public Task<ClientConfiguration> CreateAsync(ClientCreateRequest request, CancellationToken cancellationToken) => ChangeAsync(current =>
    {
        if (current.Clients.ContainsKey(request.ClientId)) throw new ClientUpdateException("Client ID already exists.");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var client = new ClientConfiguration(1, request.ClientId, request.DisplayName, request.Enabled, secret, request.MaxConnections, request.MaxPendingConnections, []) { AgentServerHost = request.AgentServerHost };
        return (new LoadedConfiguration(current.Server, current.Clients.Append(new KeyValuePair<string, ClientConfiguration>(client.ClientId, client)).ToDictionary()), client, true);
    }, cancellationToken);

    public Task<ClientConfiguration> UpdateAsync(string clientId, ClientUpdateRequest request, CancellationToken cancellationToken) => ChangeAsync(current =>
    {
        if (!current.Clients.TryGetValue(clientId, out var existing)) throw new ClientUpdateException("Client was not found.");
        var updated = existing with { DisplayName = request.DisplayName, Enabled = request.Enabled, MaxConnections = request.MaxConnections, MaxPendingConnections = request.MaxPendingConnections };
        return (new LoadedConfiguration(current.Server, current.Clients.ToDictionary(pair => pair.Key, pair => pair.Key == clientId ? updated : pair.Value, StringComparer.Ordinal)), updated, false);
    }, cancellationToken);

    private async Task<ClientConfiguration> ChangeAsync(Func<LoadedConfiguration, (LoadedConfiguration Configuration, ClientConfiguration Client, bool IsNew)> change, CancellationToken cancellationToken)
    {
        await updateLock.WaitAsync(cancellationToken);
        try
        {
            var current = loader.Load(configurationPath);
            var (updated, client, isNew) = change(current);
            loader.ValidateClientUpdate(updated);
            await proxyListeners.ApplyConfigurationAsync(updated, cancellationToken);
            var path = Path.Combine(updated.Server.ClientsDirectory, $"{client.ClientId}.json");
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(client, JsonOptions), new UTF8Encoding(false), cancellationToken); File.Move(temp, path, overwrite: false); }
            catch (IOException) when (!isNew) { File.Move(temp, path, overwrite: true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            runtime.ReplaceConfiguration(updated);
            if (!client.Enabled && sessions.TryGet(client.ClientId, out var session) && session is not null) sessions.Remove(client.ClientId, session.SessionId);
            return client;
        }
        catch (ConfigurationException exception) { throw new ClientUpdateException(exception.Message); }
        finally { updateLock.Release(); }
    }
}

public sealed record ClientCreateRequest(string ClientId, string DisplayName, bool Enabled, int MaxConnections, int MaxPendingConnections, string AgentServerHost);
public sealed record ClientUpdateRequest(string DisplayName, bool Enabled, int MaxConnections, int MaxPendingConnections);
public sealed class ClientUpdateException(string message) : Exception(message);
