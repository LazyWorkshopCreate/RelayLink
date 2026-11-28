using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Testing;
using RelayLink.Transport;

namespace RelayLink.UnitTests;

public sealed class PeerRelayReliabilityTests
{
    [Fact]
    public async Task Waiting_for_one_target_does_not_block_other_requests_or_hold_capacity_after_caller_cancellation()
    {
        using var directory = new TestDirectory("relaylink-peer-slow-target-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var runtime = Runtime(directory, openTimeoutSeconds: 60, maxConnections: 3);
        var clients = runtime.Configuration.Clients.ToDictionary();
        var callerConfig = clients["caller"];
        var mapping = callerConfig.OutboundMappings.Single();
        clients["fast"] = clients["visited"] with { ClientId = "fast" };
        clients["caller"] = callerConfig with { OutboundMappings = [mapping, mapping with { MappingId = "fast-private", TargetClientId = "fast" }] };
        runtime.ReplaceConfiguration(runtime.Configuration with { Clients = clients });
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        await audit.StartAsync(deadline.Token);
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(runtime.Configuration), audit, NullLogger<PeerRelayRegistry>.Instance);
        var (caller, callerStream) = ReadySession(runtime, "caller");
        var (slow, _) = ReadySession(runtime, "visited");
        ReadySession(runtime, "fast");
        var blocked = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        slow.AttachControlWriter(new FrameWriter(blocked.Writer.AsStream()));
        try
        {
            await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), mapping.MappingId), attempt.Token).WaitAsync(deadline.Token);
            var fastId = Guid.NewGuid();
            await registry.RequestAsync(caller, new PeerOpenRequestMessage(fastId, "fast-private"), deadline.Token);
            callerStream.Position = 0;
            var frame = await new FrameReader(callerStream).ReadAsync(8192, deadline.Token);
            Assert.Equal(FrameType.PeerOpenGranted, frame?.Type);
            Assert.Equal(fastId, JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>(frame!.Payload.Span).RequestId);
            attempt.Cancel();
            while (runtime.GlobalPendingLimit.CurrentCount != 2) await Task.Delay(5, deadline.Token);
            Assert.Empty(registry.ListConnections("visited", "private"));
            Assert.Single(registry.ListConnections("fast", "private"));
            Assert.True(caller.IsReady);
        }
        finally
        {
            registry.CancelSession(caller.SessionId);
            await blocked.Reader.CompleteAsync();
        }
    }

    [Fact]
    public async Task Legacy_admission_rechecks_the_original_channel_and_records_one_rejection()
    {
        using var directory = new TestDirectory("relaylink-peer-admission-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runtime = Runtime(directory, 5, 2);
        var (caller, replies) = ReadySession(runtime, "caller");
        caller.ProtocolVersion = 3;
        var (_, targetControl) = ReadySession(runtime, "visited");
        var original = runtime.Configuration;
        var clients = original.Clients.ToDictionary();
        var target = clients["visited"];
        clients["visited"] = target with { Channels = [target.Channels[0] with { TransportProtocol = "udp" }] };
        var updated = original with { Clients = clients };
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        await audit.StartAsync(deadline.Token);
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(original), audit, NullLogger<PeerRelayRegistry>.Instance);
        runtime.ReplaceConfiguration(original with { Clients = new SwitchingClients(original.Clients.ToDictionary(), () => runtime.ReplaceConfiguration(updated)) });
        await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), deadline.Token);
        replies.Position = 0;
        Assert.Equal(FrameType.PeerOpenRejected, (await new FrameReader(replies).ReadAsync(8192, deadline.Token))?.Type);
        Assert.Equal(0, targetControl.Length);
        Assert.Empty(registry.ListConnections("visited", "private"));
        AuditPage page;
        do { page = await audit.ReadAsync(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, null, null, 1, 10, deadline.Token); }
        while (page.Total == 0 && !deadline.IsCancellationRequested);
        var row = Assert.Single(page.Events);
        Assert.Equal(("peer_connection_rejected", "aborted", "configuration_changed"), (row.EventType, row.Outcome, row.ReasonCode));
    }

    private sealed class SwitchingClients(IDictionary<string, ClientConfiguration> clients, Action publish)
        : System.Collections.ObjectModel.ReadOnlyDictionary<string, ClientConfiguration>(clients), IReadOnlyDictionary<string, ClientConfiguration>
    {
        bool IReadOnlyDictionary<string, ClientConfiguration>.TryGetValue(string key,
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ClientConfiguration value)
        {
            var found = TryGetValue(key, out value);
            if (key == "visited") publish();
            return found;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Negotiated_relay_holds_the_pending_slot_until_the_authorized_target_is_ready(bool confirmTarget)
    {
        using var directory = new TestDirectory("relaylink-relay-pending-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        var runtime = Runtime(directory, openTimeoutSeconds: 5, maxConnections: 2, maxPending: 1);
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        await audit.StartAsync(token);
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(runtime.Configuration), audit, NullLogger<PeerRelayRegistry>.Instance);
        (Session session, FrameReader control) Register(string id)
        {
            var client = runtime.Configuration.Clients[id];
            Assert.True(runtime.Sessions.TryRegister(client, out var session));
            session.RelayControlReady = true;
            session.SetConfigVersion(ConfigurationSnapshotFactory.Create(client, runtime.Configuration).Version);
            session.MarkReady();
            var pipe = new Pipe();
            session.AttachControlWriter(new FrameWriter(pipe.Writer.AsStream()));
            return (session, new FrameReader(pipe.Reader.AsStream()));
        }
        var (caller, callerControl) = Register("caller");
        var (target, targetControl) = Register("visited");
        await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), token);
        var grant = JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>((await callerControl.ReadAsync(8192, token))!.Payload.Span);
        var open = JsonProtocolSerializer.Deserialize<PeerOpenMessage>((await targetControl.ReadAsync(8192, token))!.Payload.Span);
        using var callerInput = new WaitingReadStream();
        using var targetInput = new WaitingReadStream();
        using var callerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var targetSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var bound = new Pipe();
        Assert.True(registry.TryBind(new PeerBindDataMessage(open.ConnectionId, caller.SessionId, grant.Token, "caller"),
            new DataTunnel(callerInput, new FrameReader(callerInput), new FrameWriter(Stream.Null), callerSocket), out var relay));
        Assert.True(registry.TryBind(new PeerBindDataMessage(open.ConnectionId, target.SessionId, open.Token, "target"),
            new DataTunnel(targetInput, new FrameReader(targetInput), new FrameWriter(bound.Writer.AsStream()), targetSocket), out _));
        Assert.Equal(FrameType.PeerBindAccepted, (await new FrameReader(bound.Reader.AsStream()).ReadAsync(8192, token))?.Type);
        try
        {
            var report = new PeerRelaySignal(grant.RelayBinding!, new string('B', 64));
            registry.ReportSetup(caller, FrameType.PeerRelayAuthenticated, report);
            registry.ReportSetup(target, FrameType.PeerRelayAuthenticated, report);
            var prepare = await targetControl.ReadAsync(8192, token);
            Assert.Equal(FrameType.PeerRelayPrepare, prepare?.Type);
            Assert.Equal("connecting", relay!.Snapshot().State);
            Assert.Equal(0, runtime.GlobalPendingLimit.CurrentCount);
            await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), token);
            var denied = await callerControl.ReadAsync(8192, token);
            Assert.Equal(ErrorCode.CapacityExceeded, JsonProtocolSerializer.Deserialize<PeerOpenRejectedMessage>(denied!.Payload.Span).ErrorCode);
            if (confirmTarget)
            {
                registry.ReportSetup(target, FrameType.PeerRelayTargetReady, JsonProtocolSerializer.Deserialize<PeerRelaySignal>(prepare!.Payload.Span));
                Assert.Equal(FrameType.PeerRelayTargetReady, (await callerControl.ReadAsync(8192, token))?.Type);
                while (!relay.Opened) await Task.Delay(5, token);
                Assert.Equal(1, runtime.GlobalPendingLimit.CurrentCount);
            }
            else registry.RevokeClientConnections(target.ClientId);
        }
        finally
        {
            registry.CancelSession(caller.SessionId);
            await relay!.WaitForProcessingAsync().WaitAsync(token);
        }
        AuditPage closed;
        do
        {
            closed = await audit.ReadAsync(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "peer_connection_closed", null, 1, 10, token);
            if (closed.Total == 0) await Task.Delay(5, token);
        } while (closed.Total == 0);
        var row = Assert.Single(closed.Events);
        Assert.Equal(("aborted", confirmTarget ? "session_closed" : "client_revoked"), (row.Outcome, row.ReasonCode));
    }

    [Fact]
    public void Published_snapshot_cache_is_scoped_to_target_configuration_and_protocol_version()
    {
        using var directory = new TestDirectory("relaylink-relay-version-");
        var runtime = Runtime(directory, openTimeoutSeconds: 5, maxConnections: 2);
        var first = runtime.Configuration;
        var caller = first.Clients["caller"];
        var warm = ConfigurationSnapshotFactory.Create(caller, first);
        Assert.Null(warm.Snapshot.OutboundMappings.Single().EndToEndEncryptionEnabled);
        var withMode = ConfigurationSnapshotFactory.Create(caller, first, 5, mappingEncryptionStatus: true);
        Assert.True(withMode.Snapshot.OutboundMappings.Single().EndToEndEncryptionEnabled);
        Assert.NotEqual(warm.Version, withMode.Version);
        Assert.Equal(warm.Version, ConfigurationSnapshotHasher.Compute(warm.Snapshot));
        Assert.Equal(ConfigurationSnapshotFactory.Create(caller, first, 4).Version,
            ConfigurationSnapshotFactory.Create(caller, first, 4, mappingEncryptionStatus: true).Version);
        Assert.Same(warm.Snapshot, ConfigurationSnapshotFactory.Create(caller, first).Snapshot);
        Assert.NotSame(warm.Snapshot, ConfigurationSnapshotFactory.Create(caller, first, 3).Snapshot);
        var clients = first.Clients.ToDictionary();
        var target = clients["visited"];
        clients["visited"] = target with { Channels = [target.Channels[0] with { TransportProtocol = "udp" }] };
        var updated = first with { Clients = clients };
        var changed = ConfigurationSnapshotFactory.Create(caller, updated);
        Assert.NotEqual(warm.Version, changed.Version);
        Assert.Equal("udp", Assert.Single(changed.Snapshot.OutboundMappings).TransportProtocol);
    }

    [Fact]
    public async Task Termination_waits_for_inflight_successful_write_before_releasing_capacity_and_recording_bytes()
    {
        using var directory = new TestDirectory("relaylink-peer-final-bytes-");
        var runtime = Runtime(directory, openTimeoutSeconds: 30, maxConnections: 2);
        var metrics = new MetricsRegistry(runtime.Configuration);
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        await audit.StartAsync(CancellationToken.None);
        var registry = new PeerRelayRegistry(runtime, metrics, audit, NullLogger<PeerRelayRegistry>.Instance);
        var (caller, callerControl) = ReadySession(runtime, "caller");
        var (target, targetControl) = ReadySession(runtime, "visited");
        await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), CancellationToken.None);
        callerControl.Position = 0; targetControl.Position = 0;
        var grantedFrame = await new FrameReader(callerControl).ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None);
        var openFrame = await new FrameReader(targetControl).ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None);
        var granted = JsonProtocolSerializer.Deserialize<PeerOpenGrantedMessage>(grantedFrame!.Payload.Span);
        var open = JsonProtocolSerializer.Deserialize<PeerOpenMessage>(openFrame!.Payload.Span);
        using var callerInput = new MemoryStream();
        await new FrameWriter(callerInput).WriteAsync(new Frame(FrameType.Data, new byte[] { 1, 2, 3 }), CancellationToken.None);
        callerInput.Position = 0;
        using var callerOutput = new MemoryStream();
        using var targetInput = new WaitingReadStream();
        using var targetOutput = new DelayedDataFlushStream();
        using var callerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var targetSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.True(registry.TryBind(new PeerBindDataMessage(granted.ConnectionId, caller.SessionId, granted.Token, "caller"),
            new DataTunnel(callerInput, new FrameReader(callerInput), new FrameWriter(callerOutput), callerSocket), out _));
        Assert.True(registry.TryBind(new PeerBindDataMessage(granted.ConnectionId, target.SessionId, open.Token, "target"),
            new DataTunnel(targetInput, new FrameReader(targetInput), new FrameWriter(targetOutput), targetSocket), out _));
        await targetOutput.DataWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.TryDisconnect("visited", "private", granted.ConnectionId));
        try
        {
            Assert.Equal(1, runtime.GlobalConnectionLimit.CurrentCount);
            Assert.Empty((await ReadClosed()).Events);
        }
        finally { targetOutput.CompleteWrite.TrySetResult(); }
        AuditPage page;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do { await Task.Delay(20, deadline.Token); page = await ReadClosed(); } while (page.Total == 0);
        var closed = Assert.Single(page.Events);
        Assert.Equal(3, closed.BytesToTarget);
        Assert.Equal(metrics.For("visited", "private").Snapshot().PeerCiphertextToTarget, closed.BytesToTarget);
        Assert.Equal(2, runtime.GlobalConnectionLimit.CurrentCount);
        Assert.Equal("admin_disconnected", closed.ReasonCode);

        Task<AuditPage> ReadClosed() => audit.ReadAsync(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1),
            "peer_connection_closed", "visited", 1, 100, CancellationToken.None);
    }

    [Fact]
    public async Task Duplicate_request_id_does_not_take_another_relay_or_quota()
    {
        using var directory = new TestDirectory("relaylink-peer-dedupe-");
        var runtime = Runtime(directory, openTimeoutSeconds: 30, maxConnections: 2);
        var logger = new CollectingLogger();
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(runtime.Configuration), new AuditService(runtime, NullLogger<AuditService>.Instance), logger);
        var (caller, callerStream) = ReadySession(runtime, "caller");
        ReadySession(runtime, "visited");
        var requestId = Guid.NewGuid();

        await registry.RequestAsync(caller, new PeerOpenRequestMessage(requestId, "visited-private"), CancellationToken.None);
        await registry.RequestAsync(caller, new PeerOpenRequestMessage(requestId, "visited-private"), CancellationToken.None);
        await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), CancellationToken.None);

        Assert.Equal(2, registry.ListConnections("visited", "private").Length);
        callerStream.Position = 0;
        var reader = new FrameReader(callerStream);
        var frames = new List<Frame>();
        while (await reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, CancellationToken.None) is { } frame) frames.Add(frame);
        var rejected = frames.Single(frame => frame.Type == FrameType.PeerOpenRejected);
        Assert.Equal(ErrorCode.ProtocolError, JsonProtocolSerializer.Deserialize<PeerOpenRejectedMessage>(rejected.Payload.Span).ErrorCode);
        Assert.Equal(2, frames.Count(frame => frame.Type == FrameType.PeerOpenGranted));
        registry.CancelSession(caller.SessionId);
    }

    [Fact]
    public async Task Unexpected_relay_exception_is_logged_and_the_relay_is_removed()
    {
        using var directory = new TestDirectory("relaylink-peer-fault-");
        var runtime = Runtime(directory, openTimeoutSeconds: -1, maxConnections: 2);
        var logger = new CollectingLogger();
        var audit = new AuditService(runtime, NullLogger<AuditService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await audit.StartAsync(deadline.Token);
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(runtime.Configuration), audit, logger);
        var (caller, _) = ReadySession(runtime, "caller");
        ReadySession(runtime, "visited");

        await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), deadline.Token);

        AuditPage terminal;
        do
        {
            terminal = await audit.ReadAsync(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "peer_connection_rejected", "visited", 1, 10, deadline.Token);
            if (terminal.Total == 0) await Task.Delay(10, deadline.Token);
        } while (terminal.Total == 0);
        Assert.Equal("unexpected", Assert.Single(terminal.Events).ReasonCode);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("failed unexpectedly", StringComparison.Ordinal));
        Assert.Empty(registry.ListConnections("visited", "private"));
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

    private static ServerRuntime Runtime(TestDirectory directory, int openTimeoutSeconds, int maxConnections, int? maxPending = null)
    {
        var accessSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var callerSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var targetSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var channel = new ChannelConfiguration("private", "Private", true, "127.0.0.1", 9, "127.0.0.1", 9, maxConnections, 5)
        {
            AuthorizedClientsOnly = true,
            AccessSecret = accessSecret
        };
        var caller = new ClientConfiguration(1, "caller", "Caller", true, callerSecret, maxConnections, maxConnections, [])
        {
            E2eCertificateSha256 = fingerprint,
            OutboundMappings = [new OutboundMappingConfiguration("visited-private", true, "127.0.0.1", "visited", "private", accessSecret, fingerprint)]
        };
        var visited = new ClientConfiguration(1, "visited", "Visited", true, targetSecret, maxConnections, maxConnections, [channel])
        {
            E2eCertificateSha256 = fingerprint
        };
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, true, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            directory.Path,
            new LimitsConfiguration(maxConnections, maxPending ?? maxConnections, maxConnections, maxConnections, openTimeoutSeconds, 30, 30),
            null)
        { Audit = new AuditConfiguration(Path.Combine(directory.Path, "audit.db"), 90) };
        return new ServerRuntime(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>(StringComparer.Ordinal)
        {
            ["caller"] = caller,
            ["visited"] = visited
        }), new SessionRegistry());
    }

    private sealed class CollectingLogger : ILogger<PeerRelayRegistry>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private sealed class WaitingReadStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }

    private sealed class DelayedDataFlushStream : MemoryStream
    {
        private bool data;
        public TaskCompletionSource DataWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == ProtocolConstants.HeaderLength && buffer.Span[..4].SequenceEqual("NTP1"u8))
                data = buffer.Span[5] == (byte)FrameType.Data;
            return base.WriteAsync(buffer, CancellationToken.None);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!data) return;
            DataWritten.TrySetResult();
            // Model an OS write that completed successfully while cancellation raced.
            await CompleteWrite.Task;
        }
    }
}
