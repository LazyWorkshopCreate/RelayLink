using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.Agent;

public sealed class ControlSessionWorker(AgentConfiguration configuration, AgentStatus status, AgentDiagnosticLog diagnostics, ILogger<ControlSessionWorker> logger) : BackgroundService
{
    private readonly Random random = new();
    private readonly X509Certificate2Collection? trustedRoots = configuration.UseTls ? AgentConfigurationLoader.LoadTrustedRoots(configuration) : null;
    private readonly AgentIdentity identity = AgentIdentity.LoadOrCreate(configuration.E2eIdentityPath, configuration.ClientId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var permanentFailure = false;
            try
            {
                await RunSessionAsync(stoppingToken);
                failures = 0;
            }
            catch (AgentPermanentException exception)
            {
                permanentFailure = true;
                logger.LogWarning(exception, "Agent control session rejected; retrying slowly.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Agent control session ended; reconnecting.");
            }

            var ceiling = permanentFailure
                ? TimeSpan.FromSeconds(configuration.Reconnect.PermanentErrorDelaySeconds + random.Next(0, 16))
                : TimeSpan.FromSeconds(random.NextDouble() * Math.Min(configuration.Reconnect.MaxDelaySeconds, configuration.Reconnect.InitialDelaySeconds * Math.Pow(2, Math.Min(failures++, 20))));
            await Task.Delay(ceiling, stoppingToken);
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(configuration.ServerHost, configuration.ServerPort, stoppingToken);
        tcpClient.NoDelay = true;
        await using Stream tls = await CreateTransportStreamAsync(tcpClient, stoppingToken);

        var reader = new FrameReader(tls);
        var writer = new FrameWriter(tls);
        await writer.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage(configuration.ClientId, configuration.Secret, GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0", identity.Fingerprint))), stoppingToken);
        var accepted = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, stoppingToken);
        if (accepted?.Type == FrameType.Error) throw new AgentPermanentException("Server rejected registration.");
        if (accepted?.Type != FrameType.RegisterAccepted) throw new ProtocolException("Expected RegisterAccepted.");
        var registration = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted.Payload.Span);
        ValidateSnapshot(registration);
        await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registration.SessionId, registration.ConfigVersion))), stoppingToken);
        var ready = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, stoppingToken);
        if (ready?.Type == FrameType.Error) throw new AgentPermanentException("Server rejected configuration acknowledgement.");
        if (ready?.Type != FrameType.Ready || JsonProtocolSerializer.Deserialize<ReadyMessage>(ready.Payload.Span).SessionId != registration.SessionId) throw new ProtocolException("Expected Ready for the current session.");

        using var sessionScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["clientId"] = configuration.ClientId,
            ["sessionId"] = registration.SessionId
        });
        logger.LogInformation("Agent control session is online with session {SessionId}.", registration.SessionId);
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var activeConfiguration = new SessionConfiguration(registration.ConfigVersion, registration.Config);
        await using var peerSession = new PeerSessionCoordinator(configuration, identity, registration.SessionId, writer, logger, sessionCancellation.Token);
        peerSession.ApplyConfiguration(registration.Config);
        if (registration.Config.OutboundMappings.Count > 0)
            await writer.WriteAsync(new Frame(FrameType.PeerMappingStatus, JsonProtocolSerializer.Serialize(new PeerMappingStatusMessage(registration.SessionId, registration.ConfigVersion, peerSession.Addresses))), sessionCancellation.Token);
        status.SetOnline(registration.Config, peerSession.Addresses);
        using var openLimit = new SemaphoreSlim(registration.Config.MaxConnections);
        var startSignals = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        try
        {
            while (!sessionCancellation.IsCancellationRequested)
            {
                using var heartbeatTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
                heartbeatTimeout.CancelAfter(TimeSpan.FromSeconds(registration.HeartbeatTimeoutSeconds));
                var frame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, heartbeatTimeout.Token);
                if (frame is null) return;
                if (frame.Type == FrameType.Ping)
                {
                    var ping = JsonProtocolSerializer.Deserialize<PingMessage>(frame.Payload.Span);
                    await writer.WriteAsync(new Frame(FrameType.Pong, JsonProtocolSerializer.Serialize(new PongMessage(ping.Sequence))), stoppingToken);
                    continue;
                }

                if (frame.Type == FrameType.Open)
                {
                    var open = JsonProtocolSerializer.Deserialize<OpenMessage>(frame.Payload.Span);
                    _ = HandleOpenWithLimitAsync(open, registration.SessionId, activeConfiguration, writer, openLimit, startSignals, sessionCancellation.Token);
                    continue;
                }

                if (frame.Type == FrameType.Start)
                {
                    var start = JsonProtocolSerializer.Deserialize<StartMessage>(frame.Payload.Span);
                    if (startSignals.TryGetValue(start.ConnectionId, out var signal)) signal.TrySetResult();
                    continue;
                }

                if (frame.Type == FrameType.ConfigUpdate)
                {
                    var update = JsonProtocolSerializer.Deserialize<ConfigUpdateMessage>(frame.Payload.Span);
                    if (update.SessionId != registration.SessionId) throw new ProtocolException("Configuration update did not belong to the current session.");
                    ValidateSnapshot(update.ConfigVersion, update.Config);
                    activeConfiguration.Replace(update.ConfigVersion, update.Config);
                    peerSession.ApplyConfiguration(update.Config);
                    await writer.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(update.SessionId, update.ConfigVersion))), sessionCancellation.Token);
                    if (update.Config.OutboundMappings.Count > 0)
                        await writer.WriteAsync(new Frame(FrameType.PeerMappingStatus, JsonProtocolSerializer.Serialize(new PeerMappingStatusMessage(update.SessionId, update.ConfigVersion, peerSession.Addresses))), sessionCancellation.Token);
                    status.SetOnline(update.Config, peerSession.Addresses);
                    continue;
                }

                if (frame.Type == FrameType.PeerOpenGranted)
                {
                    peerSession.Grant(JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>(frame.Payload.Span));
                    continue;
                }
                if (frame.Type == FrameType.PeerOpenRejected)
                {
                    peerSession.Reject(JsonProtocolSerializer.Deserialize<PeerOpenRejectedMessage>(frame.Payload.Span));
                    continue;
                }
                if (frame.Type == FrameType.PeerOpen)
                {
                    peerSession.Open(JsonProtocolSerializer.Deserialize<PeerOpenMessage>(frame.Payload.Span));
                    continue;
                }

                if (frame.Type == FrameType.Error) throw new AgentPermanentException("Server closed control session with error.");
                throw new ProtocolException($"Unexpected control frame: {frame.Type}.");
            }
        }
        finally
        {
            sessionCancellation.Cancel();
            foreach (var signal in startSignals.Values) signal.TrySetCanceled();
            status.SetOffline();
        }
    }

    private async Task HandleOpenWithLimitAsync(OpenMessage open, Guid sessionId, SessionConfiguration activeConfiguration, FrameWriter controlWriter, SemaphoreSlim openLimit, ConcurrentDictionary<Guid, TaskCompletionSource> startSignals, CancellationToken cancellationToken)
    {
        if (!await openLimit.WaitAsync(0, cancellationToken))
        {
            await diagnostics.WriteAsync(open.ConnectionId, open.ChannelId, "capacity_rejected");
            await SendOpenFailedAsync(open, ErrorCode.CapacityExceeded, controlWriter, cancellationToken);
            return;
        }

        try
        {
            await HandleOpenAsync(open, sessionId, activeConfiguration.Current, controlWriter, startSignals, cancellationToken);
        }
        finally
        {
            openLimit.Release();
        }
    }

    private async Task HandleOpenAsync(OpenMessage open, Guid sessionId, (string Version, ClientConfigSnapshot Config) activeConfiguration, FrameWriter controlWriter, ConcurrentDictionary<Guid, TaskCompletionSource> startSignals, CancellationToken sessionCancellation)
    {
        var started = Stopwatch.GetTimestamp();
        var phase = "open_received";
        var outcome = "failed";
        long toServerBytes = 0, toTargetBytes = 0;
        var lastToServer = started;
        var lastToTarget = started;
        await diagnostics.WriteAsync(open.ConnectionId, open.ChannelId, phase);
        logger.LogInformation("Agent proxy {ConnectionId} received Open for {ChannelId}.", open.ConnectionId, open.ChannelId);
        if (open.SessionId != sessionId || !string.Equals(open.ConfigVersion, activeConfiguration.Version, StringComparison.Ordinal))
        {
            throw new ProtocolException("Open did not belong to the current configuration session.");
        }

        var channel = activeConfiguration.Config.Channels.SingleOrDefault(candidate => candidate.ChannelId == open.ChannelId && candidate.Enabled);
        if (channel is null)
        {
            await diagnostics.WriteAsync(open.ConnectionId, open.ChannelId, "channel_unavailable");
            await SendOpenFailedAsync(open, ErrorCode.ChannelUnavailable, controlWriter, sessionCancellation);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, open.RemainingOpenTimeoutMs)));
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!startSignals.TryAdd(open.ConnectionId, startSignal)) throw new ProtocolException("Duplicate Open connection ID.");
        try
        {
            using var dataClient = new TcpClient();
            await dataClient.ConnectAsync(configuration.ServerHost, configuration.EffectiveDataPort, deadline.Token);
            dataClient.NoDelay = true;
            var dataStream = dataClient.GetStream();
            var reader = new FrameReader(dataStream);
            var writer = new FrameWriter(dataStream);
            await writer.WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), deadline.Token);
            var bound = await reader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, deadline.Token);
            if (bound?.Type != FrameType.BindAccepted || JsonProtocolSerializer.Deserialize<BindAcceptedMessage>(bound.Payload.Span).ConnectionId != open.ConnectionId)
            {
                throw new ProtocolException("Server rejected data tunnel binding.");
            }
            phase = "data_bound";
            await diagnostics.WriteAsync(open.ConnectionId, open.ChannelId, phase, elapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            using var target = new TcpClient();
            using var targetTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            targetTimeout.CancelAfter(TimeSpan.FromSeconds(channel.TargetConnectTimeoutSeconds));
            var connectStarted = Stopwatch.GetTimestamp();
            await target.ConnectAsync(channel.TargetHost, channel.TargetPort, targetTimeout.Token);
            target.NoDelay = true;
            var duration = (int)Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds;
            await controlWriter.WriteAsync(new Frame(FrameType.TargetReady, JsonProtocolSerializer.Serialize(new TargetReadyMessage(open.ConnectionId, duration))), deadline.Token);
            phase = "target_ready";
            await diagnostics.WriteAsync(open.ConnectionId, open.ChannelId, phase, elapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await startSignal.Task.WaitAsync(deadline.Token);
            phase = "relay_started";
            logger.LogInformation("Agent proxy {ConnectionId} started relay for {ChannelId}; target connect {TargetConnectMs} ms.", open.ConnectionId, open.ChannelId, duration);
            await diagnostics.WriteAsync(open.ConnectionId, open.ChannelId, phase, elapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
            using var progressCancellation = new CancellationTokenSource();
            var progress = ReportProgressAsync(open.ConnectionId, open.ChannelId, started,
                () => (Interlocked.Read(ref toServerBytes), Interlocked.Read(ref toTargetBytes), Interlocked.Read(ref lastToServer), Interlocked.Read(ref lastToTarget)),
                progressCancellation.Token);
            try
            {
                var toServer = CopyDirectionAsync(open.ConnectionId, open.ChannelId, "target_to_server", target.Client, dataClient.Client, relayCancellation.Token,
                    count => { Interlocked.Add(ref toServerBytes, count); Interlocked.Exchange(ref lastToServer, Stopwatch.GetTimestamp()); });
                var toTarget = CopyDirectionAsync(open.ConnectionId, open.ChannelId, "server_to_target", dataClient.Client, target.Client, relayCancellation.Token,
                    count => { Interlocked.Add(ref toTargetBytes, count); Interlocked.Exchange(ref lastToTarget, Stopwatch.GetTimestamp()); });
                await RelayPump.CompleteBidirectionalAsync(toServer, toTarget, () =>
                {
                    relayCancellation.Cancel();
                    target.Dispose();
                    dataClient.Dispose();
                }, TimeSpan.FromSeconds(300));
                outcome = "completed";
            }
            finally { progressCancellation.Cancel(); await progress; }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            outcome = "session_cancelled";
        }
        catch (SocketException exception)
        {
            outcome = exception.SocketErrorCode.ToString();
            logger.LogWarning(exception, "Agent proxy {ConnectionId} failed in {Phase} for {ChannelId}.", open.ConnectionId, phase, open.ChannelId);
            var error = exception.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
                ? ErrorCode.TargetDnsFailed
                : ErrorCode.TargetConnectFailed;
            await SendOpenFailedAsync(open, error, controlWriter, sessionCancellation);
        }
        catch (OperationCanceledException)
        {
            outcome = "timeout";
            await SendOpenFailedAsync(open, ErrorCode.TargetConnectTimeout, controlWriter, sessionCancellation);
        }
        catch (Exception exception)
        {
            outcome = exception.GetType().Name;
            logger.LogWarning(exception, "Agent proxy {ConnectionId} failed in {Phase} for {ChannelId}.", open.ConnectionId, phase, open.ChannelId);
            await SendOpenFailedAsync(open, ErrorCode.ConnectionAborted, controlWriter, sessionCancellation);
        }
        finally
        {
            await diagnostics.WriteAsync(open.ConnectionId, open.ChannelId, outcome,
                Interlocked.Read(ref toServerBytes), Interlocked.Read(ref toTargetBytes),
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(Interlocked.Read(ref lastToServer)).TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(Interlocked.Read(ref lastToTarget)).TotalMilliseconds,
                outcome == "completed" ? null : phase);
            logger.LogInformation("Agent proxy {ConnectionId} ended: {Outcome}, phase {Phase}, to server {ToServerBytes} bytes, to target {ToTargetBytes} bytes, elapsed {ElapsedMs} ms.",
                open.ConnectionId, outcome, phase, Interlocked.Read(ref toServerBytes), Interlocked.Read(ref toTargetBytes), (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            startSignals.TryRemove(open.ConnectionId, out _);
        }
    }

    private async Task ReportProgressAsync(Guid connectionId, string channelId, long started,
        Func<(long ToServerBytes, long ToTargetBytes, long LastToServer, long LastToTarget)> snapshot, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var (toServer, toTarget, lastToServer, lastToTarget) = snapshot();
                var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var idleServer = (long)Stopwatch.GetElapsedTime(lastToServer).TotalMilliseconds;
                var idleTarget = (long)Stopwatch.GetElapsedTime(lastToTarget).TotalMilliseconds;
                await diagnostics.WriteAsync(connectionId, channelId, "relay_progress", toServer, toTarget, elapsed, idleServer, idleTarget);
                logger.LogInformation("Agent proxy {ConnectionId} progress: to server {ToServerBytes} bytes, to target {ToTargetBytes} bytes, idle {IdleServerMs}/{IdleTargetMs} ms.",
                    connectionId, toServer, toTarget, idleServer, idleTarget);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task CopyDirectionAsync(Guid connectionId, string channelId, string direction, Socket source, Socket destination,
        CancellationToken cancellationToken, Action<int> bytesWritten)
    {
        try
        {
            await RelayPump.CopySocketToSocketAsync(source, destination, cancellationToken, bytesWritten);
            logger.LogInformation("Agent proxy {ConnectionId} direction {Direction} reached EOF and propagated half-close.", connectionId, direction);
            await diagnostics.WriteAsync(connectionId, channelId, direction + "_eof");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await diagnostics.WriteAsync(connectionId, channelId, direction + "_cancelled");
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Agent proxy {ConnectionId} direction {Direction} failed.", connectionId, direction);
            await diagnostics.WriteAsync(connectionId, channelId, direction + "_failed", errorType: exception.GetType().Name);
            throw;
        }
    }

    private static async Task SendOpenFailedAsync(OpenMessage open, ErrorCode errorCode, FrameWriter writer, CancellationToken cancellationToken)
    {
        try { await writer.WriteAsync(new Frame(FrameType.OpenFailed, JsonProtocolSerializer.Serialize(new OpenFailedMessage(open.SessionId, open.ConnectionId, errorCode))), cancellationToken); }
        catch (IOException) { }
    }

    private static void ValidateSnapshot(RegisterAcceptedMessage registration) => ValidateSnapshot(registration.ConfigVersion, registration.Config, registration.HeartbeatIntervalSeconds, registration.HeartbeatTimeoutSeconds);

    private static void ValidateSnapshot(string version, ClientConfigSnapshot config, int heartbeatIntervalSeconds = 1, int heartbeatTimeoutSeconds = 1)
    {
        if (config.ClientId is null || config.Channels.Count > 100 || heartbeatIntervalSeconds < 1 || heartbeatTimeoutSeconds < heartbeatIntervalSeconds)
        {
            throw new AgentPermanentException("Server returned an invalid configuration snapshot.");
        }

        var computedVersion = ConfigurationSnapshotHasher.Compute(config);
        if (!string.Equals(computedVersion, version, StringComparison.Ordinal)) throw new AgentPermanentException("Configuration snapshot hash did not match.");
    }

    private async Task<Stream> CreateTransportStreamAsync(TcpClient client, CancellationToken cancellationToken)
    {
        if (!configuration.UseTls) return client.GetStream();
        var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = configuration.ServerHost, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.Online, RemoteCertificateValidationCallback = ValidateServerCertificate }, cancellationToken);
        return stream;
    }

    private bool ValidateServerCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        using var leaf = new X509Certificate2(certificate);
        using var customChain = new X509Chain();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots!);
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return customChain.Build(leaf);
    }
}

internal sealed class SessionConfiguration(string version, ClientConfigSnapshot config)
{
    private SessionConfigurationState current = new(version, config);
    public (string Version, ClientConfigSnapshot Config) Current
    {
        get
        {
            var snapshot = Volatile.Read(ref current);
            return (snapshot.Version, snapshot.Config);
        }
    }
    public void Replace(string version, ClientConfigSnapshot config) => Volatile.Write(ref current, new SessionConfigurationState(version, config));
}

internal sealed record SessionConfigurationState(string Version, ClientConfigSnapshot Config);

public sealed class AgentPermanentException(string message) : Exception(message);
