using System.Collections.Concurrent;
using System.Security.Cryptography;
using RelayLink.Protocol;
using RelayLink.Transport;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class PeerRelayRegistry(ServerRuntime runtime, MetricsRegistry metrics, AuditService audit, ILogger<PeerRelayRegistry> logger)
{
    private readonly ConcurrentDictionary<Guid, PeerRelay> relays = new();
    private readonly SemaphoreSlim limit = runtime.GlobalConnectionLimit;
    private readonly SemaphoreSlim pendingLimit = runtime.GlobalPendingLimit;
    private readonly object quotaLock = new();
    private readonly Dictionary<string, int> clientCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> channelCounts = new(StringComparer.Ordinal);

    public async Task RequestAsync(Session caller, PeerOpenRequestMessage request, CancellationToken cancellationToken)
    {
        if (!caller.IsReady || request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.MappingId) ||
            !runtime.Configuration.Clients.TryGetValue(caller.ClientId, out var callerConfig) || !callerConfig.Enabled ||
            !callerConfig.OutboundMappings.Any(mapping => mapping.Enabled && mapping.MappingId == request.MappingId))
        {
            await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken);
            return;
        }
        var mapping = callerConfig.OutboundMappings.Single(mapping => mapping.MappingId == request.MappingId);
        if (!runtime.Configuration.Clients.TryGetValue(mapping.TargetClientId, out var targetConfig) || !targetConfig.Enabled ||
            !runtime.Sessions.TryGet(mapping.TargetClientId, out var targetSession) || targetSession is not { IsReady: true } ||
            targetConfig.Channels.SingleOrDefault(channel => channel.ChannelId == mapping.TargetChannelId) is not { Enabled: true, AuthorizedClientsOnly: true } targetChannel ||
            !string.Equals(mapping.TargetCertificateSha256, targetConfig.E2eCertificateSha256, StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken);
            return;
        }
        if (!await limit.WaitAsync(0, cancellationToken))
        {
            await RejectAsync(caller, request.RequestId, ErrorCode.CapacityExceeded, cancellationToken);
            return;
        }
        if (!await pendingLimit.WaitAsync(0, cancellationToken))
        {
            limit.Release();
            await RejectAsync(caller, request.RequestId, ErrorCode.CapacityExceeded, cancellationToken);
            return;
        }
        if (!TryAcquireQuotas(callerConfig, targetConfig, targetChannel))
        {
            pendingLimit.Release();
            limit.Release();
            await RejectAsync(caller, request.RequestId, ErrorCode.CapacityExceeded, cancellationToken);
            return;
        }

        var relay = new PeerRelay(caller, targetSession, targetChannel.ChannelId, TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds), mapping.MappingId);
        var channelMetrics = metrics.For(targetConfig.ClientId, targetChannel.ChannelId);
        channelMetrics.PeerAccepted();
        if (!relays.TryAdd(relay.ConnectionId, relay))
        {
            channelMetrics.PeerFailed();
            limit.Release();
            pendingLimit.Release();
            ReleaseQuotas(caller.ClientId, targetConfig.ClientId, targetChannel.ChannelId);
            await RejectAsync(caller, request.RequestId, ErrorCode.ConnectionAborted, cancellationToken);
            return;
        }
        if (!caller.IsReady || !targetSession.IsReady ||
            !runtime.Configuration.Clients.TryGetValue(caller.ClientId, out var currentCaller) || !currentCaller.Enabled ||
            !runtime.Configuration.Clients.TryGetValue(targetConfig.ClientId, out var currentTarget) || !currentTarget.Enabled)
        {
            Cleanup(relay);
            await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken);
            return;
        }
        try
        {
            await targetSession.SendAsync(new Frame(FrameType.PeerOpen, JsonProtocolSerializer.Serialize(new PeerOpenMessage(relay.ConnectionId, targetSession.SessionId, caller.ClientId, targetChannel.ChannelId, relay.TargetToken, targetChannel.EndToEndEncryptionEnabled))), cancellationToken);
            await caller.SendAsync(new Frame(FrameType.PeerOpenGranted, JsonProtocolSerializer.Serialize(new PeerOpenGrantedMessage(request.RequestId, relay.ConnectionId, caller.SessionId, relay.CallerToken, targetChannel.EndToEndEncryptionEnabled))), cancellationToken);
            _ = RunAsync(relay);
        }
        catch
        {
            Cleanup(relay);
            throw;
        }
    }

    public bool TryBind(PeerBindDataMessage message, DataTunnel tunnel, out PeerRelay? relay)
    {
        relay = null;
        if (!relays.TryGetValue(message.ConnectionId, out var candidate) || !candidate.TryBind(message, tunnel)) return false;
        relay = candidate;
        return true;
    }

    public void CancelSession(Guid sessionId)
    {
        foreach (var relay in relays.Values.Where(relay => relay.Caller.SessionId == sessionId || relay.Target.SessionId == sessionId)) Cleanup(relay);
    }

    public void RevokeClientConnections(string clientId)
    {
        foreach (var relay in relays.Values.Where(relay => relay.Caller.ClientId == clientId || relay.Target.ClientId == clientId)) Cleanup(relay);
    }

    public ConnectionStatus[] ListConnections(string clientId, string channelId) => relays.Values
        .Where(relay => relay.Target.ClientId == clientId && relay.ChannelId == channelId && !relay.Completed)
        .Select(relay => relay.Snapshot()).ToArray();

    public bool TryDisconnect(string clientId, string channelId, Guid connectionId)
    {
        if (!relays.TryGetValue(connectionId, out var relay) || relay.Target.ClientId != clientId || relay.ChannelId != channelId) return false;
        Cleanup(relay, "admin_disconnected");
        return true;
    }

    public void RevokeChangedConnections(ClientConfiguration previous, ClientConfiguration updated)
    {
        foreach (var relay in relays.Values)
        {
            if (relay.Target.ClientId == previous.ClientId)
            {
                var before = previous.Channels.SingleOrDefault(channel => channel.ChannelId == relay.ChannelId);
                var after = updated.Channels.SingleOrDefault(channel => channel.ChannelId == relay.ChannelId);
                if (before is null || !before.HasSameRuntimeSettings(after)) { Cleanup(relay); continue; }
            }
            if (relay.Caller.ClientId == previous.ClientId)
            {
                var before = previous.OutboundMappings.SingleOrDefault(mapping => mapping.MappingId == relay.MappingId);
                var after = updated.OutboundMappings.SingleOrDefault(mapping => mapping.MappingId == relay.MappingId);
                if (before != after) Cleanup(relay);
            }
        }
    }

    private async Task RunAsync(PeerRelay relay)
    {
        try
        {
            using var timeout = new CancellationTokenSource(relay.OpenTimeout);
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, relay.LifetimeToken);
            var (caller, target) = await relay.WaitForBothAsync(opening.Token);
            if (!relay.TryBeginAuditOpening()) return;
            var auditRecorded = false;
            try
            {
                await audit.RecordAsync(new AuditEvent("peer_connection_opened", "success")
                {
                    ConnectionId = relay.ConnectionId, ClientId = relay.Target.ClientId, ChannelId = relay.ChannelId,
                    MappingId = relay.MappingId, CallerClientId = relay.Caller.ClientId, TargetClientId = relay.Target.ClientId
                }, opening.Token);
                auditRecorded = true;
            }
            finally { relay.CompleteAuditOpening(auditRecorded); }
            var accepted = new Frame(FrameType.PeerBindAccepted, JsonProtocolSerializer.Serialize(new PeerBindAcceptedMessage(relay.ConnectionId)));
            await caller.Writer.WriteAsync(accepted, opening.Token);
            await target.Writer.WriteAsync(accepted, opening.Token);
            if (relay.TryReleasePending()) pendingLimit.Release();
            lock (relay)
            {
                if (relay.Completed) return;
                relay.MarkOpened();
                metrics.For(relay.Target.ClientId, relay.ChannelId).PeerOpened();
            }
            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(relay.LifetimeToken);
            var channelMetrics = metrics.For(relay.Target.ClientId, relay.ChannelId);
            var callerToTarget = ForwardAsync(caller.Reader, target.Writer, count => { channelMetrics.AddPeerCiphertextToTarget(count); relay.AddToTarget(count); }, relayCancellation.Token);
            var targetToCaller = ForwardAsync(target.Reader, caller.Writer, count => { channelMetrics.AddPeerCiphertextToCaller(count); relay.AddToCaller(count); }, relayCancellation.Token);
            var first = await Task.WhenAny(callerToTarget, targetToCaller);
            var remaining = first == callerToTarget ? targetToCaller : callerToTarget;
            if (first.IsFaulted || first.IsCanceled) relayCancellation.Cancel();
            else if (await Task.WhenAny(remaining, Task.Delay(TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.HalfCloseDrainTimeoutSeconds), relayCancellation.Token)) != remaining)
                relayCancellation.Cancel();
            await Task.WhenAll(callerToTarget, targetToCaller);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ProtocolException or AuditUnavailableException)
        {
            relay.SetEndReason(exception is AuditUnavailableException ? "audit_unavailable" : exception is OperationCanceledException ? "cancelled" : "transport_error");
            logger.LogWarning(exception, "Peer relay {ConnectionId} ended.", relay.ConnectionId);
        }
        finally { Cleanup(relay); }
    }

    private static async Task ForwardAsync(FrameReader reader, FrameWriter writer, Action<int> bytesWritten, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await reader.ReadAsync(ProtocolConstants.MaxDataPayloadLength, cancellationToken)
                ?? throw new IOException("Peer data tunnel ended before FIN.");
            if (frame.Type is not (FrameType.Data or FrameType.Fin or FrameType.Reset)) throw new ProtocolException("Unexpected peer data frame.");
            await writer.WriteAsync(frame, cancellationToken);
            if (frame.Type == FrameType.Data) bytesWritten(frame.Payload.Length);
            if (frame.Type != FrameType.Data) return;
        }
    }

    private async Task RejectAsync(Session caller, Guid requestId, ErrorCode errorCode, CancellationToken cancellationToken)
    {
        try { await audit.RecordAsync(new AuditEvent("peer_connection_rejected", "denied")
            { ClientId = caller.ClientId, CallerClientId = caller.ClientId, ReasonCode = errorCode.ToString() }, cancellationToken); }
        catch (AuditUnavailableException) { /* The request is rejected regardless; diagnostics carry the storage failure. */ }
        await caller.SendAsync(new Frame(FrameType.PeerOpenRejected, JsonProtocolSerializer.Serialize(new PeerOpenRejectedMessage(requestId, errorCode))), cancellationToken);
    }

    private void Cleanup(PeerRelay relay, string reason = "completed")
    {
        if (relays.TryRemove(new KeyValuePair<Guid, PeerRelay>(relay.ConnectionId, relay)))
        {
            lock (relay)
            {
                relay.Complete(reason);
                var channelMetrics = metrics.For(relay.Target.ClientId, relay.ChannelId);
                if (relay.Opened) channelMetrics.PeerClosed(); else channelMetrics.PeerFailed();
            }
            if (relay.TryReleasePending()) pendingLimit.Release();
            limit.Release();
            ReleaseQuotas(relay.Caller.ClientId, relay.Target.ClientId, relay.ChannelId);
            _ = RecordTerminationAsync(relay);
        }
    }

    private async Task RecordTerminationAsync(PeerRelay relay)
    {
        await relay.WaitForAuditOpeningAsync();
        var snapshot = relay.Snapshot();
        try
        {
            await audit.RecordAsync(new AuditEvent(relay.AuditOpened ? "peer_connection_closed" : "peer_connection_rejected", relay.AuditOpened && relay.EndReason == "completed" ? "completed" : "aborted")
            {
                ReasonCode = relay.EndReason, ConnectionId = relay.ConnectionId, ClientId = relay.Target.ClientId,
                ChannelId = relay.ChannelId, MappingId = relay.MappingId, CallerClientId = relay.Caller.ClientId,
                TargetClientId = relay.Target.ClientId, DurationMs = (long)(DateTimeOffset.UtcNow - relay.StartedAtUtc).TotalMilliseconds,
                BytesToTarget = snapshot.BytesToTarget, BytesToCaller = snapshot.BytesToCaller
            });
        }
        catch (Exception exception) { logger.LogError(exception, "Could not persist peer termination audit for {ConnectionId}.", relay.ConnectionId); }
    }

    private bool TryAcquireQuotas(ClientConfiguration caller, ClientConfiguration target, ChannelConfiguration channel)
    {
        var key = $"{target.ClientId}/{channel.ChannelId}";
        lock (quotaLock)
        {
            if (clientCounts.GetValueOrDefault(caller.ClientId) >= caller.MaxConnections ||
                clientCounts.GetValueOrDefault(target.ClientId) >= target.MaxConnections ||
                channelCounts.GetValueOrDefault(key) >= channel.MaxConnections) return false;
            clientCounts[caller.ClientId] = clientCounts.GetValueOrDefault(caller.ClientId) + 1;
            clientCounts[target.ClientId] = clientCounts.GetValueOrDefault(target.ClientId) + 1;
            channelCounts[key] = channelCounts.GetValueOrDefault(key) + 1;
            return true;
        }
    }

    private void ReleaseQuotas(string callerId, string targetId, string channelId)
    {
        var key = $"{targetId}/{channelId}";
        lock (quotaLock)
        {
            clientCounts[callerId]--;
            clientCounts[targetId]--;
            channelCounts[key]--;
        }
    }
}

