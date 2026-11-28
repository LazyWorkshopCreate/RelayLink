using System.Collections.Concurrent;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.Agent;

internal sealed class PeerSessionCoordinator(
    AgentConfiguration agent,
    AgentIdentity identity,
    Guid sessionId,
    FrameWriter controlWriter,
    ILogger logger,
    AgentConnectionQuota quota,
    CancellationToken sessionToken,
    HttpProxyRuntime? httpProxy = null,
    TimeSpan? halfCloseDrainTimeout = null,
    TimeSpan? tcpIdleTimeout = null,
    bool relayControlReady = false) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PeerOpenGrantedMessage>> requests = new();
    private readonly ConcurrentDictionary<Guid, PeerRelayConfirmation> relayConfirmations = new();
    private readonly Dictionary<string, LocalBinding> listeners = new(StringComparer.Ordinal);
    private readonly AgentPortAllocator ports = new(agent.PortStatePath, agent.OutboundPortRangeStart, agent.OutboundPortRangeEnd);
    private readonly ConcurrentDictionary<long, Task> active = new();
    private readonly object quotaLock = new();
    private readonly Dictionary<string, int> targetCounts = new(StringComparer.Ordinal);
    private int openConnections;
    private ClientConfigSnapshot snapshot = null!;
    private long taskId;
    private long udpDropped;
    private long udpDropLogAt;
    internal static TimeSpan PeerSetupDeadline(int deliveredMilliseconds, int localTimeoutSeconds)
    {
        var delivered = deliveredMilliseconds > 0 ? TimeSpan.FromMilliseconds(deliveredMilliseconds) : TimeSpan.Zero;
        var local = localTimeoutSeconds > 0 ? TimeSpan.FromSeconds(localTimeoutSeconds) : TimeSpan.Zero;
        var chosen = delivered > local ? delivered : local;
        return chosen > TimeSpan.Zero ? chosen : TimeSpan.FromSeconds(20);
    }

    public IReadOnlyList<PeerMappingAddress> Addresses => listeners.Values
        .Select(binding => new PeerMappingAddress(binding.Mapping.MappingId, "127.0.0.1", binding.Port)).ToArray();

    internal static string? InvalidOutboundMappingReason(IReadOnlyList<OutboundMappingSnapshot> mappings)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.MappingId) || !ids.Add(mapping.MappingId))
                return "mapping id is empty or duplicated";
            if (mapping.LocalAddress != "127.0.0.1")
                return "local address must be 127.0.0.1";
            try
            {
                if (Convert.FromBase64String(mapping.AccessSecret).Length < 32)
                    return "access secret must contain at least 32 bytes";
            }
            catch (FormatException)
            {
                return "access secret is not valid Base64";
            }

            if (mapping.TransportProtocol is not ("tcp" or "udp"))
                return "transport protocol must be tcp or udp";
            if (mapping.ApplicationProtocol is not ("raw" or "http"))
                return "application protocol must be raw or http";
            if (mapping.ApplicationProtocol == "http")
            {
                if (mapping.TransportProtocol != "tcp" || mapping.Http is null)
                    return "http mappings require tcp and http options";
                if (mapping.Http.Scheme is not ("http" or "https"))
                    return "http mapping scheme must be http or https";
                var authorityHost = mapping.Http.AuthorityHost;
                if (string.IsNullOrWhiteSpace(authorityHost) ||
                    authorityHost.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
                    Uri.CheckHostName(authorityHost) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                    return "http mapping authority host is invalid";
                try
                {
                    _ = HttpProxyPolicy.NormalizeAuthority(authorityHost, mapping.Http.Scheme);
                }
                catch (ArgumentException)
                {
                    return "http mapping authority host is invalid";
                }
                if (mapping.Http.PolicyHash is not { Length: 64 } || !mapping.Http.PolicyHash.All(Uri.IsHexDigit))
                    return "http mapping policy hash is invalid";
            }
            else if (mapping.Http is not null)
            {
                return "raw mappings cannot include http options";
            }
        }

        return null;
    }

    public void ApplyConfiguration(ClientConfigSnapshot configuration)
    {
        var reason = InvalidOutboundMappingReason(configuration.OutboundMappings);
        if (reason is not null)
        {
            logger.LogWarning("Rejected outbound mapping snapshot for profile {ProfileId}: {Reason}. Existing listeners were left unchanged.", agent.ProfileId, reason);
            throw new AgentPermanentException($"Server sent an invalid outbound mapping: {reason}.");
        }

        Volatile.Write(ref snapshot, configuration);
        foreach (var confirmation in relayConfirmations.Values) confirmation.CheckConfiguration(configuration);
        var wanted = configuration.OutboundMappings.Where(mapping => mapping.Enabled).ToDictionary(mapping => mapping.MappingId, StringComparer.Ordinal);
        foreach (var existing in listeners.Values.ToArray())
        {
            if (wanted.TryGetValue(existing.Mapping.MappingId, out var updated) && updated == existing.Mapping) continue;
            existing.Stop();
            listeners.Remove(existing.Mapping.MappingId);
        }
        foreach (var mapping in wanted.Values)
        {
            if (listeners.ContainsKey(mapping.MappingId)) continue;
            try
            {
                var binding = mapping.TransportProtocol == "udp"
                    ? new LocalBinding(mapping, null, new DatagramListener(ports.BindUdp(mapping.MappingId)))
                    : new LocalBinding(mapping, ports.Bind(mapping.MappingId), null);
                listeners.Add(mapping.MappingId, binding);
                Track(binding.Udp is { } udp
                    ? udp.RunAsync(configuration.MaxConnections, _ => true, datagram => HandleDatagramCallerAsync(datagram, mapping), sessionToken,
                        exception => logger.LogError(exception, "UDP peer handler failed for mapping {MappingId}.", mapping.MappingId),
                        NoteUdpDrop)
                    : AcceptLoopAsync(binding));
                logger.LogInformation("Peer loopback listener {MappingId} started on 127.0.0.1:{Port}.", mapping.MappingId, binding.Port);
            }
            catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Peer loopback listener {MappingId} could not bind; other mappings continue.", mapping.MappingId);
            }
        }
    }

    public void ReceiveRelaySignal(FrameType type, PeerRelaySignal signal)
    {
        if (!relayControlReady || signal.Binding is null) throw new ProtocolException("Unexpected relay control signal.");
        if (relayConfirmations.TryGetValue(signal.Binding.ConnectionId, out var confirmation)) confirmation.Receive(type, signal);
    }

    private PeerRelayConfirmation? BeginRelaySetup(PeerRelayBinding? binding, bool target, Guid connectionId,
        string callerClient, string targetClient, string? mappingId, string channelId, string transport, bool encrypted,
        string application, CancellationTokenSource deadline, Func<ClientConfigSnapshot, bool> authorized)
    {
        if (binding is null) return null;
        if (!relayControlReady || application != "raw" || binding.ConnectionId != connectionId || binding.AttemptId == Guid.Empty ||
            binding.CallerSessionId == Guid.Empty || binding.TargetSessionId == Guid.Empty || binding.CallerSessionId == binding.TargetSessionId ||
            (target ? binding.TargetSessionId : binding.CallerSessionId) != sessionId || binding.CallerClientId != callerClient || binding.TargetClientId != targetClient ||
            string.IsNullOrWhiteSpace(binding.MappingId) || (mappingId is not null && binding.MappingId != mappingId) ||
            binding.ChannelId != channelId || binding.TransportProtocol != transport || binding.EndToEndEncryptionEnabled != encrypted)
            throw new ProtocolException("Relay binding does not match the authorized peer.");
        var confirmation = new PeerRelayConfirmation(binding, target, controlWriter, deadline, sessionToken, authorized,
            () => relayConfirmations.TryRemove(connectionId, out _));
        if (!relayConfirmations.TryAdd(connectionId, confirmation)) throw new ProtocolException("Duplicate relay attempt.");
        confirmation.CheckConfiguration(Volatile.Read(ref snapshot));
        return confirmation;
    }

    public void Grant(PeerOpenGrantedMessage response)
    {
        if (response.SessionId != sessionId) throw new ProtocolException("Peer grant has wrong session ID.");
        if (requests.TryRemove(response.RequestId, out var pending)) pending.TrySetResult(response);
    }

    public void Reject(PeerOpenRejectedMessage response)
    {
        if (requests.TryRemove(response.RequestId, out var pending)) pending.TrySetException(new IOException($"Peer request was rejected: {response.ErrorCode}."));
    }

    public void Open(PeerOpenMessage open)
    {
        if (open.SessionId != sessionId) throw new ProtocolException("Peer open has wrong session ID.");
        var current = Volatile.Read(ref snapshot);
        var channel = current.Channels.SingleOrDefault(candidate => candidate.ChannelId == open.ChannelId && candidate.Enabled && candidate.AuthorizedClientsOnly);
        if (channel is null) return;
        var lease = quota.TryAcquirePending();
        if (lease is null)
        {
            logger.LogWarning("Peer target request for {ChannelId} exceeded profile {ProfileId} capacity.", open.ChannelId, agent.ProfileId);
            return;
        }
        lock (quotaLock)
        {
            if (openConnections >= current.MaxConnections || targetCounts.GetValueOrDefault(channel.ChannelId) >= channel.MaxConnections)
            {
                lease.Dispose();
                return;
            }
            openConnections++;
            targetCounts[channel.ChannelId] = targetCounts.GetValueOrDefault(channel.ChannelId) + 1;
        }
        Track(HandleTargetWithLeaseAsync(open, channel.ChannelId, lease));
    }

    private async Task HandleTargetWithLeaseAsync(PeerOpenMessage open, string channelId, AgentConnectionQuota.Lease lease)
    {
        using (lease)
        try { await HandleTargetAsync(open, lease); }
        finally
        {
            lock (quotaLock) { openConnections--; targetCounts[channelId]--; }
        }
    }

    private async Task AcceptLoopAsync(LocalBinding binding)
    {
        try
        {
            while (!sessionToken.IsCancellationRequested && !binding.Stopped)
            {
                var caller = await binding.Listener!.AcceptSocketAsync(sessionToken);
                caller.NoDelay = true;
                var lease = quota.TryAcquirePending();
                if (lease is null)
                {
                    caller.Dispose();
                    logger.LogWarning("Peer caller request for {MappingId} exceeded profile {ProfileId} capacity.", binding.Mapping.MappingId, agent.ProfileId);
                    continue;
                }
                lock (quotaLock)
                {
                    if (openConnections >= Volatile.Read(ref snapshot).MaxConnections) { lease.Dispose(); caller.Dispose(); continue; }
                    openConnections++;
                }
                Track(HandleCallerWithLeaseAsync(caller, binding.Mapping, lease));
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (binding.Stopped) { }
        catch (SocketException) when (binding.Stopped) { }
    }

    private async Task HandleDatagramCallerAsync(DatagramSession datagram, OutboundMappingSnapshot mapping)
    {
        using var lease = quota.TryAcquirePending();
        if (lease is null) return;
        lock (quotaLock)
        {
            if (openConnections >= Volatile.Read(ref snapshot).MaxConnections) return;
            openConnections++;
        }
        try { await HandleCallerAsync(null, mapping, lease, datagram); }
        finally { lock (quotaLock) openConnections--; }
    }

    private async Task HandleCallerWithLeaseAsync(Socket caller, OutboundMappingSnapshot mapping, AgentConnectionQuota.Lease lease)
    {
        using (lease)
        try { await HandleCallerAsync(caller, mapping, lease); }
        finally { lock (quotaLock) openConnections--; }
    }

    private async Task HandleCallerAsync(Socket? caller, OutboundMappingSnapshot mapping, AgentConnectionQuota.Lease lease, DatagramSession? datagram = null)
    {
        using (caller)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, datagram?.Token ?? CancellationToken.None))
        {
            deadline.CancelAfter(PeerSetupDeadline(0, 0));
            var requestId = Guid.NewGuid();
            var reply = new TaskCompletionSource<PeerOpenGrantedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests[requestId] = reply;
            try
            {
                await controlWriter.WriteControlAsync(new Frame(FrameType.PeerOpenRequest, JsonProtocolSerializer.Serialize(new PeerOpenRequestMessage(requestId, mapping.MappingId))), deadline.Token, sessionToken);
                var grant = await reply.Task.WaitAsync(deadline.Token);
                deadline.CancelAfter(PeerSetupDeadline(grant.SetupTimeoutMilliseconds, 0));
                if (mapping.EndToEndEncryptionEnabled is { } encrypted && encrypted != grant.EndToEndEncryptionEnabled)
                    throw new ProtocolException("Peer encryption mode changed; wait for updated configuration.");
                if (grant.TransportProtocol != mapping.TransportProtocol) throw new ProtocolException("Peer transport changed; wait for updated configuration.");
                await using var confirmation = BeginRelaySetup(grant.RelayBinding, false, grant.ConnectionId, agent.ClientId, mapping.TargetClientId,
                    mapping.MappingId, mapping.TargetChannelId, mapping.TransportProtocol, grant.EndToEndEncryptionEnabled, mapping.ApplicationProtocol, deadline,
                    config => config.OutboundMappings.Contains(mapping));
                await using var connection = await ConnectDataAsync(grant.ConnectionId, grant.SessionId, grant.Token, "caller", deadline.Token);
                using var tls = grant.EndToEndEncryptionEnabled ? new SslStream(connection.Framed, leaveInnerStreamOpen: true) : null;
                Stream peerStream = connection.Framed;
                if (tls is not null)
                {
                    var expected = Convert.FromHexString(mapping.TargetCertificateSha256);
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = mapping.TargetClientId,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = (_, certificate, _, _) => MatchesPinnedCertificate(certificate, expected)
                    }, deadline.Token);
                    if (!tls.IsEncrypted) throw new AuthenticationException("Peer TLS is not encrypted.");
                    peerStream = tls;
                }
                if (mapping.ApplicationProtocol == "http" && (mapping.Http is null || grant.HttpIngress is null || !grant.EndToEndEncryptionEnabled ||
                    grant.HttpIngress.AccessKind != "peer" || grant.HttpIngress.CallerClientId != agent.ClientId || grant.HttpIngress.MappingId != mapping.MappingId ||
                    grant.HttpIngress.Scheme != mapping.Http.Scheme || grant.HttpIngress.PolicyHash != mapping.Http.PolicyHash ||
                    grant.HttpIngress.EntryAuthority != $"{mapping.Http.AuthorityHost}:{((IPEndPoint)caller!.LocalEndPoint!).Port}"))
                    throw new ProtocolException("HTTP mapping context mismatch.");
                if (mapping.ApplicationProtocol != "http" && grant.HttpIngress is not null) throw new ProtocolException("Raw mapping received HTTP context.");
                if (confirmation is not null)
                    await confirmation.AuthenticateAsync(peerStream, mapping.AccessSecret);
                else
                {
                    var challenge = new byte[32];
                    await peerStream.ReadExactlyAsync(challenge, deadline.Token);
                    var proof = ComputeProof(mapping.AccessSecret, agent.ClientId, mapping.TargetChannelId, grant.ConnectionId, grant.EndToEndEncryptionEnabled, challenge, grant.HttpIngress);
                    await peerStream.WriteAsync(proof, deadline.Token);
                    var status = new byte[1];
                    await peerStream.ReadExactlyAsync(status, deadline.Token);
                    if (status[0] != (mapping.ApplicationProtocol == "http" ? 2 : 1)) throw new IOException("Peer target was not ready.");
                }
                lease.MarkActive();
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                if (datagram is not null) await datagram.RelayAsync(peerStream, mapping.UdpIdleTimeoutSeconds, deadline.Token);
                else if (tls is not null) await RelayEncryptedSocketAsync(caller!, tls, connection.Framed, deadline.Token, halfCloseDrainTimeout ?? TimeSpan.FromSeconds(300), tcpIdleTimeout ?? TimeSpan.Zero, connection.Abort);
                else await RelayPlainSocketAsync(caller!, connection.Framed, deadline.Token, halfCloseDrainTimeout ?? TimeSpan.FromSeconds(300), tcpIdleTimeout ?? TimeSpan.Zero, connection.Abort);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or AuthenticationException or ProtocolException)
            {
                logger.LogWarning(exception, "Peer caller connection for {MappingId} ended.", mapping.MappingId);
            }
            finally { requests.TryRemove(requestId, out _); }
        }
    }

    private async Task HandleTargetAsync(PeerOpenMessage open, AgentConnectionQuota.Lease lease)
    {
        var current = Volatile.Read(ref snapshot);
        var channel = current.Channels.SingleOrDefault(candidate => candidate.ChannelId == open.ChannelId && candidate.Enabled && candidate.AuthorizedClientsOnly);
        if (channel is null || string.IsNullOrWhiteSpace(channel.AccessSecret) || channel.EndToEndEncryptionEnabled != open.EndToEndEncryptionEnabled || channel.TransportProtocol != open.TransportProtocol) return;
        if (channel.ApplicationProtocol == "http" && (!agent.UseTls || !open.EndToEndEncryptionEnabled || httpProxy is null || !httpProxy.IsReady(channel) ||
            open.HttpIngress is null || open.HttpIngress.AccessKind != "peer" || open.HttpIngress.CallerClientId != open.CallerClientId ||
            open.HttpIngress.PolicyHash != HttpProxyPolicy.Hash(channel) || open.HttpIngress.Scheme != channel.Http!.PeerEntryScheme)) return;
        if (channel.ApplicationProtocol != "http" && open.HttpIngress is not null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        deadline.CancelAfter(PeerSetupDeadline(open.SetupTimeoutMilliseconds, channel.TargetConnectTimeoutSeconds));
        try
        {
            await using var confirmation = BeginRelaySetup(open.RelayBinding, true, open.ConnectionId, open.CallerClientId, agent.ClientId,
                null, channel.ChannelId, channel.TransportProtocol, channel.EndToEndEncryptionEnabled, channel.ApplicationProtocol, deadline,
                config => config.Channels.Contains(channel));
            await using var connection = await ConnectDataAsync(open.ConnectionId, open.SessionId, open.Token, "target", deadline.Token);
            using var tls = open.EndToEndEncryptionEnabled ? new SslStream(connection.Framed, leaveInnerStreamOpen: true) : null;
            Stream peerStream = connection.Framed;
            if (tls is not null)
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                }, deadline.Token);
                if (!tls.IsEncrypted) throw new AuthenticationException("Peer TLS is not encrypted.");
                peerStream = tls;
            }
            if (confirmation is not null)
                await confirmation.AuthenticateAsync(peerStream, channel.AccessSecret);
            else
            {
                var challenge = RandomNumberGenerator.GetBytes(32);
                await peerStream.WriteAsync(challenge, deadline.Token);
                var provided = new byte[32];
                await peerStream.ReadExactlyAsync(provided, deadline.Token);
                var expected = ComputeProof(channel.AccessSecret, open.CallerClientId, channel.ChannelId, open.ConnectionId, open.EndToEndEncryptionEnabled, challenge, open.HttpIngress);
                if (!CryptographicOperations.FixedTimeEquals(provided, expected)) throw new AuthenticationException("Peer access proof was invalid.");
            }

            if (channel.ApplicationProtocol == "http")
            {
                await peerStream.WriteAsync(new byte[] { 2 }, deadline.Token);
                lease.MarkActive(); deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                await using var application = new PeerApplicationStream(peerStream);
                await httpProxy!.ServeAsync(channel, application, open.HttpIngress!, sessionToken, open.ConnectionId);
                await connection.Framed.CompleteWritesAsync(sessionToken);
                return;
            }
            if (channel.TransportProtocol == "udp")
            {
                using var targetTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                targetTimeout.CancelAfter(TimeSpan.FromSeconds(channel.TargetConnectTimeoutSeconds));
                using var udp = await DatagramRelay.ConnectTargetAsync(channel.TargetHost, channel.TargetPort, targetTimeout.Token);
                if (confirmation is not null) await confirmation.TargetReadyAsync();
                else await peerStream.WriteAsync(new byte[] { 1 }, deadline.Token);
                lease.MarkActive();
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                await DatagramRelay.RunTargetAsync(peerStream, udp, channel.UdpIdleTimeoutSeconds, deadline.Token);
                return;
            }
            using var target = new TcpClient();
            using var targetDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            targetDeadline.CancelAfter(TimeSpan.FromSeconds(channel.TargetConnectTimeoutSeconds));
            await target.ConnectAsync(channel.TargetHost, channel.TargetPort, targetDeadline.Token);
            target.NoDelay = true;
            if (confirmation is not null) await confirmation.TargetReadyAsync();
            else await peerStream.WriteAsync(new byte[] { 1 }, deadline.Token);
            lease.MarkActive();
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            if (tls is not null) await RelayEncryptedSocketAsync(target.Client, tls, connection.Framed, deadline.Token, halfCloseDrainTimeout ?? TimeSpan.FromSeconds(300), tcpIdleTimeout ?? TimeSpan.Zero, connection.Abort);
            else await RelayPlainSocketAsync(target.Client, connection.Framed, deadline.Token, halfCloseDrainTimeout ?? TimeSpan.FromSeconds(300), tcpIdleTimeout ?? TimeSpan.Zero, connection.Abort);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or AuthenticationException or ProtocolException)
        {
            logger.LogWarning(exception, "Peer target connection for {ChannelId} ended.", open.ChannelId);
        }
    }

    internal async Task<PeerDataConnection> ConnectDataAsync(Guid connectionId, Guid ownSessionId, string token, string role, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(agent.ServerHost, agent.EffectiveDataPort, cancellationToken);
            client.NoDelay = true;
            TcpKeepAlive.Enable(client.Client);
            var transport = client.GetStream();
            var reader = new FrameReader(transport);
            var writer = new FrameWriter(transport);
            await writer.WriteAsync(new Frame(FrameType.PeerBindData, JsonProtocolSerializer.Serialize(new PeerBindDataMessage(connectionId, ownSessionId, token, role))), cancellationToken);
            var bound = await reader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, cancellationToken);
            if (bound?.Type != FrameType.PeerBindAccepted || JsonProtocolSerializer.Deserialize<PeerBindAcceptedMessage>(bound.Payload.Span).ConnectionId != connectionId)
                throw new ProtocolException("Peer data tunnel was not accepted.");
            return new PeerDataConnection(client, transport, new FramedDuplexStream(reader, writer));
        }
        catch { client.Dispose(); throw; }
    }

    internal static byte[] ComputeProof(string base64Secret, string callerId, string channelId, Guid connectionId, bool encryptionEnabled, byte[] challenge, HttpIngressContext? httpIngress = null)
    {
        var context = Encoding.UTF8.GetBytes($"RelayLink peer proof v2\0{callerId}\0{channelId}\0{connectionId:N}\0{(encryptionEnabled ? "tls" : "plain")}\0");
        if (httpIngress is not null) context = Encoding.UTF8.GetBytes("RelayLink HTTP peer proof v1\0" + Encoding.UTF8.GetString(context) + Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(httpIngress)));
        var payload = new byte[context.Length + challenge.Length];
        context.CopyTo(payload, 0);
        challenge.CopyTo(payload, context.Length);
        return HMACSHA256.HashData(Convert.FromBase64String(base64Secret), payload);
    }

    private static bool MatchesPinnedCertificate(System.Security.Cryptography.X509Certificates.X509Certificate? certificate, byte[] expected)
    {
        if (certificate is null) return false;
        using var leaf = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(leaf.RawData), expected) &&
            DateTime.UtcNow >= leaf.NotBefore.ToUniversalTime() && DateTime.UtcNow <= leaf.NotAfter.ToUniversalTime();
    }

    internal static async Task RelayEncryptedSocketAsync(Socket socket, Stream tls, FramedDuplexStream framed, CancellationToken cancellationToken, TimeSpan halfCloseDrainTimeout, TimeSpan tcpIdleTimeout, Action? abortTunnel = null, TimeProvider? timeProvider = null)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idle = new RelayIdleWatch(tcpIdleTimeout, relayCancellation.Token, timeProvider);
        TcpKeepAlive.Enable(socket);
        var toPeer = Task.Run(async () =>
        {
            var frame = new byte[4 + ProtocolConstants.MaxDataPayloadLength];
            while (true)
            {
                var count = await socket.ReceiveAsync(frame.AsMemory(4), SocketFlags.None, idle.Token);
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), count);
                await tls.WriteAsync(frame.AsMemory(0, 4 + count), idle.Token);
                idle.Touch();
                if (count == 0) return;
            }
        }, relayCancellation.Token);
        var fromPeer = Task.Run(async () =>
        {
            var payload = new byte[ProtocolConstants.MaxDataPayloadLength];
            var header = new byte[4];
            while (true)
            {
                await tls.ReadExactlyAsync(header, idle.Token);
                var count = BinaryPrimitives.ReadInt32BigEndian(header);
                if (count == 0) { socket.Shutdown(SocketShutdown.Send); idle.Touch(); return; }
                if (count < 0 || count > payload.Length) throw new ProtocolException("Invalid peer application frame length.");
                await tls.ReadExactlyAsync(payload.AsMemory(0, count), idle.Token);
                var pending = payload.AsMemory(0, count);
                while (!pending.IsEmpty)
                {
                    var sent = await socket.SendAsync(pending, SocketFlags.None, idle.Token);
                    if (sent == 0) throw new IOException("Local socket closed during peer relay.");
                    pending = pending[sent..];
                }
                idle.Touch();
            }
        }, relayCancellation.Token);
        await RelayPump.CompleteBidirectionalAsync(toPeer, fromPeer, () => { relayCancellation.Cancel(); socket.Dispose(); abortTunnel?.Invoke(); }, halfCloseDrainTimeout);
        await framed.CompleteWritesAsync(relayCancellation.Token);
    }

    internal static async Task RelayPlainSocketAsync(Socket socket, FramedDuplexStream framed, CancellationToken cancellationToken, TimeSpan halfCloseDrainTimeout, TimeSpan tcpIdleTimeout, Action? abortTunnel = null, TimeProvider? timeProvider = null)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idle = new RelayIdleWatch(tcpIdleTimeout, relayCancellation.Token, timeProvider);
        TcpKeepAlive.Enable(socket);
        var toPeer = Task.Run(async () =>
        {
            var payload = new byte[ProtocolConstants.MaxDataPayloadLength];
            while (true)
            {
                var count = await socket.ReceiveAsync(payload, SocketFlags.None, idle.Token);
                if (count == 0)
                {
                    await framed.CompleteWritesAsync(idle.Token);
                    idle.Touch();
                    return;
                }
                await framed.WriteAsync(payload.AsMemory(0, count), idle.Token);
                idle.Touch();
            }
        }, relayCancellation.Token);
        var fromPeer = Task.Run(async () =>
        {
            var payload = new byte[ProtocolConstants.MaxDataPayloadLength];
            while (true)
            {
                var count = await framed.ReadAsync(payload, idle.Token);
                if (count == 0)
                {
                    socket.Shutdown(SocketShutdown.Send);
                    idle.Touch();
                    return;
                }
                var pending = payload.AsMemory(0, count);
                while (!pending.IsEmpty)
                {
                    var sent = await socket.SendAsync(pending, SocketFlags.None, idle.Token);
                    if (sent == 0) throw new IOException("Local socket closed during peer relay.");
                    pending = pending[sent..];
                }
                idle.Touch();
            }
        }, relayCancellation.Token);
        await RelayPump.CompleteBidirectionalAsync(toPeer, fromPeer, () => { relayCancellation.Cancel(); socket.Dispose(); abortTunnel?.Invoke(); }, halfCloseDrainTimeout);
    }

    private void NoteUdpDrop()
    {
        var total = Interlocked.Increment(ref udpDropped);
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref udpDropLogAt);
        if (now - last < Stopwatch.Frequency * 30) return;
        if (Interlocked.CompareExchange(ref udpDropLogAt, now, last) != last) return;
        logger.LogWarning("UDP queue dropped the newest datagram; {DroppedTotal} dropped for this profile since it came online.", total);
    }

    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref taskId);
        active[id] = task;
        _ = task.ContinueWith(completed =>
        {
            active.TryRemove(id, out _);
            if (completed.IsFaulted) logger.LogWarning(completed.Exception, "Peer session task failed.");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var listener in listeners.Values) listener.Stop();
        foreach (var request in requests.Values) request.TrySetCanceled();
        var tasks = active.Values.ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        }
    }

    private sealed class LocalBinding(OutboundMappingSnapshot mapping, TcpListener? listener, DatagramListener? udp)
    {
        public OutboundMappingSnapshot Mapping { get; } = mapping;
        public TcpListener? Listener { get; } = listener;
        public DatagramListener? Udp { get; } = udp;
        public int Port => Udp?.Port ?? ((IPEndPoint)Listener!.LocalEndpoint).Port;
        public bool Stopped { get; private set; }
        public void Stop() { Stopped = true; Listener?.Stop(); Udp?.Stop(); }
    }

    internal sealed class PeerDataConnection(TcpClient client, Stream transport, FramedDuplexStream framed) : IAsyncDisposable
    {
        public Socket Socket => client.Client;
        public FramedDuplexStream Framed { get; } = framed;
        public void Abort()
        {
            try { client.Dispose(); }
            catch (ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { await transport.DisposeAsync(); client.Dispose(); }
    }
}
