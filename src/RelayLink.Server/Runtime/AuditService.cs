using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed record AuditEvent(string EventType, string Outcome)
{
    public string? ReasonCode { get; init; }
    public Guid? ConnectionId { get; init; }
    public Guid? SessionId { get; init; }
    public string? ClientId { get; init; }
    public string? ChannelId { get; init; }
    public string? MappingId { get; init; }
    public string? CallerClientId { get; init; }
    public string? TargetClientId { get; init; }
    public string? Actor { get; init; }
    public string? RemoteIp { get; init; }
    public long? DurationMs { get; init; }
    public long BytesToTarget { get; init; }
    public long BytesToCaller { get; init; }
    // 自动化管理面：授权标识与设备公钥指纹，均为非秘密元数据。
    public Guid? AutomationGrantId { get; init; }
    public string? DeviceKeyThumbprint { get; init; }
}

public sealed record AuditEventRow(Guid EventId, DateTimeOffset OccurredAtUtc, string EventType, string Outcome,
    string? ReasonCode, Guid? ConnectionId, Guid? SessionId, string? ClientId, string? ChannelId,
    string? MappingId, string? CallerClientId, string? TargetClientId, string? Actor, string? RemoteIp,
    long? DurationMs, long BytesToTarget, long BytesToCaller, Guid ServerInstanceId)
{
    public Guid? AutomationGrantId { get; init; }
    public string? DeviceKeyThumbprint { get; init; }
}

public sealed record AuditPage(long Total, IReadOnlyList<AuditEventRow> Events);

