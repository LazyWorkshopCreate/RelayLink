using System.Net;
using System.Net.Sockets;

var options = args.Chunk(2).ToDictionary(x => x[0], x => x.Length > 1 ? x[1] : "", StringComparer.OrdinalIgnoreCase);
var port = int.Parse(options.GetValueOrDefault("--port") ?? "0");
var tag = System.Text.Encoding.UTF8.GetBytes(options.GetValueOrDefault("--tag", ""));
if (port is < 1 or > 65535) throw new ArgumentException("Use --port <1-65535>.");
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"SIM_TARGET_READY {port}");
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(async () =>
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[32 * 1024];
            var responseStarted = false;
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;
                if (!responseStarted && tag.Length > 0) await stream.WriteAsync(tag);
                responseStarted = true;
                await stream.WriteAsync(buffer.AsMemory(0, read));
            }
        }
    });
}
