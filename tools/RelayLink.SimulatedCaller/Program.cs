using System.Net.Sockets;
using System.Security.Cryptography;

var options = args.Chunk(2).ToDictionary(x => x[0], x => x.Length > 1 ? x[1] : "", StringComparer.OrdinalIgnoreCase);
var host = options.GetValueOrDefault("--host", "127.0.0.1");
var port = int.Parse(options.GetValueOrDefault("--port") ?? throw new ArgumentException("Use --port."));
var connections = int.Parse(options.GetValueOrDefault("--connections", "1"));
var bytes = int.Parse(options.GetValueOrDefault("--bytes", "4096"));
var expectedTag = System.Text.Encoding.UTF8.GetBytes(options.GetValueOrDefault("--expected-tag", ""));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = timeout.Token;
var failures = new List<string>();
await Task.WhenAll(Enumerable.Range(0, connections).Select(async index =>
{
    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, token);
        var payload = RandomNumberGenerator.GetBytes(bytes);
        var stream = client.GetStream();
        await stream.WriteAsync(payload, token);
        var received = new byte[bytes + expectedTag.Length];
        var offset = 0;
        while (offset < bytes)
        {
            var read = await stream.ReadAsync(received.AsMemory(offset), token);
            if (read == 0) throw new IOException("Connection closed early.");
            offset += read;
        }
        if (!received.AsSpan(0, expectedTag.Length).SequenceEqual(expectedTag)) throw new InvalidOperationException("Response channel tag did not match.");
        if (!CryptographicOperations.FixedTimeEquals(payload, received.AsSpan(expectedTag.Length))) throw new InvalidOperationException("Payload mismatch.");
    }
    catch (Exception exception)
    {
        lock (failures) failures.Add($"{index}: {exception.Message}");
    }
}));
if (failures.Count == 0)
{
    Console.WriteLine($"SIM_CALLER_PASS connections={connections} bytes={bytes} endpoint={host}:{port}");
}
else
{
    Console.Error.WriteLine($"SIM_CALLER_FAIL endpoint={host}:{port} succeeded={connections - failures.Count}/{connections}; {string.Join(" | ", failures)}");
    Environment.ExitCode = 1;
}
