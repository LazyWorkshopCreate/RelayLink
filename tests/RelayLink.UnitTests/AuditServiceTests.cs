using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class AuditServiceTests
{
    [Fact]
    public async Task Audit_is_persistent_queryable_and_recovers_unclosed_connections_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"relaylink-audit-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "audit.db");
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
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Locked_database_rejects_audit_write_then_recovers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"relaylink-audit-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "audit.db");
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
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
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
