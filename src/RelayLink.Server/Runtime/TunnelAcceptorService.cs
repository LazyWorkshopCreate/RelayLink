using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Transport;

namespace RelayLink.Server.Runtime;

public sealed class TunnelAcceptorService(
    ServerRuntime runtime,
    AuthenticationService authentication,
    ClientConfigurationEditor clientEditor,
    PendingConnectionRegistry pendingConnections,
    PeerRelayRegistry peerRelays,
    MetricsRegistry metrics,
    AuditService audit,
    ILogger<TunnelAcceptorService> logger) : BackgroundService
{
    private readonly SemaphoreSlim unauthenticatedLimit = new(runtime.Configuration.Server.Limits.MaxUnauthenticatedConnections);
    private TcpListener? listener;
    private X509Certificate2? certificate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (runtime.Configuration.Server.Tunnel.TlsEnabled)
            certificate = LoadServerCertificate(runtime.Configuration.Server.Tunnel.CertificatePemPath, runtime.Configuration.Server.Tunnel.PrivateKeyPemPath);
        listener = new TcpListener(IPAddress.Parse(runtime.Configuration.Server.Tunnel.ListenAddress), runtime.Configuration.Server.Tunnel.Port);
        listener.Start();
        logger.LogInformation("Tunnel listener started on {Address}:{Port}.", runtime.Configuration.Server.Tunnel.ListenAddress, runtime.Configuration.Server.Tunnel.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var tcpClient = await listener.AcceptTcpClientAsync(stoppingToken);
                tcpClient.NoDelay = true;
                _ = HandleAcceptedAsync(tcpClient, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            certificate?.Dispose();
            certificate = null;
        }
    }

    private async Task HandleAcceptedAsync(TcpClient client, CancellationToken serverStoppingToken)
    {
        if (!await unauthenticatedLimit.WaitAsync(0, serverStoppingToken))
        {
            client.Dispose();
            logger.LogWarning("Rejected tunnel connection because the unauthenticated connection limit was reached.");
            return;
        }

        var slotReleased = false;
        try
        {
            using (client)
            await using (Stream stream = await CreateTransportStreamAsync(client, serverStoppingToken))
            using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverStoppingToken))
            {
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(runtime.Configuration.Server.Tunnel.HandshakeTimeoutSeconds));
                var reader = new FrameReader(stream);
                var writer = new FrameWriter(stream);
                var first = await reader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, handshakeTimeout.Token);
                if (first?.Type != FrameType.Register)
                {
                    await RecordRejectionAsync(null, "invalid_first_frame");
                    await TryWriteErrorAsync(writer, ErrorCode.ProtocolError, handshakeTimeout.Token);
                    return;
                }

                var register = JsonProtocolSerializer.Deserialize<RegisterMessage>(first.Payload.Span);
                if (!authentication.TryAuthenticate(register.ClientId, register.Secret, out var clientConfiguration))
                {
                    await RecordRejectionAsync(register.ClientId, "authentication_failed");
                    await TryWriteErrorAsync(writer, ErrorCode.AuthFailed, handshakeTimeout.Token);
                    logger.LogWarning("Authentication failed for client {ClientId}.", register.ClientId);
                    return;
                }

                if (register.E2eCertificateSha256 is null || register.E2eCertificateSha256.Length != 64 ||
                    !register.E2eCertificateSha256.All(Uri.IsHexDigit) ||
                    (clientConfiguration!.E2eCertificateSha256 is not null &&
                    !string.Equals(clientConfiguration.E2eCertificateSha256, register.E2eCertificateSha256, StringComparison.OrdinalIgnoreCase)))
                {
                    await RecordRejectionAsync(register.ClientId, "identity_mismatch");
                    await TryWriteErrorAsync(writer, ErrorCode.AuthFailed, handshakeTimeout.Token);
                    logger.LogWarning("E2E certificate identity mismatch for client {ClientId}.", register.ClientId);
                    return;
                }

                if (!runtime.Sessions.TryRegister(clientConfiguration!, out var session))
                {
                    await RecordRejectionAsync(register.ClientId, "duplicate_session");
                    await TryWriteErrorAsync(writer, ErrorCode.DuplicateSession, handshakeTimeout.Token);
                    logger.LogWarning("Rejected duplicate session for client {ClientId}.", register.ClientId);
                    return;
                }

                if (!runtime.Configuration.Clients.TryGetValue(register.ClientId, out var currentClient) || !currentClient.Enabled || session.LifetimeToken.IsCancellationRequested)
                {
                    await RecordRejectionAsync(register.ClientId, "client_disabled");
                    runtime.Sessions.Remove(session.ClientId, session.SessionId);
                    await TryWriteErrorAsync(writer, ErrorCode.AuthFailed, handshakeTimeout.Token);
                    return;
                }

                unauthenticatedLimit.Release();
                slotReleased = true;
                var sessionReadyAudited = false;
                try
                {
                    using var sessionScope = logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["clientId"] = session.ClientId,
                        ["sessionId"] = session.SessionId
                    });
                    session.AgentVersion = register.AgentVersion;
                    session.AttachControlWriter(writer);
                    var (snapshot, configVersion) = ConfigurationSnapshotFactory.Create(clientConfiguration!);
                    await writer.WriteAsync(new Frame(FrameType.RegisterAccepted, JsonProtocolSerializer.Serialize(new RegisterAcceptedMessage(session.SessionId, configVersion, snapshot, runtime.Configuration.Server.Tunnel.HeartbeatIntervalSeconds, runtime.Configuration.Server.Tunnel.HeartbeatTimeoutSeconds))), serverStoppingToken);
                    var acknowledgement = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, handshakeTimeout.Token);
                    if (acknowledgement?.Type != FrameType.ConfigAck)
                    {
                        await TryWriteErrorAsync(writer, ErrorCode.ConfigurationMismatch, handshakeTimeout.Token);
                        return;
                    }

                    var ack = JsonProtocolSerializer.Deserialize<ConfigAckMessage>(acknowledgement.Payload.Span);
                    if (ack.SessionId != session.SessionId || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(ack.ConfigVersion), Convert.FromHexString(configVersion)))
                    {
                        await TryWriteErrorAsync(writer, ErrorCode.ConfigurationMismatch, handshakeTimeout.Token);
                        return;
                    }

                    try { clientConfiguration = await clientEditor.BindIdentityAsync(clientConfiguration, register.E2eCertificateSha256, serverStoppingToken); }
                    catch (ClientUpdateException exception)
                    {
                        await TryWriteErrorAsync(writer, ErrorCode.AuthFailed, serverStoppingToken);
                        logger.LogWarning("E2E certificate registration rejected for client {ClientId}: {Reason}", register.ClientId, exception.Message);
                        return;
                    }
                    session.SetConfigVersion(configVersion);

                    await audit.RecordAsync(new AuditEvent("agent_session_ready", "success")
                    { ClientId = session.ClientId, SessionId = session.SessionId, RemoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() }, serverStoppingToken);
                    sessionReadyAudited = true;
                    await writer.WriteAsync(new Frame(FrameType.Ready, JsonProtocolSerializer.Serialize(new ReadyMessage(session.SessionId))), serverStoppingToken);
                    session.MarkReady();
                    logger.LogInformation("Client {ClientId} session {SessionId} is online.", session.ClientId, session.SessionId);
                    await RunControlSessionAsync(reader, writer, session, serverStoppingToken);
                }
                finally
                {
                    peerRelays.CancelSession(session.SessionId);
                    runtime.Sessions.Remove(session.ClientId, session.SessionId);
                    metrics.ResetTargets(session.ClientId);
                    logger.LogInformation("Client {ClientId} session {SessionId} is offline.", session.ClientId, session.SessionId);
                    try
                    {
                        await audit.RecordAsync(new AuditEvent(sessionReadyAudited ? "agent_session_closed" : "agent_session_rejected", sessionReadyAudited ? "closed" : "denied")
                        { ClientId = session.ClientId, SessionId = session.SessionId, RemoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString(), ReasonCode = sessionReadyAudited ? "session_ended" : "registration_incomplete", DurationMs = (long)(DateTimeOffset.UtcNow - session.ConnectedAtUtc).TotalMilliseconds }, CancellationToken.None);
                    }
                    catch (Exception exception) { logger.LogError(exception, "Could not persist Agent session termination audit for {SessionId}.", session.SessionId); }
                }
            }
        }
        catch (OperationCanceledException) when (serverStoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or ProtocolException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Tunnel connection closed before or during session establishment.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected tunnel session failure.");
        }
        finally
        {
            if (!slotReleased)
            {
                unauthenticatedLimit.Release();
            }
        }

        async Task RecordRejectionAsync(string? untrustedClientId, string reason)
        {
            var safeClientId = new string((untrustedClientId ?? string.Empty).Where(character => !char.IsControl(character)).Take(128).ToArray());
            try { await audit.RecordAsync(new AuditEvent("agent_session_rejected", "denied")
                { ClientId = safeClientId, RemoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString(), ReasonCode = reason }, CancellationToken.None); }
            catch (Exception exception) { logger.LogError(exception, "Could not persist Agent authentication rejection audit."); }
        }
    }

    private async Task RunControlSessionAsync(FrameReader reader, FrameWriter writer, Session session, CancellationToken stoppingToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.LifetimeToken);
        var cancellationToken = sessionCancellation.Token;
        var heartbeatTask = RunHeartbeatLoopAsync(writer, session, cancellationToken);
        var readTask = RunControlReadLoopAsync(reader, session, cancellationToken);
        try
        {
            var completed = await Task.WhenAny(heartbeatTask, readTask);
            sessionCancellation.Cancel();
            await completed;
        }
        finally
        {
            sessionCancellation.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
            try { await readTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task RunHeartbeatLoopAsync(FrameWriter writer, Session session, CancellationToken cancellationToken)
    {
        long sequence = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(runtime.Configuration.Server.Tunnel.HeartbeatIntervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (DateTimeOffset.UtcNow - session.LastHeartbeatUtc > TimeSpan.FromSeconds(runtime.Configuration.Server.Tunnel.HeartbeatTimeoutSeconds))
            {
                throw new TimeoutException("Control heartbeat timed out.");
            }

            var next = Interlocked.Increment(ref sequence);
            session.MarkPing(next, Stopwatch.GetTimestamp());
            await writer.WriteAsync(new Frame(FrameType.Ping, JsonProtocolSerializer.Serialize(new PingMessage(next))), cancellationToken);
        }
    }

    private async Task RunControlReadLoopAsync(FrameReader reader, Session session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, cancellationToken);
            if (frame is null) return;
            if (frame.Type == FrameType.Pong)
            {
                var pong = JsonProtocolSerializer.Deserialize<PongMessage>(frame.Payload.Span);
                session.TryMarkPong(pong.Sequence, Stopwatch.GetTimestamp());
                continue;
            }

            if (frame.Type == FrameType.OpenFailed)
            {
                var failed = JsonProtocolSerializer.Deserialize<OpenFailedMessage>(frame.Payload.Span);
                if (failed.SessionId != session.SessionId) throw new ProtocolException("OpenFailed session mismatch.");
                if (pendingConnections.TryGet(failed.ConnectionId, out var pending) && pending is not null)
                {
                    metrics.For(session.ClientId, pending.Channel.ChannelId).MarkTargetFailure(failed.ErrorCode.ToString());
                }
                pendingConnections.Cancel(session.SessionId, failed.ConnectionId);
                continue;
            }

            if (frame.Type == FrameType.TargetReady)
            {
                var ready = JsonProtocolSerializer.Deserialize<TargetReadyMessage>(frame.Payload.Span);
                if (!pendingConnections.TryGet(ready.ConnectionId, out var pending) || pending is null ||
                    pending.SessionId != session.SessionId || !pending.TrySetTargetReady(ready.TargetConnectDurationMs))
                    throw new ProtocolException("TargetReady did not match a bound connection in this control session.");
                metrics.For(session.ClientId, pending.Channel.ChannelId).MarkTargetSuccess();
                continue;
            }

            if (frame.Type == FrameType.ConfigAck)
            {
                var acknowledgement = JsonProtocolSerializer.Deserialize<ConfigAckMessage>(frame.Payload.Span);
                if (acknowledgement.SessionId != session.SessionId) throw new ProtocolException("Configuration acknowledgement session mismatch.");
                session.ConfirmConfiguration(acknowledgement.ConfigVersion);
                continue;
            }

            if (frame.Type == FrameType.PeerMappingStatus)
            {
                var status = JsonProtocolSerializer.Deserialize<PeerMappingStatusMessage>(frame.Payload.Span);
                if (status.SessionId != session.SessionId || !string.Equals(status.ConfigVersion, session.ConfigVersion, StringComparison.Ordinal) ||
                    status.Addresses is null || status.Addresses.Count > runtime.Configuration.Server.Limits.MaxChannelsPerClient ||
                    !runtime.Configuration.Clients.TryGetValue(session.ClientId, out var client) ||
                    status.Addresses.Any(address => address.LocalAddress != "127.0.0.1" || address.LocalPort is < 1 or > 65535 ||
                        !client.OutboundMappings.Any(mapping => mapping.Enabled && mapping.MappingId == address.MappingId)) ||
                    status.Addresses.Select(address => address.MappingId).Distinct(StringComparer.Ordinal).Count() != status.Addresses.Count ||
                    status.Addresses.Select(address => address.LocalPort).Distinct().Count() != status.Addresses.Count)
                    throw new ProtocolException("Invalid peer mapping status.");
                session.SetPeerAddresses(status.Addresses);
                continue;
            }

            if (frame.Type == FrameType.PeerOpenRequest)
            {
                await peerRelays.RequestAsync(session, JsonProtocolSerializer.Deserialize<PeerOpenRequestMessage>(frame.Payload.Span), cancellationToken);
                continue;
            }

            throw new ProtocolException($"Control session received invalid frame {frame.Type}.");
        }
    }

    private static async Task TryWriteErrorAsync(FrameWriter writer, ErrorCode code, CancellationToken cancellationToken)
    {
        try { await writer.WriteAsync(new Frame(FrameType.Error, JsonProtocolSerializer.Serialize(new ErrorMessage(code))), cancellationToken); }
        catch (IOException) { }
    }

    private async Task<Stream> CreateTransportStreamAsync(TcpClient client, CancellationToken cancellationToken)
    {
        if (!runtime.Configuration.Server.Tunnel.TlsEnabled) return client.GetStream();
        var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate!, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ClientCertificateRequired = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, cancellationToken);
        return stream;
    }

    private static X509Certificate2 LoadServerCertificate(string certificatePath, string privateKeyPath)
    {
        // Deployment remains PEM on every platform. Schannel cannot use the ephemeral
        // private key produced by CreateFromPemFile, so import an in-memory PFX into
        // the current user's key provider; no certificate-store installation is needed.
        var certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
        if (!OperatingSystem.IsWindows()) return certificate;
        using (certificate)
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }
}
