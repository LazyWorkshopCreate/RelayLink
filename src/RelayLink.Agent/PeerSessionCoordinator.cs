using System.Collections.Concurrent;
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
    CancellationToken sessionToken) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PeerOpenGrantedMessage>> requests = new();
    private readonly Dictionary<string, LocalBinding> listeners = new(StringComparer.Ordinal);
    private readonly AgentPortAllocator ports = new(agent.PortStatePath, agent.OutboundPortRangeStart, agent.OutboundPortRangeEnd);
    private readonly ConcurrentDictionary<long, Task> active = new();
    private readonly object quotaLock = new();
    private readonly Dictionary<string, int> targetCounts = new(StringComparer.Ordinal);
    private int openConnections;
    private ClientConfigSnapshot snapshot = null!;
    private long taskId;
    public IReadOnlyList<PeerMappingAddress> Addresses => listeners.Values
        .Select(binding => new PeerMappingAddress(binding.Mapping.MappingId, "127.0.0.1", binding.Port)).ToArray();

    public void ApplyConfiguration(ClientConfigSnapshot configuration)
    {
        Volatile.Write(ref snapshot, configuration);
        var wanted = configuration.OutboundMappings.Where(mapping => mapping.Enabled).ToDictionary(mapping => mapping.MappingId, StringComparer.Ordinal);
        foreach (var existing in listeners.Values.ToArray())
        {
            if (wanted.TryGetValue(existing.Mapping.MappingId, out var updated) && updated == existing.Mapping) continue;
            existing.Stop();
            listeners.Remove(existing.Mapping.MappingId);
        }
        foreach (var mapping in wanted.Values)
        {
            if (mapping.LocalAddress != "127.0.0.1" || Convert.FromBase64String(mapping.AccessSecret).Length < 32)
                throw new AgentPermanentException("Server sent an invalid outbound mapping.");
            if (listeners.ContainsKey(mapping.MappingId)) continue;
            var binding = new LocalBinding(mapping, ports.Bind(mapping.MappingId));
            listeners.Add(mapping.MappingId, binding);
            Track(AcceptLoopAsync(binding));
            logger.LogInformation("Peer loopback listener {MappingId} started on 127.0.0.1:{Port}.", mapping.MappingId, binding.Port);
        }
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
        lock (quotaLock)
        {
            if (openConnections >= current.MaxConnections || targetCounts.GetValueOrDefault(channel.ChannelId) >= channel.MaxConnections) return;
            openConnections++;
            targetCounts[channel.ChannelId] = targetCounts.GetValueOrDefault(channel.ChannelId) + 1;
        }
        Track(HandleTargetWithLeaseAsync(open, channel.ChannelId));
    }

    private async Task HandleTargetWithLeaseAsync(PeerOpenMessage open, string channelId)
    {
        try { await HandleTargetAsync(open); }
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
                var caller = await binding.Listener.AcceptSocketAsync(sessionToken);
                lock (quotaLock)
                {
                    if (openConnections >= Volatile.Read(ref snapshot).MaxConnections) { caller.Dispose(); continue; }
                    openConnections++;
                }
                Track(HandleCallerWithLeaseAsync(caller, binding.Mapping));
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (binding.Stopped) { }
        catch (SocketException) when (binding.Stopped) { }
    }

    private async Task HandleCallerWithLeaseAsync(Socket caller, OutboundMappingSnapshot mapping)
    {
        try { await HandleCallerAsync(caller, mapping); }
        finally { lock (quotaLock) openConnections--; }
    }

    private async Task HandleCallerAsync(Socket caller, OutboundMappingSnapshot mapping)
    {
        using (caller)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var requestId = Guid.NewGuid();
            var reply = new TaskCompletionSource<PeerOpenGrantedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests[requestId] = reply;
            try
            {
                await controlWriter.WriteAsync(new Frame(FrameType.PeerOpenRequest, JsonProtocolSerializer.Serialize(new PeerOpenRequestMessage(requestId, mapping.MappingId))), deadline.Token);
                var grant = await reply.Task.WaitAsync(deadline.Token);
                await using var connection = await ConnectDataAsync(grant.ConnectionId, grant.SessionId, grant.Token, "caller", deadline.Token);
                using var tls = new SslStream(connection.Framed, leaveInnerStreamOpen: true);
                var expected = Convert.FromHexString(mapping.TargetCertificateSha256);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = mapping.TargetClientId,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => MatchesPinnedCertificate(certificate, expected)
                }, deadline.Token);
                if (!tls.IsEncrypted) throw new AuthenticationException("Peer TLS is not encrypted.");
                var challenge = new byte[32];
                await tls.ReadExactlyAsync(challenge, deadline.Token);
                var proof = ComputeProof(mapping.AccessSecret, agent.ClientId, mapping.TargetChannelId, grant.ConnectionId, challenge);
                await tls.WriteAsync(proof, deadline.Token);
                var status = new byte[1];
                await tls.ReadExactlyAsync(status, deadline.Token);
                if (status[0] != 1) throw new IOException("Peer target was not ready.");
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                await RelaySocketAsync(caller, tls, connection.Framed, sessionToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or AuthenticationException or ProtocolException)
            {
                logger.LogWarning(exception, "Peer caller connection for {MappingId} ended.", mapping.MappingId);
            }
            finally { requests.TryRemove(requestId, out _); }
        }
    }

    private async Task HandleTargetAsync(PeerOpenMessage open)
    {
        var current = Volatile.Read(ref snapshot);
        var channel = current.Channels.SingleOrDefault(candidate => candidate.ChannelId == open.ChannelId && candidate.Enabled && candidate.AuthorizedClientsOnly);
        if (channel is null || string.IsNullOrWhiteSpace(channel.AccessSecret)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using var connection = await ConnectDataAsync(open.ConnectionId, open.SessionId, open.Token, "target", deadline.Token);
            using var tls = new SslStream(connection.Framed, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, deadline.Token);
            if (!tls.IsEncrypted) throw new AuthenticationException("Peer TLS is not encrypted.");
            var challenge = RandomNumberGenerator.GetBytes(32);
            await tls.WriteAsync(challenge, deadline.Token);
            var provided = new byte[32];
            await tls.ReadExactlyAsync(provided, deadline.Token);
            var expected = ComputeProof(channel.AccessSecret, open.CallerClientId, channel.ChannelId, open.ConnectionId, challenge);
            if (!CryptographicOperations.FixedTimeEquals(provided, expected)) throw new AuthenticationException("Peer access proof was invalid.");

            using var target = new TcpClient();
            using var targetDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            targetDeadline.CancelAfter(TimeSpan.FromSeconds(channel.TargetConnectTimeoutSeconds));
            await target.ConnectAsync(channel.TargetHost, channel.TargetPort, targetDeadline.Token);
            await tls.WriteAsync(new byte[] { 1 }, deadline.Token);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            await RelaySocketAsync(target.Client, tls, connection.Framed, sessionToken);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or AuthenticationException or ProtocolException)
        {
            logger.LogWarning(exception, "Peer target connection for {ChannelId} ended.", open.ChannelId);
        }
    }

    private async Task<PeerDataConnection> ConnectDataAsync(Guid connectionId, Guid ownSessionId, string token, string role, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(agent.ServerHost, agent.EffectiveDataPort, cancellationToken);
            client.NoDelay = true;
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

    private static byte[] ComputeProof(string base64Secret, string callerId, string channelId, Guid connectionId, byte[] challenge)
    {
        var context = Encoding.UTF8.GetBytes($"RelayLink peer proof v1\0{callerId}\0{channelId}\0{connectionId:N}\0");
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

    private static async Task RelaySocketAsync(Socket socket, SslStream tls, FramedDuplexStream framed, CancellationToken cancellationToken)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toPeer = Task.Run(async () =>
        {
            var payload = new byte[ProtocolConstants.MaxDataPayloadLength];
            var header = new byte[4];
            while (true)
            {
                var count = await socket.ReceiveAsync(payload, SocketFlags.None, relayCancellation.Token);
                BinaryPrimitives.WriteInt32BigEndian(header, count);
                await tls.WriteAsync(header, relayCancellation.Token);
                if (count == 0) return;
                await tls.WriteAsync(payload.AsMemory(0, count), relayCancellation.Token);
            }
        }, relayCancellation.Token);
        var fromPeer = Task.Run(async () =>
        {
            var payload = new byte[ProtocolConstants.MaxDataPayloadLength];
            var header = new byte[4];
            while (true)
            {
                await tls.ReadExactlyAsync(header, relayCancellation.Token);
                var count = BinaryPrimitives.ReadInt32BigEndian(header);
                if (count == 0) { socket.Shutdown(SocketShutdown.Send); return; }
                if (count < 0 || count > payload.Length) throw new ProtocolException("Invalid peer application frame length.");
                await tls.ReadExactlyAsync(payload.AsMemory(0, count), relayCancellation.Token);
                var pending = payload.AsMemory(0, count);
                while (!pending.IsEmpty)
                {
                    var sent = await socket.SendAsync(pending, SocketFlags.None, relayCancellation.Token);
                    if (sent == 0) throw new IOException("Local socket closed during peer relay.");
                    pending = pending[sent..];
                }
            }
        }, relayCancellation.Token);
        await RelayPump.CompleteBidirectionalAsync(toPeer, fromPeer, () => { relayCancellation.Cancel(); socket.Dispose(); });
        await framed.CompleteWritesAsync(relayCancellation.Token);
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
        if (tasks.Length > 0) await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(5)));
    }

    private sealed class LocalBinding(OutboundMappingSnapshot mapping, TcpListener listener)
    {
        public OutboundMappingSnapshot Mapping { get; } = mapping;
        public TcpListener Listener { get; } = listener;
        public int Port => ((IPEndPoint)Listener.LocalEndpoint).Port;
        public bool Stopped { get; private set; }
        public void Stop() { Stopped = true; Listener.Stop(); }
    }

    private sealed class PeerDataConnection(TcpClient client, Stream transport, FramedDuplexStream framed) : IAsyncDisposable
    {
        public FramedDuplexStream Framed { get; } = framed;
        public async ValueTask DisposeAsync() { await transport.DisposeAsync(); client.Dispose(); }
    }
}
