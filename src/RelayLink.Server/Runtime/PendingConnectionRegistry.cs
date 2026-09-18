using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using RelayLink.Server.Configuration;
using RelayLink.Transport;

namespace RelayLink.Server.Runtime;

public sealed class PendingConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, PendingConnection> pending = new();

    public PendingConnection Create(Session session, ChannelConfiguration channel, Socket caller, TimeSpan timeout)
    {
        var connection = new PendingConnection(session.SessionId, channel, caller, DateTimeOffset.UtcNow.Add(timeout), session.LifetimeToken);
        if (!pending.TryAdd(connection.ConnectionId, connection)) throw new InvalidOperationException("Connection ID collision.");
        return connection;
    }

    public bool TryBind(Guid sessionId, Guid connectionId, string channelId, string token, out PendingConnection? connection)
    {
        connection = null;
        if (!pending.TryGetValue(connectionId, out var candidate) || candidate.SessionId != sessionId || !candidate.Channel.ChannelId.Equals(channelId, StringComparison.Ordinal) || candidate.DeadlineUtc < DateTimeOffset.UtcNow || !candidate.TryConsumeToken(token))
        {
            return false;
        }

        connection = candidate;
        return true;
    }

    public void Remove(PendingConnection connection)
    {
        pending.TryRemove(new KeyValuePair<Guid, PendingConnection>(connection.ConnectionId, connection));
        connection.Dispose();
    }

    public void Cancel(Guid sessionId, Guid connectionId)
    {
        if (pending.TryGetValue(connectionId, out var connection) && connection.SessionId == sessionId)
        {
            Remove(connection);
        }
    }

    public bool TryGet(Guid connectionId, out PendingConnection? connection) => pending.TryGetValue(connectionId, out connection);
}

public sealed class PendingConnection(Guid sessionId, ChannelConfiguration channel, Socket caller, DateTimeOffset deadlineUtc, CancellationToken sessionToken = default) : IDisposable
{
    private readonly byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
    private int tokenConsumed;
    private int disposed;
    private readonly TaskCompletionSource<DataTunnel> bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> targetReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid SessionId { get; } = sessionId;
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public ChannelConfiguration Channel { get; } = channel;
    public Socket Caller { get; } = caller;
    public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
    public string Token => Convert.ToBase64String(tokenBytes);

    public bool TryConsumeToken(string candidate)
    {
        if (sessionToken.IsCancellationRequested || Volatile.Read(ref disposed) != 0 || DateTimeOffset.UtcNow > DeadlineUtc) return false;
        byte[] provided;
        try { provided = Convert.FromBase64String(candidate); }
        catch (FormatException) { return false; }
        return provided.Length == tokenBytes.Length && CryptographicOperations.FixedTimeEquals(provided, tokenBytes) && Interlocked.CompareExchange(ref tokenConsumed, 1, 0) == 0;
    }

    public bool TrySetTunnel(DataTunnel tunnel) => bound.TrySetResult(tunnel);
    public bool TrySetTargetReady(int durationMilliseconds) => bound.Task.IsCompletedSuccessfully && targetReady.TrySetResult(durationMilliseconds);
    public Task<DataTunnel> WaitForTunnelAsync(CancellationToken cancellationToken) => bound.Task.WaitAsync(cancellationToken);
    public Task<int> WaitForTargetReadyAsync(CancellationToken cancellationToken) => targetReady.Task.WaitAsync(cancellationToken);
    public Task WaitForCompletionAsync() => completed.Task;
    public void Complete() => completed.TrySetResult();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            Caller.Dispose();
            completed.TrySetResult();
        }
    }
}

public sealed record DataTunnel(Stream Stream, FrameReader Reader, FrameWriter Writer, Socket Socket);
