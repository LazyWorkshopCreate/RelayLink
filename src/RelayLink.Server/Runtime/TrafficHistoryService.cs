using System.Text;
using System.Text.Json;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class TrafficHistoryService(ServerRuntime runtime, MetricsRegistry metrics) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim fileLock = new(1, 1);
    private HistoryConfiguration? Settings => runtime.Configuration.Server.History is { Enabled: true } value ? value : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = Settings;
        if (settings is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(settings.FilePath)!);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.SampleIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RecordAsync(stoppingToken);
    }

    public async Task RecordAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (settings is null) return;
        var samples = runtime.Configuration.Clients.Values.SelectMany(client => client.Channels.Where(channel => !channel.AuthorizedClientsOnly).Select(channel =>
        {
            var snapshot = metrics.For(client.ClientId, channel.ChannelId).Snapshot();
            return new TrafficHistorySample(DateTimeOffset.UtcNow, client.ClientId, channel.ChannelId, snapshot.BytesToTarget, snapshot.BytesToCaller, snapshot.AcceptedTotal, snapshot.OpenedTotal, snapshot.OpenFailedTotal);
        })).ToArray();
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var lines = samples.Select(sample => JsonSerializer.Serialize(sample, JsonOptions));
            await File.AppendAllLinesAsync(settings.FilePath, lines, new UTF8Encoding(false), cancellationToken);
            await TrimAsync(settings, cancellationToken);
        }
        finally { fileLock.Release(); }
    }

    public async Task<IReadOnlyList<TrafficHistorySample>> ReadAsync(string? clientId, string? channelId, int hours, CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (settings is null || !File.Exists(settings.FilePath)) return [];
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            return (await File.ReadAllLinesAsync(settings.FilePath, cancellationToken)).Select(line => TryParse(line)).Where(sample => sample is not null).Select(sample => sample!).Where(sample => sample.TimestampUtc >= since && (clientId is null || sample.ClientId == clientId) && (channelId is null || sample.ChannelId == channelId)).ToArray();
        }
        finally { fileLock.Release(); }
    }

    private static TrafficHistorySample? TryParse(string line) { try { return JsonSerializer.Deserialize<TrafficHistorySample>(line, JsonOptions); } catch (JsonException) { return null; } }
    private static async Task TrimAsync(HistoryConfiguration settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.FilePath)) return;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.RetentionDays);
        var retained = (await File.ReadAllLinesAsync(settings.FilePath, cancellationToken)).Where(line => TryParse(line)?.TimestampUtc >= cutoff).ToArray();
        await File.WriteAllLinesAsync(settings.FilePath, retained, new UTF8Encoding(false), cancellationToken);
    }
}

public sealed record TrafficHistorySample(DateTimeOffset TimestampUtc, string ClientId, string ChannelId, long BytesToTarget, long BytesToCaller, long AcceptedTotal, long OpenedTotal, long OpenFailedTotal);
