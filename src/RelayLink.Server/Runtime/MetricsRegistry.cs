using System.Collections.Concurrent;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class MetricsRegistry
{
    private readonly ConcurrentDictionary<string, ChannelMetrics> channels = new(StringComparer.Ordinal);

    public MetricsRegistry(LoadedConfiguration configuration)
    {
        foreach (var client in configuration.Clients.Values)
        foreach (var channel in client.Channels)
        {
            channels.TryAdd(Key(client.ClientId, channel.ChannelId), new ChannelMetrics());
        }
    }

    public ChannelMetrics For(string clientId, string channelId) => channels.GetOrAdd(Key(clientId, channelId), _ => new ChannelMetrics());
    public void ResetTargets(string clientId)
    {
        foreach (var pair in channels.Where(pair => pair.Key.StartsWith($"{clientId}\0", StringComparison.Ordinal))) pair.Value.ResetTarget();
    }
    private static string Key(string clientId, string channelId) => $"{clientId}\0{channelId}";
}

public sealed class ChannelMetrics
{
    private long accepted;
    private long opened;
    private long openFailed;
    private long normalClosed;
    private long aborted;
    private long bytesToTarget;
    private long bytesToCaller;
    private int pending;
    private int active;
    private long peerAccepted;
    private long peerOpened;
    private long peerFailed;
    private long peerCiphertextToTarget;
    private long peerCiphertextToCaller;
    private int peerPending;
    private int peerActive;
    private TargetConnectionResult? targetResult;

    public void Accepted() { Interlocked.Increment(ref accepted); Interlocked.Increment(ref pending); }
    public void Opened() { DecrementPending(); Interlocked.Increment(ref opened); Interlocked.Increment(ref active); }
    public void OpenFailed() { DecrementPending(); Interlocked.Increment(ref openFailed); }
    public void Closed(bool normal)
    {
        Interlocked.Decrement(ref active);
        if (normal) Interlocked.Increment(ref normalClosed);
        else Interlocked.Increment(ref aborted);
    }
    public void AddToTarget(int bytes) => Interlocked.Add(ref bytesToTarget, bytes);
    public void AddToCaller(int bytes) => Interlocked.Add(ref bytesToCaller, bytes);
    public void PeerAccepted() { Interlocked.Increment(ref peerAccepted); Interlocked.Increment(ref peerPending); }
    public void PeerOpened() { Interlocked.Decrement(ref peerPending); Interlocked.Increment(ref peerOpened); Interlocked.Increment(ref peerActive); }
    public void PeerFailed() { Interlocked.Decrement(ref peerPending); Interlocked.Increment(ref peerFailed); }
    public void PeerClosed() => Interlocked.Decrement(ref peerActive);
    public void AddPeerCiphertextToTarget(int bytes) => Interlocked.Add(ref peerCiphertextToTarget, bytes);
    public void AddPeerCiphertextToCaller(int bytes) => Interlocked.Add(ref peerCiphertextToCaller, bytes);
    public void MarkTargetSuccess() => Volatile.Write(ref targetResult, new TargetConnectionResult("success", DateTimeOffset.UtcNow));
    public void MarkTargetFailure(string errorCode) => Volatile.Write(ref targetResult, new TargetConnectionResult(errorCode, DateTimeOffset.UtcNow));
    public void ResetTarget() => Volatile.Write(ref targetResult, null);

    private void DecrementPending()
    {
        while (true)
        {
            var current = Volatile.Read(ref pending);
            if (current <= 0 || Interlocked.CompareExchange(ref pending, current - 1, current) == current) return;
        }
    }

    public ChannelMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref accepted), Interlocked.Read(ref opened), Interlocked.Read(ref openFailed), Interlocked.Read(ref normalClosed), Interlocked.Read(ref aborted),
        Volatile.Read(ref pending), Volatile.Read(ref active), Interlocked.Read(ref bytesToTarget), Interlocked.Read(ref bytesToCaller), Volatile.Read(ref targetResult))
    {
        PeerAcceptedTotal = Interlocked.Read(ref peerAccepted),
        PeerOpenedTotal = Interlocked.Read(ref peerOpened),
        PeerOpenFailedTotal = Interlocked.Read(ref peerFailed),
        PeerPendingConnections = Volatile.Read(ref peerPending),
        PeerActiveConnections = Volatile.Read(ref peerActive),
        PeerCiphertextToTarget = Interlocked.Read(ref peerCiphertextToTarget),
        PeerCiphertextToCaller = Interlocked.Read(ref peerCiphertextToCaller)
    };
}

public sealed record ChannelMetricsSnapshot(long AcceptedTotal, long OpenedTotal, long OpenFailedTotal, long NormalClosedTotal, long AbortedTotal, int PendingConnections, int ActiveConnections, long BytesToTarget, long BytesToCaller, TargetConnectionResult? TargetLastResult)
{
    public long PeerAcceptedTotal { get; init; }
    public long PeerOpenedTotal { get; init; }
    public long PeerOpenFailedTotal { get; init; }
    public int PeerPendingConnections { get; init; }
    public int PeerActiveConnections { get; init; }
    public long PeerCiphertextToTarget { get; init; }
    public long PeerCiphertextToCaller { get; init; }
}
public sealed record TargetConnectionResult(string Result, DateTimeOffset TimeUtc);
