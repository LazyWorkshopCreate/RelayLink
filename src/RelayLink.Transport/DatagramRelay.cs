using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using RelayLink.Protocol;

namespace RelayLink.Transport;

/// <summary>UDP datagrams carried by an authenticated stream; zero length is a datagram, never EOF.</summary>
public static class DatagramRelay
{
    public const int MaxPayloadLength = 65507;
    public const int QueueCapacity = 32;
    public const int SocketSendBufferSize = 128 * 1024;

    public static UdpClient PrepareSocket(UdpClient client)
    {
        try
        {
            // Darwin's default UDP send buffer cannot hold the supported large datagrams.
            client.Client.SendBufferSize = Math.Max(client.Client.SendBufferSize, SocketSendBufferSize);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length > MaxPayloadLength) throw new ProtocolException("UDP payload exceeds limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var count = BinaryPrimitives.ReadInt32BigEndian(header);
        if (count < 0 || count > MaxPayloadLength) throw new ProtocolException("Invalid UDP payload length.");
        var payload = new byte[count];
        await stream.ReadExactlyAsync(payload, token);
        return payload;
    }

    public static async Task RunAsync(Stream stream, Func<CancellationToken, ValueTask<byte[]>> receive,
        Func<byte[], CancellationToken, ValueTask> send, int idleSeconds, CancellationToken token,
        Action<int>? toStream = null, Action<int>? fromStream = null, int writeTimeoutSeconds = 30)
    {
        using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(idleSeconds));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, idle.Token);
        async Task Outgoing()
        {
            while (true)
            {
                var data = await receive(lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(writeTimeoutSeconds));
                await WriteAsync(stream, data, timeout.Token);
                toStream?.Invoke(data.Length);
                idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
            }
        }
        async Task Incoming()
        {
            while (true)
            {
                var data = await ReadAsync(stream, lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(writeTimeoutSeconds));
                await send(data, timeout.Token);
                fromStream?.Invoke(data.Length);
                idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
            }
        }
        var outgoing = Outgoing();
        var incoming = Incoming();
        await Task.WhenAny(outgoing, incoming);
        lifetime.Cancel();
        try { await Task.WhenAll(outgoing, incoming); }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !token.IsCancellationRequested) { }
    }

    public static Task RunTargetAsync(Stream stream, UdpClient target, int idleSeconds, CancellationToken token,
        Action<int>? toStream = null, Action<int>? fromStream = null) =>
        RunAsync(stream, async ct => (await target.ReceiveAsync(ct)).Buffer,
            async (data, ct) => { await target.SendAsync(data, ct); }, idleSeconds, token, toStream, fromStream);

    public static async Task<UdpClient> ConnectTargetAsync(string host, int port, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        var address = addresses.FirstOrDefault(ip => ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            ?? throw new SocketException((int)SocketError.HostNotFound);
        var client = PrepareSocket(new UdpClient(address.AddressFamily));
        try { client.Connect(address, port); return client; }
        catch { client.Dispose(); throw; }
    }
}

/// <summary>One bounded queue per source endpoint. A full queue drops the newest datagram.</summary>
public sealed class DatagramSession(IPEndPoint source, UdpClient listener) : IDisposable
{
    private readonly Channel<byte[]> queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(DatagramRelay.QueueCapacity)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = new();
    public IPEndPoint Source { get; } = source;
    public CancellationToken Token => lifetime.Token;
    public bool Enqueue(byte[] payload) => queue.Writer.TryWrite(payload);
    public async Task RelayAsync(Stream stream, int idleSeconds, CancellationToken token, Action<int>? toTarget = null,
        Action<int>? toCaller = null, int writeTimeoutSeconds = 30)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, Token);
        await DatagramRelay.RunAsync(stream, ct => queue.Reader.ReadAsync(ct),
            async (data, ct) => { await listener.SendAsync(data, Source, ct); },
            idleSeconds, cancellation.Token, toTarget, toCaller, writeTimeoutSeconds);
    }
    public void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() { Cancel(); queue.Writer.TryComplete(); lifetime.Dispose(); }
}

public sealed class DatagramListener(UdpClient listenerSocket)
{
    private readonly UdpClient socket = DatagramRelay.PrepareSocket(listenerSocket);
    public int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    public void Stop() => socket.Dispose();

    public async Task RunAsync(int maxSessions, Func<IPEndPoint, bool> allowed,
        Func<DatagramSession, Task> handle, CancellationToken token)
    {
        var sessions = new Dictionary<IPEndPoint, (DatagramSession Session, Task Task)>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var receive = socket.ReceiveAsync(lifetime.Token).AsTask();
        var tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
        try
        {
            while (true)
            {
                await Task.WhenAny(receive, tick);
                foreach (var entry in sessions.Where(pair => pair.Value.Task.IsCompleted).ToArray())
                {
                    // Observe handler failures, then release exactly once.
                    _ = entry.Value.Task.Exception;
                    sessions.Remove(entry.Key);
                    entry.Value.Session.Dispose();
                }
                if (tick.IsCompleted)
                {
                    await tick;
                    tick = timer.WaitForNextTickAsync(lifetime.Token).AsTask();
                    if (!receive.IsCompleted) continue;
                }
                var packet = await receive;
                receive = socket.ReceiveAsync(lifetime.Token).AsTask();
                if (packet.Buffer.Length > DatagramRelay.MaxPayloadLength || !allowed(packet.RemoteEndPoint)) continue;
                if (!sessions.TryGetValue(packet.RemoteEndPoint, out var current))
                {
                    if (sessions.Count >= maxSessions) continue;
                    var session = new DatagramSession(packet.RemoteEndPoint, socket);
                    session.Enqueue(packet.Buffer);
                    sessions.Add(packet.RemoteEndPoint, (session, handle(session)));
                }
                else current.Session.Enqueue(packet.Buffer);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (token.IsCancellationRequested || socket.Client is null) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            try { await receive; } catch (Exception) { }
            try { await tick; } catch (Exception) { }
            foreach (var current in sessions.Values) current.Session.Cancel();
            foreach (var current in sessions.Values)
            {
                try { await current.Task; } catch (Exception) { }
                current.Session.Dispose();
            }
        }
    }
}
