using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class ServerRuntime(LoadedConfiguration configuration, SessionRegistry sessions)
{
    private LoadedConfiguration configuration = configuration;
    public LoadedConfiguration Configuration => Volatile.Read(ref configuration);
    public SessionRegistry Sessions { get; } = sessions;
    public SemaphoreSlim GlobalConnectionLimit { get; } = new(configuration.Server.Limits.MaxConnections);
    public SemaphoreSlim GlobalPendingLimit { get; } = new(configuration.Server.Limits.MaxPendingConnections);
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public Guid InstanceId { get; } = Guid.NewGuid();
    private int ready;
    public bool IsReady
    {
        get => Volatile.Read(ref ready) == 1;
        set => Volatile.Write(ref ready, value ? 1 : 0);
    }
    public void ReplaceConfiguration(LoadedConfiguration updated) => Volatile.Write(ref configuration, updated);
}
