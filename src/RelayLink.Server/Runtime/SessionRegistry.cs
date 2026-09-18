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
    public string? E2eCertificateSha256 { get; set; }
    private FrameWriter? ControlWriter { get; set; }
    private long expectedPongSequence;
    private long pingTimestamp;
    private string configVersion = string.Empty;
    private string? pendingConfigVersion;
    private IReadOnlyList<PeerMappingAddress> peerAddresses = [];

    public void AttachControlWriter(FrameWriter writer) => ControlWriter = writer;
    public string ConfigVersion => Volatile.Read(ref configVersion);
    public void SetConfigVersion(string version)
    {
        Volatile.Write(ref peerAddresses, []);
        Volatile.Write(ref configVersion, version);
    }
    public IReadOnlyList<PeerMappingAddress> PeerAddresses => Volatile.Read(ref peerAddresses);
    public void SetPeerAddresses(IReadOnlyList<PeerMappingAddress> addresses) => Volatile.Write(ref peerAddresses, addresses.ToArray());
    public async ValueTask SendConfigurationUpdateAsync(ClientConfigSnapshot snapshot, string version, CancellationToken cancellationToken)
    {
        Volatile.Write(ref pendingConfigVersion, version);
        await SendAsync(new Frame(FrameType.ConfigUpdate, JsonProtocolSerializer.Serialize(new ConfigUpdateMessage(SessionId, version, snapshot))), cancellationToken);
    }
    public void ConfirmConfiguration(string version)
    {
        if (!string.Equals(Volatile.Read(ref pendingConfigVersion), version, StringComparison.Ordinal)) throw new ProtocolException("Unexpected configuration acknowledgement.");
        SetConfigVersion(version);
        Volatile.Write(ref pendingConfigVersion, null);
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
