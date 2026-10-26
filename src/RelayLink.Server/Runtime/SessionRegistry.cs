using System.Collections.Concurrent;
using System.Diagnostics;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Transport;

namespace RelayLink.Server.Runtime;

public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);

    public bool TryRegister(ClientConfiguration client, out Session session)
    {
        session = new Session(Guid.NewGuid(), client.ClientId, DateTimeOffset.UtcNow);
        return sessions.TryAdd(client.ClientId, session);
    }

    public bool Remove(string clientId, Guid sessionId)
    {
        if (!sessions.TryGetValue(clientId, out var current) || current.SessionId != sessionId ||
            !sessions.TryRemove(new KeyValuePair<string, Session>(clientId, current))) return false;
        current.Close();
        return true;
    }
    public bool TryGet(string clientId, out Session? session) => sessions.TryGetValue(clientId, out session);
    public int Count => sessions.Count;
}

public sealed class Session(Guid sessionId, string clientId, DateTimeOffset connectedAtUtc)
{
    private readonly CancellationTokenSource lifetime = new();
    private int ready;
    public CancellationToken LifetimeToken => lifetime.Token;
    public bool IsReady => Volatile.Read(ref ready) == 1 && !lifetime.IsCancellationRequested;
    public void MarkReady() => Volatile.Write(ref ready, 1);
    public void Close() { Volatile.Write(ref ready, 0); lifetime.Cancel(); }
    public Guid SessionId { get; } = sessionId;
    public string ClientId { get; } = clientId;
    public DateTimeOffset ConnectedAtUtc { get; } = connectedAtUtc;
    public DateTimeOffset LastHeartbeatUtc { get; set; } = connectedAtUtc;
    public TimeSpan? LastHeartbeatRtt { get; set; }
    public string? AgentVersion { get; set; }
    public byte ProtocolVersion { get; set; } = ProtocolConstants.Version;
    private FrameWriter? ControlWriter { get; set; }
    private long expectedPongSequence;
    private long pingTimestamp;
    private string configVersion = string.Empty;
    private const int MaximumPendingConfigurations = 32;
    private readonly SemaphoreSlim configUpdateLock = new(1, 1);
    private readonly object configAckLock = new();
    private readonly Queue<string> pendingConfigVersions = new();
    private IReadOnlyList<HttpChannelStatus> httpStates = [];
    public IReadOnlyList<HttpChannelStatus> HttpStates => Volatile.Read(ref httpStates);
    public void SetHttpStates(IReadOnlyList<HttpChannelStatus> states) => Volatile.Write(ref httpStates, states.ToArray());
    public bool IsHttpReady(ChannelConfiguration channel) => ProtocolVersion >= 5 && HttpStates.Any(state => state.ChannelId == channel.ChannelId && state.State == "ready" && state.PolicyHash == HttpProxyPolicy.Hash(ConfigurationSnapshotFactory.ToHttpSnapshot(channel)));
    private IReadOnlyList<PeerMappingAddress> peerAddresses = [];

    public void AttachControlWriter(FrameWriter writer) => ControlWriter = writer;
    public string ConfigVersion => Volatile.Read(ref configVersion);
    public void SetConfigVersion(string version)
    {
        Volatile.Write(ref peerAddresses, []);
        Volatile.Write(ref httpStates, []);
        Volatile.Write(ref configVersion, version);
    }
    public IReadOnlyList<PeerMappingAddress> PeerAddresses => Volatile.Read(ref peerAddresses);
    public void SetPeerAddresses(IReadOnlyList<PeerMappingAddress> addresses) => Volatile.Write(ref peerAddresses, addresses.ToArray());
    public async ValueTask SendConfigurationUpdateAsync(ClientConfigSnapshot snapshot, string version, CancellationToken cancellationToken)
    {
        await configUpdateLock.WaitAsync(cancellationToken);
        try
        {
            lock (configAckLock)
            {
                if (pendingConfigVersions.Count >= MaximumPendingConfigurations)
                    throw new ProtocolException("Too many unacknowledged configuration updates.");
                pendingConfigVersions.Enqueue(version);
            }
            await SendAsync(new Frame(FrameType.ConfigUpdate, JsonProtocolSerializer.Serialize(new ConfigUpdateMessage(SessionId, version, snapshot), ProtocolVersion)), cancellationToken);
        }
        catch { Close(); throw; }
        finally { configUpdateLock.Release(); }
    }
    public void ConfirmConfiguration(string version)
    {
        lock (configAckLock)
        {
            if (!pendingConfigVersions.TryPeek(out var expected) || !string.Equals(expected, version, StringComparison.Ordinal))
                throw new ProtocolException("Unexpected configuration acknowledgement.");
            pendingConfigVersions.Dequeue();
            SetConfigVersion(version);
        }
    }

    public void MarkPing(long sequence, long timestamp)
    {
        Interlocked.Exchange(ref pingTimestamp, timestamp);
        Interlocked.Exchange(ref expectedPongSequence, sequence);
    }

    public void TryMarkPong(long sequence, long timestamp)
    {
        if (sequence != Interlocked.Read(ref expectedPongSequence)) return;
        LastHeartbeatUtc = DateTimeOffset.UtcNow;
        LastHeartbeatRtt = Stopwatch.GetElapsedTime(Interlocked.Read(ref pingTimestamp), timestamp);
    }

    public ValueTask SendAsync(Frame frame, CancellationToken cancellationToken) =>
        ControlWriter?.WriteAsync(frame, cancellationToken) ?? throw new InvalidOperationException("Control writer is not available.");
}
