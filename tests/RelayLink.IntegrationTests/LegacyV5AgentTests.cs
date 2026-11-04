#if LEGACY_V5_AGENT_TESTS
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RelayLink.Protocol;

namespace RelayLink.IntegrationTests;

public sealed class LegacyV5AgentTests
{
    private static string Legacy => Environment.GetEnvironmentVariable("RELAYLINK_LEGACY_V5_AGENT_ASSEMBLY")
        ?? throw new InvalidOperationException("Set RELAYLINK_LEGACY_V5_AGENT_ASSEMBLY to the checksum-verified unmodified rc.12 Agent DLL.");

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public async Task Real_v5_none_http_forwards_to_the_target_agent(bool oldCaller, bool oldTarget, bool peer)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var upstream = builder.Build();
        upstream.Urls.Add("http://127.0.0.1:0");
        var received = 0;
        upstream.Run(async context =>
        {
            Interlocked.Increment(ref received);
            using var body = new StreamReader(context.Request.Body);
            await context.Response.WriteAsJsonAsync(new
            {
                host = context.Request.Host.Value,
                query = context.Request.QueryString.Value,
                body = await body.ReadToEndAsync(context.RequestAborted)
            }, context.RequestAborted);
        });
        await upstream.StartAsync(token);
        using var fixture = new PeerConnectionTests.Fixture();
        var options = new HttpProxyOptions
        {
            PublicEntryScheme = "http", PeerEntryScheme = "http",
            PublicAllowedAuthorities = [$"127.0.0.1:{fixture.ProxyPort}"],
            PublicAllowedOrigins = [$"http://127.0.0.1:{fixture.ProxyPort}"],
            PeerAuthorityHost = "localhost", PeerAllowedOrigins = ["entry"],
            UpstreamAuthority = "upstream.test", HostMode = "fixed", HostValue = "upstream.test"
        };
        await fixture.StartAsync(callerAssembly: oldCaller ? Legacy : null, targetAssembly: oldTarget ? Legacy : null,
            httpOptions: options, httpTargetPort: new Uri(upstream.Urls.Single()).Port);
        await fixture.WaitOnlineAsync(token);
        using var status = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        while (true)
        {
            var channels = await status.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", token);
            if (channels.GetProperty("channels").EnumerateArray().Where(item => item.GetProperty("applicationProtocol").GetString() == "http")
                .All(item => item.GetProperty("httpState").GetString() == "ready")) break;
            await Task.Delay(50, token);
        }
        var origin = peer ? $"http://localhost:{fixture.LocalPort}" : $"http://127.0.0.1:{fixture.ProxyPort}";
        using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/echo?version=5") { Content = new StringContent("v5-write-once") };
        request.Headers.Add("Origin", origin);
        using var response = await status.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var echo = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("upstream.test", echo.GetProperty("host").GetString());
        Assert.Equal("?version=5", echo.GetProperty("query").GetString());
        Assert.Equal("v5-write-once", echo.GetProperty("body").GetString());
        Assert.Equal(1, Volatile.Read(ref received));
        await VerifyRawMappingAsync(fixture, token);
    }

    [Theory]
    [InlineData("server-peer", true, false)]
    [InlineData("server-peer", false, true)]
    [InlineData("server-peer", true, true)]
    [InlineData("server-jump", false, true)]
    public async Task Real_v5_inspection_is_filtered_or_rejected_without_affecting_none(string mode, bool oldCaller, bool oldTarget)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(callerAssembly: oldCaller ? Legacy : null, targetAssembly: oldTarget ? Legacy : null,
            inspectionMode: mode, peerApplication: "http", jumpApplication: "http");
        await fixture.WaitOnlineAsync(token, requireMappings: false);
        await VerifyRawMappingAsync(fixture, token);
        if (mode == "server-peer" && oldCaller)
        {
            Assert.Throws<InvalidOperationException>(() => fixture.LocalPort);
        }
        else
        {
            var port = mode == "server-peer" ? await fixture.WaitPortAsync("to-visited", token) : fixture.ProxyPort;
            using var rejected = new TcpClient();
            await rejected.ConnectAsync(IPAddress.Loopback, port, token);
            try
            {
                await rejected.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), token);
                Assert.Equal(0, await rejected.GetStream().ReadAsync(new byte[1], token));
            }
            catch (IOException) { }
        }
        Assert.Equal(0, fixture.TargetConnections);
        await VerifyRawMappingAsync(fixture, token);
        using var status = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var overview = await status.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/overview", token);
        Assert.Equal(2, overview.GetProperty("clientsOnline").GetInt32());
    }

    private static async Task VerifyRawMappingAsync(PeerConnectionTests.Fixture fixture, CancellationToken token)
    {
        var port = await fixture.WaitPortAsync("to-visited-b", token);
        using var raw = await fixture.ConnectLocalAsync(port, token);
        var stream = raw.GetStream();
        var payload = Encoding.ASCII.GetBytes("v5-raw");
        await stream.WriteAsync(payload, token);
        raw.Client.Shutdown(SocketShutdown.Send);
        var received = new byte[payload.Length + 1];
        await stream.ReadExactlyAsync(received, token);
        Assert.Equal("Bv5-raw", Encoding.ASCII.GetString(received));
        Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
    }
}
#endif
