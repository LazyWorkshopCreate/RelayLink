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
}

public sealed record AuditEventRow(Guid EventId, DateTimeOffset OccurredAtUtc, string EventType, string Outcome,
    string? ReasonCode, Guid? ConnectionId, Guid? SessionId, string? ClientId, string? ChannelId,
    string? MappingId, string? CallerClientId, string? TargetClientId, string? Actor, string? RemoteIp,
    long? DurationMs, long BytesToTarget, long BytesToCaller, Guid ServerInstanceId);

public sealed record AuditPage(long Total, IReadOnlyList<AuditEventRow> Events);

public sealed class AuditUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AuditService(ServerRuntime runtime, ILogger<AuditService> logger) : IHostedService
{
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly SemaphoreSlim capacity = new(1024, 1024);
    private DateOnly lastTrimDate;
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
                WHERE event_type IN ('proxy_connection_opened', 'peer_connection_opened', 'agent_session_ready')
                  AND NOT EXISTS (SELECT 1 FROM audit_event AS closed
                    WHERE ((opened.connection_id IS NOT NULL AND closed.connection_id = opened.connection_id
                      AND closed.event_type IN ('proxy_connection_closed', 'peer_connection_closed'))
                      OR (opened.session_id IS NOT NULL AND closed.session_id = opened.session_id
                      AND closed.event_type = 'agent_session_closed')));
                """;
            recover.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            recover.Parameters.AddWithValue("$instance", runtime.InstanceId.ToString());
            await recover.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task RecordAsync(AuditEvent value, CancellationToken cancellationToken = default)
    {
        if (!await capacity.WaitAsync(0, cancellationToken)) throw new AuditUnavailableException("Audit writer capacity exceeded.");
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
                        target_client_id, actor, remote_ip, duration_ms, bytes_to_target, bytes_to_caller, server_instance_id)
                    VALUES ($id, $at, $type, $outcome, $reason, $connection, $session, $client,
                        $channel, $mapping, $caller, $target, $actor, $remote, $duration,
                        $toTarget, $toCaller, $instance)
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
            logger.LogError(exception, "Audit event {EventType} could not be persisted.", value.EventType);
            throw new AuditUnavailableException("Audit storage is unavailable.", exception);
        }
        finally { capacity.Release(); }
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
                   actor, remote_ip, duration_ms, bytes_to_target, bytes_to_caller, server_instance_id
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
                reader.GetInt64(15), reader.GetInt64(16), Guid.Parse(reader.GetString(17))));
        return new AuditPage(total, items);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, DefaultTimeout = 5 }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
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
    private static string? OptionalString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? OptionalGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
}
