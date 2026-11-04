using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelayLink.Protocol;
using RelayLink.Transport;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class PeerRelayRegistry(ServerRuntime runtime, MetricsRegistry metrics, AuditService audit, ILogger<PeerRelayRegistry> logger, CredentialStore? credentials = null)
{
    private readonly ConcurrentDictionary<Guid, PeerRelay> relays = new();
    private readonly SemaphoreSlim limit = runtime.GlobalConnectionLimit;
    private readonly SemaphoreSlim pendingLimit = runtime.GlobalPendingLimit;
    private readonly object quotaLock = new();
    private readonly Dictionary<string, int> clientCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> channelCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, long>> recentPeerRequests = new();
    private readonly object certificateGate = new();
    private X509Certificate2? serverCertificate;
    private static readonly long RecentRequestWindowTicks = Stopwatch.Frequency * 120;
    private const int RecentRequestsPerSession = 1024;

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
        if (targetChannel.InspectionMode == "server-peer")
        {
            if (caller.ProtocolVersion < ProtocolConstants.InspectionVersion || targetSession.ProtocolVersion < ProtocolConstants.InspectionVersion || targetChannel.TransportProtocol != "tcp" ||
                targetChannel.Application is not ("ssh" or "rdp" or "http" or "https") || !runtime.Configuration.Server.Tunnel.TlsEnabled)
            {
                await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken);
                return;
            }
            try { _ = ServerCertificate; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Inspection TLS certificate is unavailable for {ClientId}/{ChannelId}.", targetConfig.ClientId, targetChannel.ChannelId);
                await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken);
                return;
            }
        }
        else if (targetChannel.InspectionMode != "none")
        {
            await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken);
            return;
        }
        if (targetChannel.TransportProtocol == "udp" && (caller.ProtocolVersion < 4 || targetSession.ProtocolVersion < 4))
        {
            await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken);
            return;
        }
        HttpIngressContext? httpIngress = null;
        if (targetChannel.ApplicationProtocol == "http")
        {
            var address = caller.PeerAddresses.SingleOrDefault(address => address.MappingId == mapping.MappingId);
            if (caller.ProtocolVersion < 5 || !targetSession.IsHttpReady(targetChannel) || !targetChannel.EndToEndEncryptionEnabled || address is null)
            { await RejectAsync(caller, request.RequestId, ErrorCode.ChannelUnavailable, cancellationToken); return; }
            var http = targetChannel.Http!;
            httpIngress = new HttpIngressContext("peer", http.PeerEntryScheme, CallerClientId: caller.ClientId, MappingId: mapping.MappingId,
                EntryAuthority: $"{http.PeerAuthorityHost}:{address.LocalPort}", PolicyHash: HttpProxyPolicy.Hash(ConfigurationSnapshotFactory.ToHttpSnapshot(targetChannel)));
        }
        if (!TryRememberPeerRequest(caller.SessionId, request.RequestId))
        {
            await RejectAsync(caller, request.RequestId, ErrorCode.ProtocolError, cancellationToken);
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

        var relay = new PeerRelay(caller, targetSession, targetChannel.ChannelId, TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds), mapping.MappingId, targetChannel.TransportProtocol)
        { InspectionMode = targetChannel.InspectionMode };
        var setupTimeoutMilliseconds = PeerSetupTimeoutMilliseconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds, targetChannel.TargetConnectTimeoutSeconds);
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
            var waitMilliseconds = InspectionWaitMilliseconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds, targetChannel);
            await targetSession.SendAsync(new Frame(FrameType.PeerOpen, JsonProtocolSerializer.Serialize(new PeerOpenMessage(relay.ConnectionId, targetSession.SessionId, caller.ClientId, targetChannel.ChannelId, relay.TargetToken, targetChannel.EndToEndEncryptionEnabled, targetChannel.TransportProtocol, httpIngress) { SetupTimeoutMilliseconds = setupTimeoutMilliseconds, InspectionWaitTimeoutMilliseconds = waitMilliseconds, InspectionMode = targetChannel.InspectionMode }, targetSession.ProtocolVersion)), cancellationToken);
            await caller.SendAsync(new Frame(FrameType.PeerOpenGranted, JsonProtocolSerializer.Serialize(new PeerOpenGrantedMessage(request.RequestId, relay.ConnectionId, caller.SessionId, relay.CallerToken, targetChannel.EndToEndEncryptionEnabled, targetChannel.TransportProtocol, httpIngress) { SetupTimeoutMilliseconds = setupTimeoutMilliseconds, InspectionWaitTimeoutMilliseconds = waitMilliseconds, InspectionMode = targetChannel.InspectionMode }, caller.ProtocolVersion)), cancellationToken);
            if (relay.TryStartProcessing()) _ = RunAsync(relay);
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
        if (!relays.TryGetValue(message.ConnectionId, out var candidate) ||
            tunnel.Writer.ProtocolVersion != (message.Role == "caller" ? candidate.Caller : candidate.Target).ProtocolVersion ||
            !candidate.TryBind(message, tunnel)) return false;
        relay = candidate;
        return true;
    }

    public void CancelSession(Guid sessionId)
    {
        recentPeerRequests.TryRemove(sessionId, out _);
        foreach (var relay in relays.Values.Where(relay => relay.Caller.SessionId == sessionId || relay.Target.SessionId == sessionId)) Cleanup(relay);
    }

    public void ReportInspectionTargetReady(Session session, InspectionTargetReadyMessage message)
    {
        if (message.SessionId != session.SessionId || session.ProtocolVersion < ProtocolConstants.InspectionVersion)
            throw new ProtocolException("Inspection readiness session mismatch.");
        // A terminal cancellation may race a valid target response.
        if (!relays.TryGetValue(message.ConnectionId, out var relay)) return;
        if (relay.Target.SessionId != session.SessionId || relay.InspectionMode != "server-peer" || !relay.TargetOpening || !relay.TrySetTargetReady())
            throw new ProtocolException("Inspection readiness did not match the target leg.");
        metrics.For(relay.Target.ClientId, relay.ChannelId).MarkTargetSuccess();
    }

    public bool ReportTargetFailure(Session session, OpenFailedMessage message)
    {
        if (!relays.TryGetValue(message.ConnectionId, out var relay)) return false;
        if (relay.Target.SessionId != session.SessionId || relay.InspectionMode != "server-peer")
            throw new ProtocolException("Inspection target failure session mismatch.");
        if (relay.FailTarget(new IOException($"Target failed: {message.ErrorCode}.")))
            metrics.For(relay.Target.ClientId, relay.ChannelId).MarkTargetFailure(message.ErrorCode.ToString());
        return true;
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
                await audit.RecordAsync(new AuditEvent("peer_connection_opened", relay.InspectionMode == "server-peer" ? "prepared" : "success")
                {
                    ReasonCode = relay.InspectionMode == "server-peer" ? "inspection_preparing" : null,
                    ConnectionId = relay.ConnectionId, ClientId = relay.Target.ClientId, ChannelId = relay.ChannelId,
                    MappingId = relay.MappingId, CallerClientId = relay.Caller.ClientId, TargetClientId = relay.Target.ClientId
                }, opening.Token);
                auditRecorded = true;
            }
            finally { relay.CompleteAuditOpening(auditRecorded); }
            var accepted = new Frame(FrameType.PeerBindAccepted, JsonProtocolSerializer.Serialize(new PeerBindAcceptedMessage(relay.ConnectionId)));
            await caller.Writer.WriteAsync(accepted, opening.Token);
            await target.Writer.WriteAsync(accepted, opening.Token);
            if (relay.InspectionMode == "server-peer")
            {
                await InspectAsync(relay, caller, target, opening.Token);
                return;
            }
            MarkActive(relay);
            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(relay.LifetimeToken);
            using var idle = new RelayIdleWatch(IdleTimeoutFor(relay.TransportProtocol, runtime.Configuration.Server.Limits.TcpIdleTimeoutSeconds), relayCancellation.Token);
            var channelMetrics = metrics.For(relay.Target.ClientId, relay.ChannelId);
            var callerToTarget = ForwardAsync(caller.Reader, target.Writer, count => { channelMetrics.AddPeerCiphertextToTarget(count); relay.AddToTarget(count); idle.Touch(); }, idle.Token);
            var targetToCaller = ForwardAsync(target.Reader, caller.Writer, count => { channelMetrics.AddPeerCiphertextToCaller(count); relay.AddToCaller(count); idle.Touch(); }, idle.Token);
            var first = await Task.WhenAny(callerToTarget, targetToCaller);
            var remaining = first == callerToTarget ? targetToCaller : callerToTarget;
            if (first.IsFaulted || first.IsCanceled) relayCancellation.Cancel();
            else if (await Task.WhenAny(remaining, Task.Delay(TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.HalfCloseDrainTimeoutSeconds), relayCancellation.Token)) != remaining)
                relayCancellation.Cancel();
            await Task.WhenAll(callerToTarget, targetToCaller);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ProtocolException or AuditUnavailableException or AuthenticationException)
        {
            if (relay.EndReason == "completed") relay.SetEndReason(exception is AuditUnavailableException ? "audit_unavailable" : exception is AuthenticationException ? "inspection_rejected" : exception is OperationCanceledException ? "cancelled" : "transport_error");
            logger.LogWarning(exception, "Peer relay {ConnectionId} ended.", relay.ConnectionId);
        }
        catch (Exception exception)
        {
            if (relay.EndReason == "completed") relay.SetEndReason("unexpected");
            logger.LogError(exception, "Peer relay {ConnectionId} failed unexpectedly.", relay.ConnectionId);
        }
        finally
        {
            relay.CompleteProcessing();
            Cleanup(relay);
        }
    }

    private async Task InspectAsync(PeerRelay relay, DataTunnel caller, DataTunnel target, CancellationToken setupToken)
    {
        var channel = runtime.Configuration.Clients[relay.Target.ClientId].Channels.Single(item => item.ChannelId == relay.ChannelId);
        if (channel.InspectionMode != "server-peer" || string.IsNullOrWhiteSpace(channel.AccessSecret))
            throw new ProtocolException("Inspection channel disappeared before the handshake.");
        var limits = runtime.Configuration.Server.Limits;
        using var lifecycle = new InspectionLifetime(relay.LifetimeToken,
            TimeSpan.FromMilliseconds(InspectionWaitMilliseconds(limits.OpenTimeoutSeconds, channel)),
            TimeSpan.FromSeconds(limits.TcpIdleTimeoutSeconds));
        await using var callerStream = new FramedDuplexStream(caller.Reader, caller.Writer);
        await using var targetStream = new FramedDuplexStream(target.Reader, target.Writer);
        using var callerTls = new SslStream(callerStream, leaveInnerStreamOpen: true);
        using var targetTls = new SslStream(targetStream, leaveInnerStreamOpen: true);
        void Abort() { lifecycle.Abort(); caller.Socket.Dispose(); target.Socket.Dispose(); }
        using var termination = lifecycle.Token.Register(() => { caller.Socket.Dispose(); target.Socket.Dispose(); });
        SslServerAuthenticationOptions Options() => new()
        {
            ServerCertificate = ServerCertificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };
        await Task.WhenAll(callerTls.AuthenticateAsServerAsync(Options(), setupToken), targetTls.AuthenticateAsServerAsync(Options(), setupToken));
        if (!callerTls.IsEncrypted || !targetTls.IsEncrypted) throw new AuthenticationException("Inspection TLS is not encrypted.");
        var challenge = RandomNumberGenerator.GetBytes(32);
        await callerTls.WriteAsync(challenge, setupToken);
        var provided = new byte[32];
        await callerTls.ReadExactlyAsync(provided, setupToken);
        var expected = InspectionProof.Compute(channel.AccessSecret, "caller", relay.Caller.ClientId, channel.ChannelId, relay.ConnectionId, challenge);
        if (!CryptographicOperations.FixedTimeEquals(provided, expected)) throw new AuthenticationException("Caller inspection proof was invalid.");
        var targetChallenge = new byte[32];
        await targetTls.ReadExactlyAsync(targetChallenge, setupToken);
        await targetTls.WriteAsync(InspectionProof.Compute(channel.AccessSecret, "target", relay.Caller.ClientId, channel.ChannelId, relay.ConnectionId, targetChallenge), setupToken);
        if (credentials is not null) await credentials.CheckAvailableAsync(setupToken);
        // This byte permits bounded input to the plugin; it does not activate either lease.
        await callerTls.WriteAsync(new byte[] { 1 }, setupToken);
        var channelMetrics = metrics.For(relay.Target.ClientId, relay.ChannelId);
        void FromCaller(int count) { channelMetrics.AddApplicationToTarget(count); relay.AddToTarget(count); lifecycle.Touch(); }
        void FromTarget(int count) { channelMetrics.AddApplicationToCaller(count); relay.AddToCaller(count); lifecycle.Touch(); }
        async ValueTask<IDisposable?> AcquireApproval(CancellationToken token)
        {
            if (!relay.Opened) return new InspectionApprovalLease();
            if (!await pendingLimit.WaitAsync(0, token)) return null;
            var idleSuspension = lifecycle.SuspendIdle();
            return new InspectionApprovalLease(() => { idleSuspension.Dispose(); pendingLimit.Release(); });
        }
        async Task<Stream> OpenTarget(CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifecycle.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(channel.TargetConnectTimeoutSeconds + 1));
            relay.BeginTargetOpening();
            await targetTls.WriteAsync(new byte[] { 1 }, deadline.Token);
            await relay.WaitForTargetReadyAsync(deadline.Token);
            // Target is now piping the leg, so upstream TLS/SSH authentication can proceed.
            return targetTls;
        }
        async ValueTask Established(CancellationToken token)
        {
            await audit.RecordAsync(new AuditEvent("inspection_session_opened", "success")
            { ConnectionId = relay.ConnectionId, ClientId = relay.Target.ClientId, ChannelId = channel.ChannelId, CallerClientId = relay.Caller.ClientId }, token);
            await relay.Caller.SendAsync(new Frame(FrameType.InspectionActive, JsonProtocolSerializer.Serialize(new InspectionActiveMessage(relay.Caller.SessionId, relay.ConnectionId))), token);
            await relay.Target.SendAsync(new Frame(FrameType.InspectionActive, JsonProtocolSerializer.Serialize(new InspectionActiveMessage(relay.Target.SessionId, relay.ConnectionId))), token);
            token.ThrowIfCancellationRequested();
            if (!lifecycle.Activate()) throw new OperationCanceledException(token);
            MarkActive(relay);
        }
        bool opened;
        if (channel.Application == "ssh" && credentials is not null)
        {
            opened = await SshInspection.RelayAsync(callerTls, OpenTarget, relay.Target.ClientId, channel.ChannelId, channel,
                credentials, audit, relay.ConnectionId, lifecycle.Token, fromCaller: FromCaller, fromTarget: FromTarget,
                onAuthenticated: token => Established(token).AsTask(), onActivity: lifecycle.Touch, setupTimeout: TimeSpan.FromSeconds(limits.OpenTimeoutSeconds),
                writeTimeout: TimeSpan.FromSeconds(limits.BlockedWriteTimeoutSeconds), halfCloseTimeout: TimeSpan.FromSeconds(limits.HalfCloseDrainTimeoutSeconds), acquireApprovalLease: AcquireApproval, onRejected: relay.SetEndReason, logger: logger);
        }
        else if (channel.Application is "http" or "https")
        {
            opened = await HttpInspection.RelayAsync(callerTls, channel, OpenTarget,
                async (decision, token) =>
                {
                    if (!decision.Allowed) relay.SetEndReason(decision.Reason);
                    await audit.RecordAsync(new AuditEvent("inspection_decision", decision.Allowed ? "success" : "denied")
                    { ReasonCode = $"request:{decision.RequestId} " + decision.Summary + " " + decision.Reason, ConnectionId = relay.ConnectionId,
                        ClientId = relay.Target.ClientId, ChannelId = channel.ChannelId, CallerClientId = relay.Caller.ClientId }, token);
                },
                async (response, token) => await audit.RecordAsync(new AuditEvent("inspection_response", response.Outcome)
                { ReasonCode = $"request:{response.RequestId} " + response.Summary + " " + response.StatusCode + " " + response.Reason, ConnectionId = relay.ConnectionId,
                    BytesToTarget = response.BytesToTarget, BytesToCaller = response.BytesToCaller, DurationMs = (long)response.ElapsedMilliseconds,
                    ClientId = relay.Target.ClientId, ChannelId = channel.ChannelId, CallerClientId = relay.Caller.ClientId }, token),
                lifecycle.Token, FromCaller, FromTarget, Established,
                new HttpInspectionRelayOptions { SetupTimeout = TimeSpan.FromSeconds(limits.OpenTimeoutSeconds),
                    BlockedWriteTimeout = TimeSpan.FromSeconds(limits.BlockedWriteTimeoutSeconds),
                    HalfCloseTimeout = TimeSpan.FromSeconds(limits.HalfCloseDrainTimeoutSeconds), Abort = Abort, AcquireApprovalLease = AcquireApproval,
                    OnApprovalWaiting = async (decision, token) => await audit.RecordAsync(new AuditEvent("inspection_decision", "waiting")
                    { ReasonCode = $"request:{decision.RequestId} " + decision.Summary + " approval_waiting", ConnectionId = relay.ConnectionId,
                        ClientId = relay.Target.ClientId, ChannelId = channel.ChannelId, CallerClientId = relay.Caller.ClientId }, token) });
        }
        else
        {
            await targetTls.WriteAsync(new byte[] { 0 }, lifecycle.Token);
            relay.SetEndReason("inspection_denied");
            return;
        }
        if (!opened && relay.EndReason == "completed") relay.SetEndReason("inspection_denied");
    }

    private void MarkActive(PeerRelay relay)
    {
        lock (relay)
        {
            if (relay.Completed || relay.Opened) return;
            relay.MarkOpened();
            metrics.For(relay.Target.ClientId, relay.ChannelId).PeerOpened();
            if (relay.TryReleasePending()) pendingLimit.Release();
        }
    }

    internal static int InspectionWaitMilliseconds(int openSeconds, ChannelConfiguration channel) =>
        (int)Math.Clamp(((long)Math.Max(1, openSeconds) * 3 + Math.Clamp(channel.Inspection?.ApprovalTimeoutSeconds ?? 60, 1, 300)
            + Math.Max(1, channel.TargetConnectTimeoutSeconds) + 5) * 1000, 1000, 900_000);

    private X509Certificate2 ServerCertificate
    {
        get
        {
            if (serverCertificate is not null) return serverCertificate;
            lock (certificateGate)
            {
                if (serverCertificate is not null) return serverCertificate;
                var tunnel = runtime.Configuration.Server.Tunnel;
                return serverCertificate = ServerCertificateLoader.Load(tunnel.CertificatePemPath, tunnel.PrivateKeyPemPath);
            }
        }
    }

    internal static int PeerSetupTimeoutMilliseconds(int openTimeoutSeconds, int targetConnectTimeoutSeconds)
    {
        var milliseconds = (long)Math.Max(1, openTimeoutSeconds) * 1000L + (long)Math.Max(1, targetConnectTimeoutSeconds) * 1000L;
        return milliseconds >= int.MaxValue ? int.MaxValue : (int)milliseconds;
    }

    internal static TimeSpan IdleTimeoutFor(string transportProtocol, int tcpIdleTimeoutSeconds) =>
        transportProtocol == "udp" ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, tcpIdleTimeoutSeconds));

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

    private bool TryRememberPeerRequest(Guid sessionId, Guid requestId)
    {
        var now = Stopwatch.GetTimestamp();
        var window = recentPeerRequests.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, long>());
        while (true)
        {
            if (window.TryAdd(requestId, now))
            {
                if (window.Count > RecentRequestsPerSession) PrunePeerRequests(window, now);
                return true;
            }

            if (!window.TryGetValue(requestId, out var seen)) continue;
            if (now - seen <= RecentRequestWindowTicks) return false;
            if (window.TryUpdate(requestId, now, seen)) return true;
        }
    }

    private static void PrunePeerRequests(ConcurrentDictionary<Guid, long> window, long now)
    {
        foreach (var entry in window)
        {
            if (now - entry.Value > RecentRequestWindowTicks) window.TryRemove(entry.Key, out _);
        }

        while (window.Count > RecentRequestsPerSession)
        {
            var oldestTicks = long.MaxValue;
            var oldestId = Guid.Empty;
            var found = false;
            foreach (var entry in window)
            {
                if (entry.Value >= oldestTicks) continue;
                oldestTicks = entry.Value;
                oldestId = entry.Key;
                found = true;
            }

            if (!found || !window.TryRemove(oldestId, out _)) return;
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
            lock (relay) relay.Complete(reason);
            _ = FinishCleanupAsync(relay);
        }
    }

    private async Task FinishCleanupAsync(PeerRelay relay)
    {
        // Cancellation can race the successful-write callback. Join all forwarding
        // before releasing capacity and taking the final audit byte snapshot.
        await relay.WaitForProcessingAsync();
        var channelMetrics = metrics.For(relay.Target.ClientId, relay.ChannelId);
        if (relay.Opened) channelMetrics.PeerClosed(); else channelMetrics.PeerFailed();
        if (relay.TryReleasePending()) pendingLimit.Release();
        limit.Release();
        ReleaseQuotas(relay.Caller.ClientId, relay.Target.ClientId, relay.ChannelId);
        await RecordTerminationAsync(relay);
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

public sealed class PeerRelay(Session caller, Session target, string channelId, TimeSpan openTimeout, string mappingId = "", string transportProtocol = "tcp")
{
    private readonly byte[] callerToken = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] targetToken = RandomNumberGenerator.GetBytes(32);
    private readonly TaskCompletionSource<DataTunnel> callerTunnel = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DataTunnel> targetTunnel = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource targetReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int targetOpening;
    private readonly CancellationTokenSource lifetime = new();
    private int callerBound;
    private int targetBound;
    private int pendingHeld = 1;
    private int opened;
    private int auditOpened;
    private TaskCompletionSource<bool>? auditOpening;
    private TaskCompletionSource? processing;
    private string endReason = "completed";
    private long bytesToTarget;
    private long bytesToCaller;
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public Session Caller { get; } = caller;
    public Session Target { get; } = target;
    public string ChannelId { get; } = channelId;
    public string TransportProtocol { get; } = transportProtocol;
    public string MappingId { get; } = mappingId;
    public string InspectionMode { get; init; } = "none";
    public TimeSpan OpenTimeout { get; } = openTimeout;
    public string CallerToken => Convert.ToBase64String(callerToken);
    public string TargetToken => Convert.ToBase64String(targetToken);
    public Task WaitForCompletionAsync() => completion.Task;
    public CancellationToken LifetimeToken => lifetime.Token;
    public bool Completed => completion.Task.IsCompleted;
    public bool AuditOpened => Volatile.Read(ref auditOpened) == 1;
    public string EndReason => Volatile.Read(ref endReason);
    public bool TargetOpening => Volatile.Read(ref targetOpening) == 1;
    public bool TryStartProcessing()
    {
        lock (this)
        {
            if (Completed || processing is not null) return false;
            processing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }
    public void CompleteProcessing() => Volatile.Read(ref processing)?.TrySetResult();
    public Task WaitForProcessingAsync() => Volatile.Read(ref processing)?.Task ?? Task.CompletedTask;
    public void BeginTargetOpening() => Volatile.Write(ref targetOpening, 1);
    public bool TrySetTargetReady() => targetReady.TrySetResult();
    public bool FailTarget(Exception exception)
    {
        if (!targetReady.TrySetException(exception)) return false;
        _ = targetReady.Task.Exception;
        return true;
    }
    public Task WaitForTargetReadyAsync(CancellationToken token) => targetReady.Task.WaitAsync(token);
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
    public ConnectionStatus Snapshot() => new(ConnectionId, InspectionMode == "server-peer" ? "server-peer" : "end-to-end", Opened ? "relaying" : "connecting", Caller.ClientId, StartedAtUtc, Interlocked.Read(ref bytesToTarget), Interlocked.Read(ref bytesToCaller));

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
