using System.Text.Json;

namespace RelayLink.Agent;

// Operational connection traces stay beside the protected Agent configuration.
// Never include frame payloads, credentials, tokens, or application bytes here.
public sealed class AgentDiagnosticLog(AgentConfiguration configuration)
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(Path.GetDirectoryName(configuration.E2eIdentityPath)!, "relaylink-diagnostics.jsonl");

    public string FilePath => path;

    public async Task WriteAsync(Guid connectionId, string channelId, string phase, long toServerBytes = 0, long toTargetBytes = 0, long elapsedMs = 0, long idleToServerMs = 0, long idleToTargetMs = 0, string? errorType = null)
    {
        var entry = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            connectionId,
            channelId,
            phase,
            toServerBytes,
            toTargetBytes,
            elapsedMs,
            idleToServerMs,
            idleToTargetMs,
            errorType
        }) + "\n";

        try
        {
            await gate.WaitAsync();
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length + entry.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                await File.AppendAllTextAsync(path, entry);
            }
            finally { gate.Release(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never interrupt a business connection.
        }
    }
}
