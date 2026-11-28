using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Agent;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Testing;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class PeerKeepAliveAndIdleTests
{
    [Fact]
    public async Task DataAcceptorService_enables_tcp_keepalive_on_accepted_data_socket()
    {
        using var directory = new TestDirectory("relaylink-data-ka-");
        var runtime = CreateServerRuntime(directory);
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(runtime.Configuration), audit, NullLogger<PeerRelayRegistry>.Instance);
        var service = new DataAcceptorService(runtime, new PendingConnectionRegistry(), registry, NullLogger<DataAcceptorService>.Instance);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
        var accepted = await listener.AcceptTcpClientAsync();
        await connectTask;

        Assert.Equal(0, Convert.ToInt32(accepted.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var handleTask = service.HandleAcceptedAsync(accepted, cts.Token);

        var enabled = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (Convert.ToInt32(accepted.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)) != 0)
            {
                enabled = true;
                break;
            }
            await Task.Delay(10);
        }

        cts.Cancel();
        await handleTask;
        Assert.True(enabled);
    }

    [Fact]
    public async Task PeerRelayRegistry_enables_tcp_keepalive_on_both_bound_peer_sockets()
    {
        using var directory = new TestDirectory("relaylink-peer-ka-");
        var runtime = CreateServerRuntime(directory);
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        await audit.StartAsync(CancellationToken.None);
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(runtime.Configuration), audit, NullLogger<PeerRelayRegistry>.Instance);
        try
        {
            var (caller, callerControl) = ReadySession(runtime, "caller");
            var (target, targetControl) = ReadySession(runtime, "visited");

            await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), CancellationToken.None);
            callerControl.Position = 0;
            targetControl.Position = 0;
            var granted = JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>((await new FrameReader(callerControl).ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None))!.Payload.Span)!;
            var open = JsonProtocolSerializer.Deserialize<PeerOpenMessage>((await new FrameReader(targetControl).ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None))!.Payload.Span)!;

            using var callerPair = await LoopbackSocketPair.CreateAsync();
            using var targetPair = await LoopbackSocketPair.CreateAsync();
            Assert.Equal(0, Convert.ToInt32(callerPair.Server.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)));
            Assert.Equal(0, Convert.ToInt32(targetPair.Server.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)));

            using var callerStream = new NetworkStream(callerPair.Server, ownsSocket: false);
            using var targetStream = new NetworkStream(targetPair.Server, ownsSocket: false);
            Assert.True(registry.TryBind(new PeerBindDataMessage(granted.ConnectionId, caller.SessionId, granted.Token, "caller"),
                new DataTunnel(callerStream, new FrameReader(callerStream), new FrameWriter(callerStream), callerPair.Server), out _));
            Assert.True(registry.TryBind(new PeerBindDataMessage(granted.ConnectionId, target.SessionId, open.Token, "target"),
                new DataTunnel(targetStream, new FrameReader(targetStream), new FrameWriter(targetStream), targetPair.Server), out _));

            var callerEnabled = false;
            var targetEnabled = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                callerEnabled = Convert.ToInt32(callerPair.Server.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)) != 0;
                targetEnabled = Convert.ToInt32(targetPair.Server.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)) != 0;
                if (callerEnabled && targetEnabled) break;
                await Task.Delay(15);
            }

            Assert.True(callerEnabled);
            Assert.True(targetEnabled);

            registry.TryDisconnect("visited", "private", granted.ConnectionId);
            while (registry.ListConnections("visited", "private").Length > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(15);
            registry.CancelSession(caller.SessionId);
        }
        finally
        {
            await audit.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PeerSessionCoordinator_connect_data_enables_tcp_keepalive_and_nodelay()
    {
        using var directory = new TestDirectory("relaylink-agent-connect-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var dataPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connectionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(cts.Token);
            var stream = accepted.GetStream();
            var reader = new FrameReader(stream);
            var writer = new FrameWriter(stream);
            var frame = await reader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, cts.Token);
            Assert.NotNull(frame);
            Assert.Equal(FrameType.PeerBindData, frame!.Type);
            await writer.WriteAsync(new Frame(FrameType.PeerBindAccepted, JsonProtocolSerializer.Serialize(new PeerBindAcceptedMessage(connectionId))), cts.Token);
        }, cts.Token);

        var config = new AgentConfiguration(
            "127.0.0.1",
            dataPort == 65535 ? 65534 : dataPort + 1,
            "caller",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            false,
            null,
            new ReconnectConfiguration(1, 5, 30))
        {
            DataPort = dataPort,
            E2eIdentityPath = Path.Combine(directory.Path, "caller.e2e.pfx"),
            PortStatePath = Path.Combine(directory.Path, "caller.ports.json")
        };

        using var identity = AgentIdentity.LoadOrCreate(config.E2eIdentityPath, config.ClientId);
        await using var coordinator = new PeerSessionCoordinator(
            config,
            identity,
            sessionId,
            new FrameWriter(Stream.Null),
            NullLogger.Instance,
            new AgentConnectionQuota(10, 5),
            cts.Token);

        await using var connection = await coordinator.ConnectDataAsync(connectionId, sessionId, "token", "caller", cts.Token);
        await serverTask;

        var socket = connection.Socket;
        Assert.True(socket.NoDelay);
        Assert.NotEqual(0, Convert.ToInt32(socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)));

        connection.Abort();
        Assert.Throws<ObjectDisposedException>(() => _ = socket.Poll(0, SelectMode.SelectRead));
    }

    [Fact]
    public async Task RelayPlainSocket_does_not_refresh_idle_timeout_when_peer_write_is_blocked_and_aborts_tunnel()
    {
        using var pair = await LoopbackSocketPair.CreateAsync();
        using var blockingStream = new BlockingPeerStream();
        var framed = new FramedDuplexStream(new FrameReader(blockingStream), new FrameWriter(blockingStream));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var time = new ManualTimeProvider();
        var aborted = false;

        var relayTask = PeerSessionCoordinator.RelayPlainSocketAsync(
            pair.Server,
            framed,
            cts.Token,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            () => aborted = true,
            time);

        await time.WaitForTimerCountAsync(1, cts.Token);
        time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(relayTask.IsCompleted);

        // Sending at t=0.6s enters blocked WriteAsync without calling Touch(), so advancing 0.45s expires the 1s idle watch at t=1.05s (<1.6s).
        await pair.Client.SendAsync(new byte[] { 0x41 }, SocketFlags.None, cts.Token);
        await blockingStream.WriteEntered.Task.WaitAsync(cts.Token);

        time.Advance(TimeSpan.FromMilliseconds(450));
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => relayTask.WaitAsync(cts.Token));
        Assert.True(
            exception is OperationCanceledException or FrameWriteException { InnerException: OperationCanceledException },
            $"Unexpected exception: {exception}");
        Assert.False(cts.IsCancellationRequested, "Relay should terminate on idle watch before the outer deadline.");
        Assert.True(aborted);
    }

    [Fact]
    public async Task RelayEncryptedSocket_coalesces_header_and_payload_and_preserves_half_close()
    {
        using var pair = await LoopbackSocketPair.CreateAsync();
        using var duplex = new RecordingDuplexStream();
        using var framedSink = new MemoryStream();
        var framed = new FramedDuplexStream(new FrameReader(Stream.Null), new FrameWriter(framedSink));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var relayTask = PeerSessionCoordinator.RelayEncryptedSocketAsync(
            pair.Server,
            duplex,
            framed,
            cts.Token,
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero);

        var request = RandomNumberGenerator.GetBytes(1200);
        await pair.Client.SendAsync(request, SocketFlags.None, cts.Token);
        pair.Client.Shutdown(SocketShutdown.Send);

        var receivedFrame = new byte[4 + request.Length];
        await duplex.Outbound.ReadExactlyAsync(receivedFrame, cts.Token);
        Assert.Equal(request.Length, BinaryPrimitives.ReadInt32BigEndian(receivedFrame.AsSpan(0, 4)));
        Assert.Equal(request, receivedFrame.AsSpan(4).ToArray());

        var eofHeader = new byte[4];
        await duplex.Outbound.ReadExactlyAsync(eofHeader, cts.Token);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(eofHeader));

        Assert.Contains(duplex.Writes, chunk =>
            chunk.Length == 4 + request.Length &&
            BinaryPrimitives.ReadInt32BigEndian(chunk) == request.Length &&
            chunk.AsSpan(4).SequenceEqual(request));
        Assert.DoesNotContain(duplex.Writes, chunk => chunk.Length == 4 && BinaryPrimitives.ReadInt32BigEndian(chunk) != 0);

        var reply = RandomNumberGenerator.GetBytes(750);
        var replyFrame = new byte[4 + reply.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(replyFrame.AsSpan(0, 4), reply.Length);
        reply.CopyTo(replyFrame.AsSpan(4));
        await duplex.WriteToInboundAsync(replyFrame, cts.Token);

        var actualReply = new byte[reply.Length];
        var total = 0;
        var buffer = new byte[1024];
        while (true)
        {
            var count = await pair.Client.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            if (count == 0) break;
            buffer.AsSpan(0, count).CopyTo(actualReply.AsSpan(total));
            total += count;
        }
        Assert.Equal(reply.Length, total);
        Assert.Equal(reply, actualReply);

        await relayTask;
        framedSink.Position = 0;
        var fin = await new FrameReader(framedSink).ReadAsync(ProtocolConstants.MaxDataPayloadLength, cts.Token);
        Assert.NotNull(fin);
        Assert.Equal(FrameType.Fin, fin!.Type);
    }

    [Fact]
    public async Task RelayEncryptedSocket_does_not_refresh_idle_timeout_when_peer_write_is_blocked_and_aborts_tunnel()
    {
        using var pair = await LoopbackSocketPair.CreateAsync();
        using var blockingStream = new BlockingPeerStream();
        var framed = new FramedDuplexStream(new FrameReader(Stream.Null), new FrameWriter(Stream.Null));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var time = new ManualTimeProvider();
        var aborted = false;

        var relayTask = PeerSessionCoordinator.RelayEncryptedSocketAsync(
            pair.Server,
            blockingStream,
            framed,
            cts.Token,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            () => aborted = true,
            time);

        await time.WaitForTimerCountAsync(1, cts.Token);
        time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(relayTask.IsCompleted);

        // Sending at t=0.6s enters blocked WriteAsync without calling Touch(), so advancing 0.45s expires the 1s idle watch at t=1.05s (<1.6s).
        await pair.Client.SendAsync(new byte[] { 0x42 }, SocketFlags.None, cts.Token);
        await blockingStream.WriteEntered.Task.WaitAsync(cts.Token);

        time.Advance(TimeSpan.FromMilliseconds(450));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relayTask.WaitAsync(cts.Token));
        Assert.False(cts.IsCancellationRequested, "Relay should terminate on idle watch before the outer deadline.");
        Assert.True(aborted);
    }

    private static (Session Session, MemoryStream Stream) ReadySession(ServerRuntime runtime, string clientId)
    {
        Assert.True(runtime.Sessions.TryRegister(runtime.Configuration.Clients[clientId], out var session));
        session.ProtocolVersion = ProtocolConstants.Version;
        session.MarkReady();
        var stream = new MemoryStream();
        session.AttachControlWriter(new FrameWriter(stream));
        return (session, stream);
    }

    private static ServerRuntime CreateServerRuntime(TestDirectory directory)
    {
        var accessSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var callerSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var targetSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 9, "127.0.0.1", 9, 10, 5)
        {
            AuthorizedClientsOnly = true,
            AccessSecret = accessSecret
        };
        var caller = new ClientConfiguration(1, "caller", "Caller", true, callerSecret, 10, 10, [])
        {
            E2eCertificateSha256 = fingerprint,
            OutboundMappings = [new OutboundMappingConfiguration("visited-private", true, "127.0.0.1", "visited", "private", accessSecret, fingerprint)]
        };
        var visited = new ClientConfiguration(1, "visited", "Visited", true, targetSecret, 10, 10, [channel])
        {
            E2eCertificateSha256 = fingerprint
        };
        var server = new ServerConfiguration(
            1,
            new TunnelConfiguration("127.0.0.1", 7443, true, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            directory.Path,
            new LimitsConfiguration(10, 5, 5, 5, 20, 30, 30),
            null)
        {
            Audit = new AuditConfiguration(Path.Combine(directory.Path, "audit.db"), 90)
        };
        return new ServerRuntime(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>(StringComparer.Ordinal)
        {
            ["caller"] = caller,
            ["visited"] = visited
        }), new SessionRegistry());
    }

    private sealed class LoopbackSocketPair(Socket client, Socket server) : IDisposable
    {
        public Socket Client { get; } = client;
        public Socket Server { get; } = server;

        public static async Task<LoopbackSocketPair> CreateAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
            var server = await listener.AcceptSocketAsync();
            await connectTask;
            return new LoopbackSocketPair(client, server);
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    private sealed class BlockingPeerStream : Stream
    {
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class RecordingDuplexStream : Stream
    {
        private readonly Pipe inbound = new();
        private readonly Pipe outbound = new();
        private readonly Stream inboundStream;
        private readonly Stream outboundStream;

        public RecordingDuplexStream()
        {
            inboundStream = inbound.Reader.AsStream();
            outboundStream = outbound.Reader.AsStream();
        }

        public ConcurrentQueue<byte[]> Writes { get; } = new();
        public Stream Outbound => outboundStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inboundStream.ReadAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes.Enqueue(buffer.ToArray());
            await outbound.Writer.WriteAsync(buffer, cancellationToken);
        }

        public async Task WriteToInboundAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            await inbound.Writer.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inbound.Writer.Complete();
                inbound.Reader.Complete();
                outbound.Writer.Complete();
                outbound.Reader.Complete();
            }
            base.Dispose(disposing);
        }
    }
}
