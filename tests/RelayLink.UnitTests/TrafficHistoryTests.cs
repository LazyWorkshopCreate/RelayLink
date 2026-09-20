using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class TrafficHistoryTests
{
    [Fact]
    public async Task Sqlite_history_aggregates_direct_and_peer_bytes_once_per_minute_and_trims_expired_rows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"relaylink-history-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "traffic.db");
            var configuration = Configuration(path);
            var metrics = new MetricsRegistry(configuration);
            var counter = metrics.For("agent", "channel");
            counter.AddToTarget(100);
            counter.AddToCaller(200);
            counter.AddPeerCiphertextToTarget(30);
            counter.AddPeerCiphertextToCaller(40);
            counter.PeerAccepted();
            counter.PeerOpened();
            var runtime = new ServerRuntime(configuration, new SessionRegistry());
            using var service = new TrafficHistoryService(runtime, metrics,
                NullLogger<TrafficHistoryService>.Instance);

            await service.RecordAsync(CancellationToken.None);
            await service.RecordAsync(CancellationToken.None);
            counter.AddPeerCiphertextToTarget(7);
            runtime.ReplaceConfiguration(configuration with { Clients = new Dictionary<string, ClientConfiguration>() });
            await service.RecordAsync(CancellationToken.None);

            var samples = await service.ReadAsync("agent", "channel", 24, CancellationToken.None);
            var sample = Assert.Single(samples);
            Assert.Equal(137, sample.BytesToTarget);
            Assert.Equal(240, sample.BytesToCaller);
            Assert.Equal(37, sample.PeerCiphertextToTarget);
            Assert.Equal(40, sample.PeerCiphertextToCaller);
            Assert.Equal(1, sample.AcceptedTotal);
            Assert.Equal(1, sample.OpenedTotal);
            Assert.Equal(0, sample.TimestampUtc.Second);
            Assert.True(File.Exists(path));

            await using var database = new SqliteConnection($"Data Source={path}");
            await database.OpenAsync();
            await using var insert = database.CreateCommand();
            insert.CommandText = """
                INSERT INTO traffic_minute VALUES ($minute, 'agent', 'channel', 1, 1, 0, 0, 0, 0, 0)
                """;
            insert.Parameters.AddWithValue("$minute", DateTimeOffset.UtcNow.AddDays(-91).ToUnixTimeSeconds() / 60 * 60);
            await insert.ExecuteNonQueryAsync();
            // A fresh service performs the daily retention pass on its first record.
            using var restarted = new TrafficHistoryService(new ServerRuntime(configuration, new SessionRegistry()), metrics,
                NullLogger<TrafficHistoryService>.Instance);
            await restarted.RecordAsync(CancellationToken.None);
            await using var count = database.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM traffic_minute";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Legacy_jsonl_is_imported_once_without_deleting_source()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"relaylink-history-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "traffic.jsonl");
            var minute = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60).AddMinutes(-3);
            var lines = new[]
            {
                new TrafficHistorySample(minute, "agent", "channel", 100, 200, 1, 1, 0),
                new TrafficHistorySample(minute.AddMinutes(1), "agent", "channel", 140, 260, 2, 2, 0)
            };
            await File.WriteAllLinesAsync(path, lines.Select(sample => JsonSerializer.Serialize(sample, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
            var configuration = Configuration(path);
            var metrics = new MetricsRegistry(configuration);
            using var service = new TrafficHistoryService(new ServerRuntime(configuration, new SessionRegistry()), metrics,
                NullLogger<TrafficHistoryService>.Instance);
            var imported = await service.ReadAsync("agent", "channel", 24, CancellationToken.None);
            Assert.Equal(new long[] { 100, 40 }, imported.Select(sample => sample.BytesToTarget));
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(TrafficHistoryService.DatabasePath(path)));
            using var restarted = new TrafficHistoryService(new ServerRuntime(configuration, new SessionRegistry()), metrics,
                NullLogger<TrafficHistoryService>.Instance);
            Assert.Equal(2, (await restarted.ReadAsync("agent", "channel", 24, CancellationToken.None)).Count);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static LoadedConfiguration Configuration(string path)
    {
        var channel = new ChannelConfiguration("channel", "Channel", true, "127.0.0.1", 19000,
            "127.0.0.1", 19001, 10, 10);
        var client = new ClientConfiguration(1, "agent", "Agent", true,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 10, 10, [channel]);
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
            "clients", new LimitsConfiguration(100, 20, 10, 10, 20, 120, 300), new HistoryConfiguration(true, path));
        return new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration> { [client.ClientId] = client });
    }
}
