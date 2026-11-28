using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RelayLink.Sdk;

namespace RelayLink.UnitTests;

public sealed class SdkDataPlaneTests
{
    private sealed record ConformanceCase(string Id, JsonElement Input, JsonElement Expect);

    private static Dictionary<string, ConformanceCase> LoadCases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "contract", "conformance", "data-plane.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "contract", "conformance", "data-plane.json")));
        var result = new Dictionary<string, ConformanceCase>(StringComparer.Ordinal);
        foreach (var item in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            result[id] = new ConformanceCase(id, item.GetProperty("input").Clone(), item.GetProperty("expect").Clone());
        }
        return result;
    }

    [Fact]
    public async Task Contract_conformance_cases_match_across_net_sdk()
    {
        var cases = LoadCases();

        var empty = cases["entry-descriptor-rejects-empty-host"];
        var emptyEx = Assert.Throws<RelayLinkException>(() =>
            new EntryDescriptor(empty.Input.GetProperty("host").GetString()!, empty.Input.GetProperty("port").GetInt32()));
        Assert.Equal(empty.Expect.GetProperty("error").GetString(), emptyEx.Code);

        var badPort = cases["entry-descriptor-rejects-port-range"];
        var portEx = Assert.Throws<RelayLinkException>(() =>
            new EntryDescriptor(badPort.Input.GetProperty("host").GetString()!, badPort.Input.GetProperty("port").GetInt32()));
        Assert.Equal(badPort.Expect.GetProperty("error").GetString(), portEx.Code);

        var endpoint = cases["entry-endpoint-string"];
        var desc = new EntryDescriptor(endpoint.Input.GetProperty("host").GetString()!, endpoint.Input.GetProperty("port").GetInt32());
        Assert.Equal(endpoint.Expect.GetProperty("endpoint").GetString(), desc.ToEndpointString());

        var refused = cases["tcp-connect-refused-maps-to-entry-unavailable"];
        var refusedEx = await Assert.ThrowsAsync<RelayLinkException>(() =>
            TcpEntry.ConnectAsync(new EntryDescriptor(refused.Input.GetProperty("host").GetString()!, refused.Input.GetProperty("port").GetInt32()),
                TimeSpan.FromSeconds(5)));
        Assert.Equal(refused.Expect.GetProperty("error").GetString(), refusedEx.Code);

        var tcpMismatch = cases["tcp-rejects-non-tcp-transport"];
        var tcpMismatchEx = await Assert.ThrowsAsync<RelayLinkException>(() =>
            TcpEntry.ConnectAsync(new EntryDescriptor(tcpMismatch.Input.GetProperty("host").GetString()!, tcpMismatch.Input.GetProperty("port").GetInt32(),
                tcpMismatch.Input.GetProperty("transport").GetString()!)));
        Assert.Equal(tcpMismatch.Expect.GetProperty("error").GetString(), tcpMismatchEx.Code);

        var udpMismatch = cases["udp-rejects-non-udp-transport"];
        var udpMismatchEx = await Assert.ThrowsAsync<RelayLinkException>(() =>
            UdpEntry.BindAsync(new EntryDescriptor(udpMismatch.Input.GetProperty("host").GetString()!, udpMismatch.Input.GetProperty("port").GetInt32(),
                udpMismatch.Input.GetProperty("transport").GetString()!)));
        Assert.Equal(udpMismatch.Expect.GetProperty("error").GetString(), udpMismatchEx.Code);

        var oversized = cases["udp-rejects-oversized-datagram"];
        using (var udp = await UdpEntry.BindAsync(new EntryDescriptor("127.0.0.1", 20001, "udp")))
        {
            var oversizedEx = await Assert.ThrowsAsync<RelayLinkException>(() =>
                UdpEntry.SendAsync(udp, new byte[oversized.Input.GetProperty("size").GetInt32()]));
            Assert.Equal(oversized.Expect.GetProperty("error").GetString(), oversizedEx.Code);
        }

        var badDash = cases["discovery-rejects-invalid-dashboard-port"];
        var badDashEx = await Assert.ThrowsAsync<RelayLinkException>(() =>
            EntryDiscovery.DiscoverMappingAsync(badDash.Input.GetProperty("dashboardPort").GetInt32(), "c", "ch"));
        Assert.Equal(badDash.Expect.GetProperty("error").GetString(), badDashEx.Code);

        var unreachable = cases["discovery-unreachable-agent"];
        var unreachableEx = await Assert.ThrowsAsync<RelayLinkException>(() =>
            EntryDiscovery.DiscoverMappingAsync(unreachable.Input.GetProperty("dashboardPort").GetInt32(), "c", "ch"));
        Assert.Equal(unreachable.Expect.GetProperty("error").GetString(), unreachableEx.Code);
    }

    [Fact]
    public async Task EntryDescriptor_ipv6_tcp_half_close_cancellation_and_udp_roundtrip()
    {
        Assert.Equal("[::1]:20001", new EntryDescriptor("::1", 20001).ToEndpointString());
        Assert.Equal("[::1]:20001", new EntryDescriptor("[::1]", 20001).ToEndpointString());
        Assert.Throws<RelayLinkException>(() => new EntryDescriptor("   ", 8080));
        Assert.Throws<RelayLinkException>(() => new EntryDescriptor("127.0.0.1", 8080, "sctp"));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TcpEntry.ConnectAsync(new EntryDescriptor("127.0.0.1", 1), TimeSpan.FromSeconds(5), cancelled.Token));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var reply = Encoding.UTF8.GetBytes("ok:" + Encoding.UTF8.GetString(ms.ToArray()));
            await stream.WriteAsync(reply);
        });

        using var tcp = await TcpEntry.ConnectAsync(new EntryDescriptor("127.0.0.1", port), TimeSpan.FromSeconds(5));
        using var stream = tcp.GetStream();
        await stream.WriteAsync("ping"u8.ToArray());
        TcpEntry.ShutdownWrite(tcp);
        using var replyStream = new MemoryStream();
        await stream.CopyToAsync(replyStream);
        Assert.Equal("ok:ping", Encoding.UTF8.GetString(replyStream.ToArray()));
        await serverTask;

        using var udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udpServer.Client.LocalEndPoint!).Port;
        var udpTask = Task.Run(async () =>
        {
            var r = await udpServer.ReceiveAsync();
            await udpServer.SendAsync(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(r.Buffer).ToUpperInvariant()), r.RemoteEndPoint);
        });
        using var udpClient = await UdpEntry.BindAsync(new EntryDescriptor("127.0.0.1", udpPort, "udp"));
        await UdpEntry.SendAsync(udpClient, "datagram"u8.ToArray());
        var recv = await udpClient.ReceiveAsync();
        Assert.Equal("DATAGRAM", Encoding.UTF8.GetString(recv.Buffer));
        await udpTask;
    }

    [Fact]
    public async Task Discovery_supports_udp_skips_disabled_disambiguates_profile_and_parses_ipv6()
    {
        var json = """
            {
              "servers": [
                {
                  "profileId": "east",
                  "mappings": [
                    { "enabled": false, "targetClientId": "v", "targetChannelId": "u", "localAddress": "127.0.0.1:20999", "transportProtocol": "udp" },
                    { "enabled": true, "targetClientId": "v", "targetChannelId": "dup", "localAddress": "127.0.0.1:21001", "transportProtocol": "tcp" }
                  ]
                },
                {
                  "profileId": "west",
                  "mappings": [
                    { "enabled": true, "targetClientId": "v", "targetChannelId": "u", "localAddress": "[::1]:20002", "transportProtocol": "udp" },
                    { "enabled": true, "targetClientId": "v", "targetChannelId": "dup", "localAddress": "127.0.0.1:22002", "transportProtocol": "tcp" }
                  ]
                }
              ]
            }
            """;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var serveTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                catch { break; }
                using (client)
                {
                    await using var stream = client.GetStream();
                    var reqBuf = new byte[1024];
                    _ = await stream.ReadAsync(reqBuf, cts.Token);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, cts.Token);
                    await stream.WriteAsync(bytes, cts.Token);
                }
            }
        });

        try
        {
            var udp = await EntryDiscovery.DiscoverMappingAsync(port, "v", "u");
            Assert.Equal("udp", udp.Transport);
            Assert.Equal("::1", udp.Host);
            Assert.Equal(20002, udp.Port);
            Assert.Equal("[::1]:20002", udp.ToEndpointString());

            var west = await EntryDiscovery.DiscoverMappingAsync(port, "v", "dup", "west");
            Assert.Equal(22002, west.Port);

            var missingEx = await Assert.ThrowsAsync<RelayLinkException>(() => EntryDiscovery.DiscoverMappingAsync(port, "v", "none"));
            Assert.Equal(RelayLinkErrorCodes.EntryNotFound, missingEx.Code);

            json = "{corrupt";
            var driftEx = await Assert.ThrowsAsync<RelayLinkException>(() => EntryDiscovery.DiscoverMappingAsync(port, "v", "u"));
            Assert.Equal(RelayLinkErrorCodes.ConfigurationDrift, driftEx.Code);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serveTask;
        }
    }
}
