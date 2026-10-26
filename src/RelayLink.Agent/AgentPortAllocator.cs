using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace RelayLink.Agent;

/// <summary>Agent-owned loopback port leases, persisted by mapping ID.</summary>
public sealed class AgentPortAllocator
{
    private static readonly object PortGate = new();
    private readonly string path;
    private readonly int first;
    private readonly int last;
    private readonly Dictionary<string, int> saved;

    public AgentPortAllocator(string path, int first, int last)
    {
        if (first is < 1024 or > 65535 || last < first || last > 65535) throw new ArgumentOutOfRangeException(nameof(first));
        this.path = path;
        this.first = first;
        this.last = last;
        saved = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public TcpListener Bind(string mappingId)
    {
        lock (PortGate)
        {
            if (saved.TryGetValue(mappingId, out var preferred) && preferred >= first && preferred <= last && !saved.Any(pair => pair.Key != mappingId && pair.Value == preferred) && TryStart(preferred, out var existing))
                return existing!;

            var count = last - first + 1;
            var offset = RandomNumberGenerator.GetInt32(count);
            for (var index = 0; index < count; index++)
            {
                var port = first + (offset + index) % count;
                if (port == preferred || saved.Any(pair => pair.Key != mappingId && pair.Value == port) || !TryStart(port, out var listener)) continue;
                try
                {
                    saved[mappingId] = port;
                    Persist();
                    return listener!;
                }
                catch
                {
                    listener!.Stop();
                    throw;
                }
            }
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }
    }

    public UdpClient BindUdp(string mappingId)
    {
        lock (PortGate)
        {
            saved.TryGetValue(mappingId, out var preferred);
            var count = last - first + 1;
            var offset = RandomNumberGenerator.GetInt32(count);
            var candidates = Enumerable.Range(0, count).Select(index => first + (offset + index) % count);
            foreach (var port in new[] { preferred }.Concat(candidates).Distinct())
            {
                if (port < first || port > last || saved.Any(pair => pair.Key != mappingId && pair.Value == port)) continue;
                var socket = new UdpClient(AddressFamily.InterNetwork);
                try
                {
                    socket.Client.ExclusiveAddressUse = true;
                    socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }
                catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                { socket.Dispose(); continue; }
                catch { socket.Dispose(); throw; }
                try { saved[mappingId] = port; Persist(); return socket; }
                catch { socket.Dispose(); throw; }
            }
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }
    }

    private static bool TryStart(int port, out TcpListener? listener)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(); return true; }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            listener.Stop();
            listener = null;
            return false;
        }
    }

    private void Persist()
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
