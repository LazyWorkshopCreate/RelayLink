using System.Security.Cryptography;
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
        var registry = new PeerRelayRegistry(runtime, new MetricsRegistry(runtime.Configuration), new AuditService(runtime, NullLogger<AuditService>.Instance), logger);
        var (caller, _) = ReadySession(runtime, "caller");
        ReadySession(runtime, "visited");

        await registry.RequestAsync(caller, new PeerOpenRequestMessage(Guid.NewGuid(), "visited-private"), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !logger.Entries.Any(entry => entry.Level == LogLevel.Error && entry.Message.Contains("failed unexpectedly", StringComparison.Ordinal)))
            await Task.Delay(20);
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

    private static ServerRuntime Runtime(TestDirectory directory, int openTimeoutSeconds, int maxConnections)
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
            new LimitsConfiguration(maxConnections, maxConnections, maxConnections, maxConnections, openTimeoutSeconds, 30, 30),
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
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
