using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RelayLink.Protocol;

namespace RelayLink.IntegrationTests;

public sealed class HttpProxyProcessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Real_server_and_agents_forward_http_https_and_websocket_with_verified_headers(bool peer, bool https)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using var upstream = builder.Build();
        upstream.Urls.Add("http://127.0.0.1:0");
        upstream.UseWebSockets();
        var requests = 0;
        var releaseStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Run(async context =>
        {
            Interlocked.Increment(ref requests);
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync("echo");
                var bytes = new byte[4096];
                while (true)
                {
                    var received = await socket.ReceiveAsync(bytes, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted); return;
                    }
                    await socket.SendAsync(bytes.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
                }
            }
            if (context.Request.Path == "/stream")
            {
                await context.Response.WriteAsync("one"); await context.Response.Body.FlushAsync();
                await releaseStream.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsync("two"); return;
            }
            using var body = new StreamReader(context.Request.Body);
            await context.Response.WriteAsJsonAsync(new Dictionary<string, string>
            {
                ["host"] = context.Request.Host.Value!, ["origin"] = context.Request.Headers.Origin.ToString(),
                ["for"] = context.Request.Headers["X-Forwarded-For"].ToString(),
                ["forwarded"] = context.Request.Headers["Forwarded"].ToString(),
                ["proto"] = context.Request.Headers["X-Forwarded-Proto"].ToString(),
                ["query"] = context.Request.QueryString.Value ?? string.Empty,
                ["body"] = await body.ReadToEndAsync(context.RequestAborted)
            });
        });
        await upstream.StartAsync(token);
        using var fixture = new PeerConnectionTests.Fixture();
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=HTTP Process Test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=panel.test", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("panel.test"); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(san.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var leaf = leafRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        var scheme = https ? "https" : "http";
        var publicOrigin = $"{scheme}://panel.test:{fixture.ProxyPort}";
        var options = new HttpProxyOptions
        {
            PublicEntryScheme = scheme, PeerEntryScheme = scheme, CertificateRef = https ? "panel" : null,
            PublicAllowedAuthorities = [$"panel.test:{fixture.ProxyPort}"], PublicAllowedOrigins = [publicOrigin],
            PeerAuthorityHost = "localhost", PeerAllowedOrigins = ["entry"], UpstreamAuthority = "upstream.test",
            HostMode = "fixed", HostValue = "upstream.test", OriginMode = "fixed", OriginValue = "http://localhost"
        };
        await fixture.StartAsync(httpOptions: options, httpTargetPort: new Uri(upstream.Urls.Single()).Port,
            prepareHttpResources: resources =>
            {
                Directory.CreateDirectory(resources);
                File.WriteAllText(Path.Combine(resources, "panel.crt.pem"), leaf.ExportCertificatePem());
                File.WriteAllText(Path.Combine(resources, "panel.key.pem"), leafKey.ExportPkcs8PrivateKeyPem());
            });
        await fixture.WaitOnlineAsync(token);
        var port = peer ? fixture.LocalPort : fixture.ProxyPort;
        var origin = peer ? $"{scheme}://localhost:{port}" : publicOrigin;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, port, cancellation); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(root);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/echo?item=1") { Content = new StringContent("write-once") };
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Forwarded-For", "198.51.100.99");
        request.Headers.Add("Forwarded", "for=198.51.100.99");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var echo = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(token);
        Assert.Equal("upstream.test", echo!["host"]);
        Assert.Equal("http://localhost", echo["origin"]);
        Assert.Equal(peer ? string.Empty : "127.0.0.1", echo["for"]);
        Assert.Equal(string.Empty, echo["forwarded"]);
        Assert.Equal(scheme, echo["proto"]);
        Assert.Equal("?item=1", echo["query"]);
        Assert.Equal("write-once", echo["body"]);
        Assert.Equal(1, Volatile.Read(ref requests));
        using var bad = new HttpRequestMessage(HttpMethod.Post, origin + "/echo"); bad.Headers.Add("Origin", "http://evil.test");
        using var rejected = await client.SendAsync(bad, token);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(1, Volatile.Read(ref requests));
        using var missing = await client.GetAsync(origin + "/echo", token);
        var missingEcho = await missing.Content.ReadFromJsonAsync<Dictionary<string, string>>(token);
        Assert.Equal(string.Empty, missingEcho!["origin"]);

        using var websocket = new ClientWebSocket();
        websocket.Options.AddSubProtocol("echo"); websocket.Options.SetRequestHeader("Origin", origin);
        await websocket.ConnectAsync(new Uri((https ? "wss" : "ws") + origin[scheme.Length..] + "/ws"), client, token);
        Assert.Equal("echo", websocket.SubProtocol);
        await websocket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, token);
        var buffer = new byte[8];
        var read = await websocket.ReceiveAsync(buffer, token);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..read.Count]);
        await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", token);
        using var streaming = await client.GetAsync(origin + "/stream", HttpCompletionOption.ResponseHeadersRead, token);
        await using var stream = await streaming.Content.ReadAsStreamAsync(token);
        await stream.ReadExactlyAsync(buffer.AsMemory(0, 3), token);
        Assert.Equal("one", Encoding.UTF8.GetString(buffer[..3]));
        releaseStream.SetResult();
        await stream.ReadExactlyAsync(buffer.AsMemory(0, 3), token);
        Assert.Equal("two", Encoding.UTF8.GetString(buffer[..3]));
        using var statusClient = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var channels = await statusClient.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", token);
        var web = channels.GetProperty("channels").EnumerateArray().Single(item => item.GetProperty("channelId").GetString() == (peer ? "private" : "web-public"));
        Assert.Equal("http", web.GetProperty("applicationProtocol").GetString());
        Assert.Equal(scheme, web.GetProperty("entryScheme").GetString());
        Assert.Equal("ready", web.GetProperty("httpState").GetString());
        var mappings = await statusClient.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/clients/caller/mappings", token);
        var mapping = mappings.GetProperty("mappings").EnumerateArray().Single(item => item.GetProperty("mappingId").GetString() == "to-visited");
        Assert.True(mapping.GetProperty("available").GetBoolean());
        Assert.Equal($"{scheme}://localhost:{fixture.LocalPort}", mapping.GetProperty("accessUrl").GetString());
        var localStatus = await statusClient.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{fixture.AgentDashboardPort}/api/v2/status", token);
        var localMapping = localStatus.GetProperty("servers")[0].GetProperty("mappings").EnumerateArray().Single(item => item.GetProperty("mappingId").GetString() == "to-visited");
        Assert.Equal(mapping.GetProperty("accessUrl").GetString(), localMapping.GetProperty("accessUrl").GetString());
        Assert.DoesNotContain(fixture.CallerSecret, fixture.Logs(), StringComparison.Ordinal);
        Assert.DoesNotContain("write-once", fixture.Logs(), StringComparison.Ordinal);
        Assert.DoesNotContain("item=1", fixture.Logs(), StringComparison.Ordinal);
        await stream.DisposeAsync();
        streaming.Dispose();
        websocket.Dispose();
        client.Dispose();
        while (true)
        {
            channels = await statusClient.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", token);
            if (channels.GetProperty("channels").EnumerateArray().All(item => item.GetProperty("activeConnections").GetInt32() == 0 && item.GetProperty("pendingConnections").GetInt32() == 0)) break;
            await Task.Delay(50, token);
        }
        await upstream.StopAsync(token);
    }
}
