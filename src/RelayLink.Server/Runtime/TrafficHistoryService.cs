using System.Text.Json;
using Microsoft.Data.Sqlite;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class TrafficHistoryService(ServerRuntime runtime, MetricsRegistry metrics, ILogger<TrafficHistoryService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Counters> previous = new(StringComparer.Ordinal);
    private bool initialized;
    private DateOnly lastTrimDate;
    private HistoryConfiguration? Settings => runtime.Configuration.Server.History is { Enabled: true } value ? value : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = Settings;
        if (settings is null) return;
        await EnsureInitializedAsync(settings, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Min(settings.SampleIntervalSeconds, 5)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RecordAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Unable to persist traffic history; will retry."); }
        }
    }

    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (settings is null) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(settings, cancellationToken);
            await InitializeAsync(connection, settings, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var minute = now.ToUnixTimeSeconds() / 60 * 60;
            var captured = metrics.Snapshots().Select(item =>
            {
                var value = item.Snapshot;
                return (item.ClientId, item.ChannelId, Value: new Counters(
                    value.BytesToTarget, value.BytesToCaller, value.PeerCiphertextToTarget, value.PeerCiphertextToCaller,
                    value.AcceptedTotal + value.PeerAcceptedTotal, value.OpenedTotal + value.PeerOpenedTotal,
                    value.OpenFailedTotal + value.PeerOpenFailedTotal));
            }).ToArray();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var item in captured)
            {
                previous.TryGetValue(Key(item.ClientId, item.ChannelId), out var baseline);
                var delta = item.Value.Difference(baseline);
                if (!delta.IsZero)
                    await UpsertAsync(connection, (SqliteTransaction)transaction, minute, item.ClientId, item.ChannelId, delta, cancellationToken);
            }
            if (lastTrimDate != DateOnly.FromDateTime(now.UtcDateTime))
            {
                await using var trim = connection.CreateCommand();
                trim.Transaction = (SqliteTransaction)transaction;
                trim.CommandText = "DELETE FROM traffic_minute WHERE minute_utc < $cutoff";
                trim.Parameters.AddWithValue("$cutoff", now.AddDays(-settings.RetentionDays).ToUnixTimeSeconds() / 60 * 60);
                await trim.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            foreach (var item in captured) previous[Key(item.ClientId, item.ChannelId)] = item.Value;
            lastTrimDate = DateOnly.FromDateTime(now.UtcDateTime);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<TrafficHistorySample>> ReadAsync(string? clientId, string? channelId, int hours, CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (settings is null) return [];
        await EnsureInitializedAsync(settings, cancellationToken);
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = connection.CreateCommand();
        var bucketSeconds = hours <= 24 ? 60 : hours <= 168 ? 900 : 3600;
        command.CommandText = """
            SELECT (minute_utc / $bucket) * $bucket, client_id, channel_id,
                   SUM(direct_to_target + peer_to_target), SUM(direct_to_caller + peer_to_caller),
                   SUM(accepted), SUM(opened), SUM(failed), SUM(peer_to_target), SUM(peer_to_caller)
            FROM traffic_minute
            WHERE minute_utc >= $since AND ($client IS NULL OR client_id = $client)
              AND ($channel IS NULL OR channel_id = $channel)
            GROUP BY 1, client_id, channel_id
            ORDER BY 1, client_id, channel_id
            """;
        command.Parameters.AddWithValue("$bucket", bucketSeconds);
        command.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeSeconds() / 60 * 60);
        command.Parameters.AddWithValue("$client", (object?)clientId ?? DBNull.Value);
        command.Parameters.AddWithValue("$channel", (object?)channelId ?? DBNull.Value);
        var samples = new List<TrafficHistorySample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            samples.Add(new TrafficHistorySample(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7))
            { PeerCiphertextToTarget = reader.GetInt64(8), PeerCiphertextToCaller = reader.GetInt64(9) });
        return samples;
    }

    private async Task EnsureInitializedAsync(HistoryConfiguration settings, CancellationToken cancellationToken)
    {
        if (initialized) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return;
            await using var connection = await OpenAsync(settings, cancellationToken);
            await InitializeAsync(connection, settings, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task InitializeAsync(SqliteConnection connection, HistoryConfiguration settings, CancellationToken cancellationToken)
    {
        if (initialized) return;
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS traffic_minute (
                minute_utc INTEGER NOT NULL, client_id TEXT NOT NULL, channel_id TEXT NOT NULL,
                direct_to_target INTEGER NOT NULL, direct_to_caller INTEGER NOT NULL,
                peer_to_target INTEGER NOT NULL, peer_to_caller INTEGER NOT NULL,
                accepted INTEGER NOT NULL, opened INTEGER NOT NULL, failed INTEGER NOT NULL,
                PRIMARY KEY (minute_utc, client_id, channel_id));
            CREATE INDEX IF NOT EXISTS ix_traffic_minute_client_channel_time
                ON traffic_minute (client_id, channel_id, minute_utc);
            CREATE TABLE IF NOT EXISTS history_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken);
        if (Path.GetExtension(settings.FilePath).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) && File.Exists(settings.FilePath))
            await ImportLegacyAsync(connection, settings.FilePath, settings.RetentionDays, cancellationToken);
        initialized = true;
    }

    private static async Task ImportLegacyAsync(SqliteConnection connection, string path, int retentionDays, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM history_meta WHERE key = 'legacy_jsonl_imported'";
        if (await check.ExecuteScalarAsync(cancellationToken) is not null) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var baselines = new Dictionary<string, Counters>(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeSeconds() / 60 * 60;
        using var reader = new StreamReader(path);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            TrafficHistorySample? sample;
            try { sample = JsonSerializer.Deserialize<TrafficHistorySample>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (sample is null) continue;
            var key = Key(sample.ClientId, sample.ChannelId);
            baselines.TryGetValue(key, out var baseline);
            var current = new Counters(sample.BytesToTarget, sample.BytesToCaller, 0, 0,
                sample.AcceptedTotal, sample.OpenedTotal, sample.OpenFailedTotal);
            var delta = current.Difference(baseline);
            if (!delta.IsZero && sample.TimestampUtc.ToUnixTimeSeconds() / 60 * 60 >= cutoff)
                await UpsertAsync(connection, transaction, sample.TimestampUtc.ToUnixTimeSeconds() / 60 * 60,
                    sample.ClientId, sample.ChannelId, delta, cancellationToken);
            baselines[key] = current;
        }
        await using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO history_meta(key, value) VALUES ('legacy_jsonl_imported', '1')";
        await mark.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        long minute, string clientId, string channelId, Counters delta, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO traffic_minute VALUES ($minute, $client, $channel, $directTarget, $directCaller,
                $peerTarget, $peerCaller, $accepted, $opened, $failed)
            ON CONFLICT(minute_utc, client_id, channel_id) DO UPDATE SET
                direct_to_target = direct_to_target + excluded.direct_to_target,
                direct_to_caller = direct_to_caller + excluded.direct_to_caller,
                peer_to_target = peer_to_target + excluded.peer_to_target,
                peer_to_caller = peer_to_caller + excluded.peer_to_caller,
                accepted = accepted + excluded.accepted, opened = opened + excluded.opened,
                failed = failed + excluded.failed
            """;
        command.Parameters.AddWithValue("$minute", minute);
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$channel", channelId);
        command.Parameters.AddWithValue("$directTarget", delta.DirectToTarget);
        command.Parameters.AddWithValue("$directCaller", delta.DirectToCaller);
        command.Parameters.AddWithValue("$peerTarget", delta.PeerToTarget);
        command.Parameters.AddWithValue("$peerCaller", delta.PeerToCaller);
        command.Parameters.AddWithValue("$accepted", delta.Accepted);
        command.Parameters.AddWithValue("$opened", delta.Opened);
        command.Parameters.AddWithValue("$failed", delta.Failed);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SqliteConnection> OpenAsync(HistoryConfiguration settings, CancellationToken cancellationToken)
    {
        var path = DatabasePath(settings.FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public static string DatabasePath(string filePath) => Path.GetFullPath(
        Path.GetExtension(filePath).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(filePath, ".db") : filePath);
    private static string Key(string clientId, string channelId) => $"{clientId}\0{channelId}";

    private readonly record struct Counters(long DirectToTarget, long DirectToCaller, long PeerToTarget,
        long PeerToCaller, long Accepted, long Opened, long Failed)
    {
        public bool IsZero => this == default;
        public Counters Difference(Counters previous) => new(
            Delta(DirectToTarget, previous.DirectToTarget), Delta(DirectToCaller, previous.DirectToCaller),
            Delta(PeerToTarget, previous.PeerToTarget), Delta(PeerToCaller, previous.PeerToCaller),
            Delta(Accepted, previous.Accepted), Delta(Opened, previous.Opened), Delta(Failed, previous.Failed));
        private static long Delta(long current, long baseline) => current >= baseline ? current - baseline : current;
    }
}

public sealed record TrafficHistorySample(DateTimeOffset TimestampUtc, string ClientId, string ChannelId,
    long BytesToTarget, long BytesToCaller, long AcceptedTotal, long OpenedTotal, long OpenFailedTotal)
{
    public long PeerCiphertextToTarget { get; init; }
    public long PeerCiphertextToCaller { get; init; }
}
