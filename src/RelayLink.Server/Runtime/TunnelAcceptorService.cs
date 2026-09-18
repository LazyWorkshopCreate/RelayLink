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
    PendingConnectionRegistry pendingConnections,
    PeerRelayRegistry peerRelays,
    MetricsRegistry metrics,
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
                if (first?.Type == FrameType.BindData)
                {
                    var bind = JsonProtocolSerializer.Deserialize<BindDataMessage>(first.Payload.Span);
                    if (!pendingConnections.TryBind(bind.SessionId, bind.ConnectionId, bind.ChannelId, bind.Token, out var pending) || pending is null || !pending.TrySetTunnel(new DataTunnel(stream, reader, writer)))
                    {
                        await TryWriteErrorAsync(writer, ErrorCode.TokenInvalid, handshakeTimeout.Token);
                        return;
                    }

                    await writer.WriteAsync(new Frame(FrameType.BindAccepted, JsonProtocolSerializer.Serialize(new BindAcceptedMessage(bind.ConnectionId))), handshakeTimeout.Token);
                    // A bound data tunnel is authenticated by its single-use token. It must
                    // no longer consume the short-lived unauthenticated handshake budget
                    // for the duration of a potentially long relay.
                    unauthenticatedLimit.Release();
                    slotReleased = true;
                    await pending.WaitForCompletionAsync();
                    return;
                }

                if (first?.Type == FrameType.PeerBindData)
                {
                    var bind = JsonProtocolSerializer.Deserialize<PeerBindDataMessage>(first.Payload.Span);
                    if (!peerRelays.TryBind(bind, new DataTunnel(stream, reader, writer), out var relay) || relay is null)
                    {
                        await TryWriteErrorAsync(writer, ErrorCode.TokenInvalid, handshakeTimeout.Token);
                        return;
                    }
                    unauthenticatedLimit.Release();
                    slotReleased = true;
                    await relay.WaitForCompletionAsync();
                    return;
                }

                if (first?.Type != FrameType.Register)
                {
                    await TryWriteErrorAsync(writer, ErrorCode.ProtocolError, handshakeTimeout.Token);
                    return;
                }

                var register = JsonProtocolSerializer.Deserialize<RegisterMessage>(first.Payload.Span);
                if (!authentication.TryAuthenticate(register.ClientId, register.Secret, out var clientConfiguration))
                {
                    await TryWriteErrorAsync(writer, ErrorCode.AuthFailed, handshakeTimeout.Token);
                    logger.LogWarning("Authentication failed for client {ClientId}.", register.ClientId);
                    return;
                }

                if (clientConfiguration!.Channels.Any(channel => channel.Enabled && channel.AuthorizedClientsOnly && !string.Equals(channel.E2eCertificateSha256, register.E2eCertificateSha256, StringComparison.OrdinalIgnoreCase)))
                {
                    await TryWriteErrorAsync(writer, ErrorCode.AuthFailed, handshakeTimeout.Token);
                    logger.LogWarning("E2E certificate identity mismatch for client {ClientId}.", register.ClientId);
                    return;
                }

                if (!runtime.Sessions.TryRegister(clientConfiguration!, out var session))
                {
                    await TryWriteErrorAsync(writer, ErrorCode.DuplicateSession, handshakeTimeout.Token);
                    logger.LogWarning("Rejected duplicate session for client {ClientId}.", register.ClientId);
                    return;
                }

                unauthenticatedLimit.Release();
                slotReleased = true;
                try
                {
                    using var sessionScope = logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["clientId"] = session.ClientId,
                        ["sessionId"] = session.SessionId
                    });
                    session.AgentVersion = register.AgentVersion;
                    session.E2eCertificateSha256 = register.E2eCertificateSha256;
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

                    session.SetConfigVersion(configVersion);

                    await writer.WriteAsync(new Frame(FrameType.Ready, JsonProtocolSerializer.Serialize(new ReadyMessage(session.SessionId))), serverStoppingToken);
                    logger.LogInformation("Client {ClientId} session {SessionId} is online.", session.ClientId, session.SessionId);
                    await RunControlSessionAsync(reader, writer, session, serverStoppingToken);
                }
                finally
                {
                    peerRelays.CancelSession(session.SessionId);
                    runtime.Sessions.Remove(session.ClientId, session.SessionId);
                    metrics.ResetTargets(session.ClientId);
                    logger.LogInformation("Client {ClientId} session {SessionId} is offline.", session.ClientId, session.SessionId);
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
    }

    private async Task RunControlSessionAsync(FrameReader reader, FrameWriter writer, Session session, CancellationToken stoppingToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
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