public sealed class PeerRelay(Session caller, Session target, string channelId, TimeSpan openTimeout, string mappingId = "")
{
    private readonly byte[] callerToken = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] targetToken = RandomNumberGenerator.GetBytes(32);
    private readonly TaskCompletionSource<DataTunnel> callerTunnel = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DataTunnel> targetTunnel = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private int callerBound;
    private int targetBound;
    private int pendingHeld = 1;
    private int opened;
    private int auditOpened;
    private TaskCompletionSource<bool>? auditOpening;
    private string endReason = "completed";
    private long bytesToTarget;
    private long bytesToCaller;
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public Session Caller { get; } = caller;
    public Session Target { get; } = target;
    public string ChannelId { get; } = channelId;
    public string MappingId { get; } = mappingId;
    public TimeSpan OpenTimeout { get; } = openTimeout;
    public string CallerToken => Convert.ToBase64String(callerToken);
    public string TargetToken => Convert.ToBase64String(targetToken);
    public Task WaitForCompletionAsync() => completion.Task;
    public CancellationToken LifetimeToken => lifetime.Token;
    public bool Completed => completion.Task.IsCompleted;
    public bool AuditOpened => Volatile.Read(ref auditOpened) == 1;
    public string EndReason => Volatile.Read(ref endReason);
    public bool TryBeginAuditOpening()
    {
        lock (this)
        {
            if (Completed) return false;
            auditOpening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }
    public void CompleteAuditOpening(bool success)
    {
        if (success) Volatile.Write(ref auditOpened, 1);
        Volatile.Read(ref auditOpening)?.TrySetResult(success);
    }
    public Task<bool> WaitForAuditOpeningAsync() => Volatile.Read(ref auditOpening)?.Task ?? Task.FromResult(false);
    public void SetEndReason(string reason) { if (!Completed) Volatile.Write(ref endReason, reason); }
    public void Complete(string reason = "completed")
    {
        if (!completion.TrySetResult()) return;
        if (reason != "completed") Volatile.Write(ref endReason, reason);
        lifetime.Cancel();
        CryptographicOperations.ZeroMemory(callerToken);
        CryptographicOperations.ZeroMemory(targetToken);
    }
    public bool TryReleasePending() => Interlocked.Exchange(ref pendingHeld, 0) == 1;
    public bool Opened => Volatile.Read(ref opened) == 1;
    public void MarkOpened() => Volatile.Write(ref opened, 1);
    public void AddToTarget(int count) => Interlocked.Add(ref bytesToTarget, count);
    public void AddToCaller(int count) => Interlocked.Add(ref bytesToCaller, count);
    public ConnectionStatus Snapshot() => new(ConnectionId, "end-to-end", Opened ? "relaying" : "connecting", Caller.ClientId, StartedAtUtc, Interlocked.Read(ref bytesToTarget), Interlocked.Read(ref bytesToCaller));

    public bool TryBind(PeerBindDataMessage message, DataTunnel tunnel)
    {
        var callerSide = message.Role == "caller";
        if (!callerSide && message.Role != "target") return false;
        var session = callerSide ? Caller : Target;
        var expected = callerSide ? callerToken : targetToken;
        if (message.SessionId != session.SessionId || !TryDecodeToken(message.Token, expected, out var actual)) return false;
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
        if (Interlocked.CompareExchange(ref (callerSide ? ref callerBound : ref targetBound), 1, 0) != 0) return false;
        return (callerSide ? callerTunnel : targetTunnel).TrySetResult(tunnel);
    }

    public async Task<(DataTunnel Caller, DataTunnel Target)> WaitForBothAsync(CancellationToken cancellationToken)
    {
        var caller = await callerTunnel.Task.WaitAsync(cancellationToken);
        var target = await targetTunnel.Task.WaitAsync(cancellationToken);
        return (caller, target);
    }

    private static bool TryDecodeToken(string candidate, byte[] expected, out byte[] actual)
    {
        try { actual = Convert.FromBase64String(candidate); }
        catch (FormatException) { actual = []; return false; }
        return actual.Length == expected.Length;
    }
}
