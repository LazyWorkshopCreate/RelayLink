namespace RelayLink.Server.Runtime;

// Serializes client JSON and in-memory configuration mutations.
public sealed class ConfigurationWriteLock
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}
