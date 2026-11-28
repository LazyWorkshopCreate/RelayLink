using System.Collections.Concurrent;
using RelayLink.Server.Configuration;
using RelayLink.Transport;

namespace RelayLink.Server.Runtime;

public sealed class ServerRuntime
{
    private LoadedConfiguration configuration;

    public ServerRuntime(LoadedConfiguration configuration, SessionRegistry sessions)
    {
        this.configuration = configuration;
        Sessions = sessions;
        GlobalConnectionLimit = new(configuration.Server.Limits.MaxConnections);
        GlobalPendingLimit = new(configuration.Server.Limits.MaxPendingConnections);
        SyncClientLimits(configuration.Clients.Values);
    }

    public LoadedConfiguration Configuration => Volatile.Read(ref configuration);
    public SessionRegistry Sessions { get; }
    public SemaphoreSlim GlobalConnectionLimit { get; }
    public SemaphoreSlim GlobalPendingLimit { get; }
    public ConcurrentDictionary<string, AdjustableConnectionLimit> ClientConnectionLimits { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, AdjustableConnectionLimit> ClientPendingLimits { get; } = new(StringComparer.Ordinal);
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public Guid InstanceId { get; } = Guid.NewGuid();
    private int ready;
    public bool IsReady
    {
        get => Volatile.Read(ref ready) == 1;
        set => Volatile.Write(ref ready, value ? 1 : 0);
    }

    public void ReplaceConfiguration(LoadedConfiguration updated) => Volatile.Write(ref configuration, updated);

    internal (Dictionary<string, int> Connections, Dictionary<string, int> Pending) SyncClientLimits(IEnumerable<ClientConfiguration> clients)
    {
        var previousConnections = ClientConnectionLimits.ToDictionary(pair => pair.Key, pair => pair.Value.Maximum, StringComparer.Ordinal);
        var previousPending = ClientPendingLimits.ToDictionary(pair => pair.Key, pair => pair.Value.Maximum, StringComparer.Ordinal);
        foreach (var client in clients)
        {
            ClientConnectionLimits.GetOrAdd(client.ClientId, _ => new AdjustableConnectionLimit(client.MaxConnections)).SetMaximum(client.MaxConnections);
            ClientPendingLimits.GetOrAdd(client.ClientId, _ => new AdjustableConnectionLimit(client.MaxPendingConnections)).SetMaximum(client.MaxPendingConnections);
        }
        return (previousConnections, previousPending);
    }

    internal void RestoreClientLimits(Dictionary<string, int> previousConnections, Dictionary<string, int> previousPending)
    {
        foreach (var pair in previousConnections) ClientConnectionLimits[pair.Key].SetMaximum(pair.Value);
        foreach (var pair in previousPending) ClientPendingLimits[pair.Key].SetMaximum(pair.Value);
    }
}
