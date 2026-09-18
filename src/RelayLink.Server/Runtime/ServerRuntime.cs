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
    public bool IsReady { get; set; }
    public void ReplaceConfiguration(LoadedConfiguration updated) => Volatile.Write(ref configuration, updated);
}