public sealed class AuditUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AuditService(ServerRuntime runtime, ILogger<AuditService> logger) : IHostedService
{
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly SemaphoreSlim capacity = new(1024, 1024);
    private readonly SemaphoreSlim rejectionCapacity = new(256, 256);
    private readonly ConcurrentDictionary<string, long> recentRejections = new(StringComparer.Ordinal);
    private long lastOverflowWarningTick;
    private DateOnly lastTrimDate;
    private const long RejectionCoalesceWindowMs = 5_000;
    private const int MaxTrackedRejections = 2048;
    private string FilePath => runtime.Configuration.Server.Audit!.FilePath;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=WAL";
            await journal.ExecuteNonQueryAsync(cancellationToken);
            SecretFilePermissions.RestrictDatabase(FilePath);
            await using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS audit_event (
                    event_id TEXT PRIMARY KEY, occurred_at_utc INTEGER NOT NULL,
                    event_type TEXT NOT NULL, outcome TEXT NOT NULL, reason_code TEXT,
                    connection_id TEXT, session_id TEXT, client_id TEXT, channel_id TEXT,
                    mapping_id TEXT, caller_client_id TEXT, target_client_id TEXT,
                    actor TEXT, remote_ip TEXT, duration_ms INTEGER,
                    bytes_to_target INTEGER NOT NULL, bytes_to_caller INTEGER NOT NULL,
                    server_instance_id TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_audit_event_time ON audit_event (occurred_at_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_audit_event_connection ON audit_event (connection_id, occurred_at_utc);
                CREATE INDEX IF NOT EXISTS ix_audit_event_client_time ON audit_event (client_id, occurred_at_utc DESC);
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken);
            // 自动化管理面新增两列。既有部署的审计库就地增量迁移，无需重建。
            await AddColumnIfMissingAsync(connection, "automation_grant_id", cancellationToken);
            await AddColumnIfMissingAsync(connection, "device_key_thumbprint", cancellationToken);
            // A previous process can die after the opening event was committed. Close only
            // unmatched openings, once, and retain their original correlation ID.
            await using var recover = connection.CreateCommand();
            recover.CommandText = """
                INSERT INTO audit_event (event_id, occurred_at_utc, event_type, outcome, reason_code,
                    connection_id, session_id, client_id, channel_id, mapping_id, caller_client_id,
                    target_client_id, actor, remote_ip, duration_ms, bytes_to_target, bytes_to_caller, server_instance_id)
                SELECT lower(hex(randomblob(16))), $now,
                    CASE event_type WHEN 'proxy_connection_opened' THEN 'proxy_connection_closed'
                                    WHEN 'peer_connection_opened' THEN 'peer_connection_closed'
                                    ELSE 'agent_session_closed' END,
                    'aborted', 'server_restart', connection_id, session_id, client_id, channel_id,
                    mapping_id, caller_client_id, target_client_id, actor, remote_ip,
                    MAX(0, $now - occurred_at_utc), 0, 0, $instance
                FROM audit_event AS opened
                WHERE opened.event_type IN ('proxy_connection_opened', 'peer_connection_opened', 'agent_session_ready')
                  AND NOT EXISTS (SELECT 1 FROM audit_event AS closed
                    WHERE (
                        (opened.event_type = 'proxy_connection_opened' AND opened.connection_id IS NOT NULL
                         AND closed.connection_id = opened.connection_id AND closed.event_type = 'proxy_connection_closed')
                     OR (opened.event_type = 'peer_connection_opened' AND opened.connection_id IS NOT NULL
                         AND closed.connection_id = opened.connection_id AND closed.event_type = 'peer_connection_closed')
                     OR (opened.event_type = 'agent_session_ready' AND opened.session_id IS NOT NULL
                         AND closed.session_id = opened.session_id AND closed.event_type = 'agent_session_closed')
                    ));
                """;
            recover.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            recover.Parameters.AddWithValue("$instance", runtime.InstanceId.ToString());
            await recover.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task CheckAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await writer.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var probe = connection.CreateCommand();
                probe.CommandText = "BEGIN IMMEDIATE; ROLLBACK;";
                await probe.ExecuteNonQueryAsync(cancellationToken);
            }
            finally { writer.Release(); }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Audit storage health probe failed.");
            throw new AuditUnavailableException("Audit storage is unavailable.", exception);
        }
    }

    public async Task RecordAsync(AuditEvent value, CancellationToken cancellationToken = default)
    {
        var isRejection = IsHighFrequencyRejection(value);
        if (isRejection && !ShouldRecordRejection(value))
            return;

        var quota = isRejection ? rejectionCapacity : capacity;
        if (!await quota.WaitAsync(0, cancellationToken))
        {
            if (isRejection)
            {
                LogCoalescedWarning("High-frequency audit rejection capacity exceeded; dropping {EventType} ({ReasonCode}).", value.EventType, value.ReasonCode);
                return;
            }
            throw new AuditUnavailableException("Audit writer capacity exceeded.");
        }
        try
        {
            await writer.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO audit_event (event_id, occurred_at_utc, event_type, outcome, reason_code,
                        connection_id, session_id, client_id, channel_id, mapping_id, caller_client_id,
                        target_client_id, actor, remote_ip, duration_ms, bytes_to_target, bytes_to_caller,
                        server_instance_id, automation_grant_id, device_key_thumbprint)
                    VALUES ($id, $at, $type, $outcome, $reason, $connection, $session, $client,
                        $channel, $mapping, $caller, $target, $actor, $remote, $duration,
                        $toTarget, $toCaller, $instance, $grant, $thumb)
                    ON CONFLICT(event_id) DO NOTHING
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$type", value.EventType);
                command.Parameters.AddWithValue("$outcome", value.Outcome);
                command.Parameters.AddWithValue("$reason", Db(value.ReasonCode));
                command.Parameters.AddWithValue("$connection", Db(value.ConnectionId?.ToString()));
                command.Parameters.AddWithValue("$session", Db(value.SessionId?.ToString()));
                command.Parameters.AddWithValue("$client", Db(value.ClientId));
                command.Parameters.AddWithValue("$channel", Db(value.ChannelId));
                command.Parameters.AddWithValue("$mapping", Db(value.MappingId));
                command.Parameters.AddWithValue("$caller", Db(value.CallerClientId));
                command.Parameters.AddWithValue("$target", Db(value.TargetClientId));
                command.Parameters.AddWithValue("$actor", Db(value.Actor));
                command.Parameters.AddWithValue("$remote", Db(value.RemoteIp));
                command.Parameters.AddWithValue("$duration", Db(value.DurationMs));
                command.Parameters.AddWithValue("$toTarget", value.BytesToTarget);
                command.Parameters.AddWithValue("$toCaller", value.BytesToCaller);
                command.Parameters.AddWithValue("$instance", runtime.InstanceId.ToString());
                command.Parameters.AddWithValue("$grant", Db(value.AutomationGrantId?.ToString()));
                command.Parameters.AddWithValue("$thumb", Db(value.DeviceKeyThumbprint));
                await command.ExecuteNonQueryAsync(cancellationToken);
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (today != lastTrimDate)
                {
                    await using var trim = connection.CreateCommand();
                    trim.CommandText = "DELETE FROM audit_event WHERE occurred_at_utc < $cutoff";
                    trim.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-runtime.Configuration.Server.Audit!.RetentionDays).ToUnixTimeMilliseconds());
                    await trim.ExecuteNonQueryAsync(cancellationToken);
                    lastTrimDate = today;
                }
            }
            finally { writer.Release(); }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            if (isRejection)
            {
                LogCoalescedWarning("Audit storage unavailable while recording rejection {EventType} ({ReasonCode}).", value.EventType, value.ReasonCode);
            }
            else
            {
                logger.LogError(exception, "Audit event {EventType} could not be persisted.", value.EventType);
            }
            throw new AuditUnavailableException("Audit storage is unavailable.", exception);
        }
        finally { quota.Release(); }
    }

    private static bool IsHighFrequencyRejection(AuditEvent value) =>
        (value.EventType is "proxy_connection_rejected" or "peer_connection_rejected" &&
         value.ReasonCode is "security_group_denied" or "agent_offline" or "channel_unavailable" or "udp_not_supported" or "http_unavailable") ||
        (value.EventType == "admin_login_failure" && value.ReasonCode is "rate_limited" or "login_state_full");

    private bool ShouldRecordRejection(AuditEvent value)
    {
        var now = Environment.TickCount64;
        if (recentRejections.Count >= MaxTrackedRejections)
        {
            foreach (var entry in recentRejections.ToArray())
            {
                if (now - entry.Value >= RejectionCoalesceWindowMs) recentRejections.TryRemove(entry.Key, out _);
            }
            if (recentRejections.Count >= MaxTrackedRejections)
            {
                LogCoalescedWarning("High-frequency audit rejection table is full; coalescing {EventType} ({ReasonCode}).", value.EventType, value.ReasonCode);
                return false;
            }
        }

        var key = $"{value.EventType}\0{value.ReasonCode}\0{value.ClientId}\0{value.ChannelId}\0{value.Actor}\0{value.RemoteIp}";
        while (true)
        {
            if (recentRejections.TryAdd(key, now)) return true;
            if (!recentRejections.TryGetValue(key, out var previous)) continue;
            if (now - previous < RejectionCoalesceWindowMs) return false;
            if (recentRejections.TryUpdate(key, now, previous)) return true;
        }
    }

    private void LogCoalescedWarning(string template, string eventType, string? reasonCode)
    {
        var now = Environment.TickCount64;
        var prior = Volatile.Read(ref lastOverflowWarningTick);
        if (now - prior >= 10_000 && Interlocked.CompareExchange(ref lastOverflowWarningTick, now, prior) == prior)
            logger.LogWarning(template, eventType, reasonCode);
    }

    public async Task<AuditPage> ReadAsync(DateTimeOffset since, DateTimeOffset until, string? eventType, string? clientId,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string predicate = "occurred_at_utc >= $since AND occurred_at_utc <= $until AND ($type IS NULL OR event_type = $type) AND ($client IS NULL OR client_id = $client OR caller_client_id = $client OR target_client_id = $client)";
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM audit_event WHERE {predicate}";
        AddFilters(count, since, until, eventType, clientId);
        var total = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT event_id, occurred_at_utc, event_type, outcome, reason_code, connection_id,
                   session_id, client_id, channel_id, mapping_id, caller_client_id, target_client_id,
                   actor, remote_ip, duration_ms, bytes_to_target, bytes_to_caller, server_instance_id,
                   automation_grant_id, device_key_thumbprint
            FROM audit_event WHERE {predicate}
            ORDER BY occurred_at_utc DESC, event_id DESC LIMIT $limit OFFSET $offset
            """;
        AddFilters(command, since, until, eventType, clientId);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
        var items = new List<AuditEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new AuditEventRow(Guid.Parse(reader.GetString(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2), reader.GetString(3), OptionalString(reader, 4), OptionalGuid(reader, 5), OptionalGuid(reader, 6),
                OptionalString(reader, 7), OptionalString(reader, 8), OptionalString(reader, 9), OptionalString(reader, 10),
                OptionalString(reader, 11), OptionalString(reader, 12), OptionalString(reader, 13), reader.IsDBNull(14) ? null : reader.GetInt64(14),
                reader.GetInt64(15), reader.GetInt64(16), Guid.Parse(reader.GetString(17)))
            {
                AutomationGrantId = OptionalGuid(reader, 18),
                DeviceKeyThumbprint = OptionalString(reader, 19)
            });
        return new AuditPage(total, items);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            SecretFilePermissions.PrepareDatabase(FilePath);
        }
        catch (InvalidOperationException exception)
        {
            throw new AuditUnavailableException("Audit storage is unavailable.", exception);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, DefaultTimeout = 5 }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            SecretFilePermissions.RestrictDatabase(FilePath);
            return connection;
        }
        catch (InvalidOperationException exception)
        {
            await connection.DisposeAsync();
            throw new AuditUnavailableException("Audit storage is unavailable.", exception);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void AddFilters(SqliteCommand command, DateTimeOffset since, DateTimeOffset until, string? eventType, string? clientId)
    {
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$until", until.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$type", Db(eventType));
        command.Parameters.AddWithValue("$client", Db(clientId));
    }

    private static object Db(string? value) => (object?)value ?? DBNull.Value;
    private static object Db(long? value) => (object?)value ?? DBNull.Value;

    /// <summary>
    /// 增量加列。用 PRAGMA 查既有列，避免 ALTER TABLE 重复执行报错；
    /// 旧审计库因此无需重建，符合兼容性内增补的要求。
    /// </summary>
    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string column, CancellationToken cancellationToken)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('audit_event') WHERE name = $name;";
            probe.Parameters.AddWithValue("$name", column);
            var exists = Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (exists > 0) return;
        }

        await using var alter = connection.CreateCommand();
        // 列名来自本类内部的固定字面量，不来自外部输入。
        alter.CommandText = $"ALTER TABLE audit_event ADD COLUMN {column} TEXT;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
    private static string? OptionalString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? OptionalGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
}
