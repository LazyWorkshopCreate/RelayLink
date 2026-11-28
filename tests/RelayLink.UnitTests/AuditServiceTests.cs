using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Testing;

namespace RelayLink.UnitTests;

public sealed class AuditServiceTests
{
    [Fact]
    public async Task Audit_is_persistent_queryable_and_recovers_unclosed_connections_once()
    {
        using var directory = new TestDirectory("relaylink-audit-");
        try
        {
            var path = Path.Combine(directory.Path, "audit.db");
            var connectionId = Guid.NewGuid();
            var first = Service(path);
            await first.StartAsync(CancellationToken.None);
            await first.RecordAsync(new AuditEvent("proxy_connection_opened", "success")
            { ClientId = "agent", ChannelId = "echo", ConnectionId = connectionId, RemoteIp = "127.0.0.1" });
            await first.RecordAsync(new AuditEvent("admin_login_failure", "denied")
            { Actor = "operator", ReasonCode = "invalid_credentials" });

            var restarted = Service(path);
            await restarted.StartAsync(CancellationToken.None);
            var page = await restarted.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), null, "agent", 1, 50, CancellationToken.None);
            Assert.Equal(2, page.Total);
            Assert.Contains(page.Events, item => item.EventType == "proxy_connection_opened" && item.ConnectionId == connectionId);
            Assert.Contains(page.Events, item => item.EventType == "proxy_connection_closed" && item.ConnectionId == connectionId && item.ReasonCode == "server_restart");
            var again = Service(path);
            await again.StartAsync(CancellationToken.None);
            Assert.Equal(2, (await again.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), null, "agent", 1, 50, CancellationToken.None)).Total);
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task Locked_database_rejects_audit_write_then_recovers()
    {
        using var directory = new TestDirectory("relaylink-audit-");
        try
        {
            var path = Path.Combine(directory.Path, "audit.db");
            var service = Service(path);
            await service.StartAsync(CancellationToken.None);
            await using var blocker = new SqliteConnection($"Data Source={path}");
            await blocker.OpenAsync();
            await using (var lockCommand = blocker.CreateCommand())
            {
                lockCommand.CommandText = "BEGIN IMMEDIATE";
                await lockCommand.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<AuditUnavailableException>(() => service.RecordAsync(new AuditEvent("admin_login_success", "success")));
            await using (var rollback = blocker.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync();
            }
            await service.RecordAsync(new AuditEvent("admin_login_success", "success"));
            Assert.Equal(1, (await service.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), null, null, 1, 50, CancellationToken.None)).Total);
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task Permission_refusal_is_reported_as_audit_unavailable()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var directory = Directory.CreateTempSubdirectory("relaylink-audit-perm-").FullName;
        var openedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            var path = Path.Combine(directory, "audit.db");
            var service = Service(path);
            await service.StartAsync(CancellationToken.None);
            File.SetUnixFileMode(directory, openedMode | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit);
            var exception = await Assert.ThrowsAsync<AuditUnavailableException>(() => service.RecordAsync(new AuditEvent("admin_logout", "success")));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }
        finally
        {
            File.SetUnixFileMode(directory, openedMode);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rejection_flood_does_not_starve_critical_audit_writes_and_recovery_matches_event_types()
    {
        using var directory = new TestDirectory("relaylink-audit-flood-");
        try
        {
            var path = Path.Combine(directory.Path, "audit.db");
            var first = Service(path);
            await first.StartAsync(CancellationToken.None);

            // Simulate rejection flood + opened peer connection without closing it,
            // plus a closed proxy connection that must NOT suppress recovering the peer connection.
            var proxyId = Guid.NewGuid();
            var peerId = Guid.NewGuid();
            for (var index = 0; index < 300; index++)
                await first.RecordAsync(new AuditEvent("security_group_rejected", "denied") { ClientId = "agent", ChannelId = "echo", RemoteIp = $"192.0.2.{index % 20}" });

            await first.RecordAsync(new AuditEvent("proxy_connection_opened", "success") { ClientId = "agent", ChannelId = "echo", ConnectionId = proxyId, RemoteIp = "127.0.0.1" });
            await first.RecordAsync(new AuditEvent("proxy_connection_closed", "closed") { ClientId = "agent", ChannelId = "echo", ConnectionId = proxyId, ReasonCode = "completed" });
            await first.RecordAsync(new AuditEvent("peer_connection_opened", "success") { ClientId = "agent", ChannelId = "peer", ConnectionId = peerId, RemoteIp = "127.0.0.1" });

            var restarted = Service(path);
            await restarted.StartAsync(CancellationToken.None);
            var page = await restarted.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), null, "agent", 1, 50, CancellationToken.None);
            Assert.Contains(page.Events, item => item.EventType == "proxy_connection_opened" && item.ConnectionId == proxyId);
            Assert.Contains(page.Events, item => item.EventType == "peer_connection_closed" && item.ConnectionId == peerId && item.ReasonCode == "server_restart");
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static AuditService Service(string path)
    {
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            "clients", new LimitsConfiguration(100, 20, 10, 10, 20, 120, 300), null)
        { Audit = new AuditConfiguration(path) };
        var runtime = new ServerRuntime(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>()), new SessionRegistry());
        return new AuditService(runtime, NullLogger<AuditService>.Instance);
    }
}
