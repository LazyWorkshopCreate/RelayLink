using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using RelayLink.Agent;
using RelayLink.Protocol;

namespace RelayLink.IntegrationTests;

public sealed class HttpProxyHostTests
{
    [Fact]
    public async Task Ingress_sends_the_pem_intermediate_chain_without_installing_system_trust()
    {
        await using var fixture = await Fixture.CreateAsync(https: true, intermediateCertificate: true);
        using var response = await fixture.Client.GetAsync("https://panel.test/echo");
        response.EnsureSuccessStatusCode();
        Assert.Equal(1, fixture.Requests);
    }

    [Theory]
    [InlineData("upstream", "upstream.test")]
    [InlineData("preserve", "panel.test")]
    [InlineData("fixed", "fixed.test")]
    public async Task All_host_modes_use_validated_source_and_fixed_destination(string mode, string expected)
    {
        await using var fixture = await Fixture.CreateAsync(hostMode: mode, fixedHost: "fixed.test");
        using var response = await fixture.Client.GetAsync("http://panel.test/echo");
        var echo = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal(expected, echo!["host"]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("http://panel.test/path")]
    [InlineData("http://panel.test,http://evil.test")]
    public async Task Malformed_or_null_origins_do_not_reach_upstream(string origin)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://panel.test/echo");
        request.Headers.Add("Origin", origin);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task Browser_does_not_accept_an_untrusted_ingress_certificate()
    {
        await using var fixture = await Fixture.CreateAsync(https: true, trustEntry: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.GetAsync("https://panel.test/echo"));
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_https_upstream_validates_trust_and_name_and_supports_websocket(bool httpsEntry)
    {
        await using var fixture = await Fixture.CreateAsync(httpsEntry, httpsUpstream: true);
        using var response = await fixture.Client.GetAsync(fixture.Origin + "/echo");
        response.EnsureSuccessStatusCode();
        var echo = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("upstream.test", echo!["host"]);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("echo"); socket.Options.SetRequestHeader("Origin", fixture.Origin);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri((httpsEntry ? "wss" : "ws") + "://panel.test/ws"), fixture.Client, deadline.Token);
        await socket.SendAsync(new byte[] { 9 }, WebSocketMessageType.Binary, true, deadline.Token);
        var bytes = new byte[1]; Assert.Equal(1, (await socket.ReceiveAsync(bytes, deadline.Token)).Count);
        Assert.Equal((byte)9, bytes[0]);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
    }

    [Theory]
    [InlineData(false, "upstream.test")]
    [InlineData(true, "wrong.test")]
    public async Task Unknown_ca_or_wrong_upstream_name_is_rejected_before_http_delivery(bool trusted, string name)
    {
        await using var fixture = await Fixture.CreateAsync(httpsUpstream: true, trustUpstream: trusted, upstreamAuthority: name);
        using var response = await fixture.Client.GetAsync("http://panel.test/echo");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task Request_body_limit_and_websocket_origin_are_enforced()
    {
        await using var fixture = await Fixture.CreateAsync(maxBody: 8);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://panel.test/echo") { Content = new StringContent(new string('x', 32)) };
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var socket = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri("ws://panel.test/ws"), fixture.Client, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delivered_request_is_not_replayed_when_upstream_aborts(bool post)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, "http://panel.test/abort");
        if (post) request.Content = new StringContent("write-once");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task Virtual_ingress_rewrites_only_allowed_origins_and_discards_forged_forwarding_headers()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var good = new HttpRequestMessage(HttpMethod.Get, "http://panel.test/echo?key=value");
        good.Headers.Add("Origin", "http://panel.test");
        good.Headers.Add("X-Forwarded-For", "198.51.100.99");
        good.Headers.Add("Forwarded", "for=198.51.100.99");
        using var response = await fixture.Client.SendAsync(good);
        response.EnsureSuccessStatusCode();
        var echo = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("upstream.test", echo!["host"]);
        Assert.Equal("http://localhost", echo["origin"]);
        Assert.Equal("192.0.2.10", echo["for"]);
        Assert.Equal("panel.test", echo["forwardedHost"]);
        Assert.Equal("http", echo["proto"]);
        Assert.Equal(string.Empty, echo["forwarded"]);
        Assert.Equal("?key=value", echo["query"]);
        Assert.Equal(1, fixture.Requests);

        using var bad = new HttpRequestMessage(HttpMethod.Post, "http://panel.test/echo");
        bad.Headers.Add("Origin", "http://evil.test");
        using var rejected = await fixture.Client.SendAsync(bad);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(1, fixture.Requests);

        using var wrongHost = await fixture.Client.GetAsync("http://evil.test/echo");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, wrongHost.StatusCode);
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task Missing_origin_is_not_added_and_revocation_closes_the_stream()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.Client.GetAsync("http://panel.test/echo");
        var echo = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal(string.Empty, echo!["origin"]);
        fixture.Host.Revoke();
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.GetAsync("http://panel.test/echo"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Websocket_and_streaming_work_through_virtual_ingress(bool https)
    {
        await using var fixture = await Fixture.CreateAsync(https);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("echo");
        socket.Options.SetRequestHeader("Origin", fixture.Origin);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri((https ? "wss" : "ws") + "://panel.test/ws"), fixture.Client, deadline.Token);
        Assert.Equal("echo", socket.SubProtocol);
        await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, deadline.Token);
        var bytes = new byte[8];
        var received = await socket.ReceiveAsync(bytes, deadline.Token);
        Assert.Equal(3, received.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes[..3]);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        using var response = await fixture.Client.GetAsync(fixture.Origin + "/stream", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
        await body.ReadExactlyAsync(bytes.AsMemory(0, 3), deadline.Token);
        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(bytes[..3]));
        fixture.ReleaseStream.TrySetResult();
        await body.ReadExactlyAsync(bytes.AsMemory(0, 3), deadline.Token);
        Assert.Equal("two", System.Text.Encoding.UTF8.GetString(bytes[..3]));
    }

    [Fact]
    public async Task Empty_origin_allowlist_rejects_websocket_but_still_forwards_ordinary_requests()
    {
        await using var fixture = await Fixture.CreateAsync();
        var field = typeof(HttpProxyHost).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic);
        var current = (HttpProxyOptions)field!.GetValue(fixture.Host)!;
        field.SetValue(fixture.Host, current with { PublicAllowedOrigins = [] });
        var before = fixture.Requests;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://evil.test");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri("ws://panel.test/ws"), fixture.Client, CancellationToken.None));
        using var sameOrigin = new ClientWebSocket();
        sameOrigin.Options.SetRequestHeader("Origin", fixture.Origin);
        await Assert.ThrowsAsync<WebSocketException>(() => sameOrigin.ConnectAsync(new Uri("ws://panel.test/ws"), fixture.Client, CancellationToken.None));
        Assert.Equal(before, fixture.Requests);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://panel.test/echo");
        request.Headers.Add("Origin", "http://evil.test");
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal(before + 1, fixture.Requests);
    }

    [Theory]
    [InlineData("websocket, foo", "http://evil.test")]
    [InlineData("foo, websocket", null)]
    [InlineData(" websocket ", null)]
    public async Task Upgrade_variants_cannot_bypass_the_websocket_origin_check(string upgrade, string? origin)
    {
        await using var fixture = await Fixture.CreateAsync();
        var (status, _) = await fixture.SendUpgradeAsync(upgrade, origin);
        Assert.Equal(403, status);
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task Upgrade_variant_with_allowed_origin_uses_the_websocket_activity_timeout()
    {
        await using var fixture = await Fixture.CreateAsync(activityTimeoutSeconds: 30, webSocketActivityTimeoutSeconds: 1);
        var started = Stopwatch.GetTimestamp();
        var (status, elapsed) = await fixture.SendUpgradeAsync("websocket, foo", fixture.Origin, "/hold");
        Assert.NotEqual(403, status);
        Assert.Equal(1, fixture.Requests);
        Assert.InRange(elapsed.TotalSeconds, 0.4, 6);
    }

    [Fact]
    public async Task Idle_websocket_is_closed_by_the_activity_timeout()
    {
        await using var fixture = await Fixture.CreateAsync(activityTimeoutSeconds: 30, webSocketActivityTimeoutSeconds: 1);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("echo");
        socket.Options.SetRequestHeader("Origin", fixture.Origin);
        var started = Stopwatch.GetTimestamp();
        await socket.ConnectAsync(new Uri("ws://panel.test/ws"), fixture.Client, CancellationToken.None);
        var receive = socket.ReceiveAsync(new byte[8], CancellationToken.None);
        var completed = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(receive, completed);
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.InRange(elapsed.TotalSeconds, 0.4, 8);
        await Assert.ThrowsAnyAsync<WebSocketException>(() => receive);
    }

    [Fact]
    public async Task WebSocket_cap_leaves_room_for_an_ordinary_request()
    {
        await using var fixture = await Fixture.CreateAsync(maxConnections: 2, maxWebSocketConnections: 1);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("echo");
        socket.Options.SetRequestHeader("Origin", fixture.Origin);
        await socket.ConnectAsync(new Uri("ws://panel.test/ws"), fixture.Client, CancellationToken.None);
        var (status, _) = await fixture.SendUpgradeAsync("websocket", fixture.Origin);
        Assert.Equal(503, status);
        using var response = await fixture.Client.GetAsync("http://panel.test/echo");
        response.EnsureSuccessStatusCode();
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Client_supplied_address_headers_are_stripped_and_the_strip_list_is_configurable()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://panel.test/echo");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "198.51.100.20");
        request.Headers.TryAddWithoutValidation("True-Client-IP", "198.51.100.21");
        request.Headers.TryAddWithoutValidation("X-Client-IP", "198.51.100.22");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "198.51.100.23");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.99");
        request.Headers.TryAddWithoutValidation("X-Request-Id", "keep-me");
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var echo = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal(string.Empty, echo!["realIp"]);
        Assert.Equal(string.Empty, echo["trueClient"]);
        Assert.Equal(string.Empty, echo["clientIp"]);
        Assert.Equal(string.Empty, echo["cfIp"]);
        Assert.Equal("192.0.2.10", echo["for"]);
        Assert.Equal("keep-me", echo["requestId"]);

        await using var custom = await Fixture.CreateAsync(stripRequestHeaders: ["X-Request-Id"]);
        using var customRequest = new HttpRequestMessage(HttpMethod.Get, "http://panel.test/echo");
        customRequest.Headers.TryAddWithoutValidation("X-Real-IP", "198.51.100.20");
        customRequest.Headers.TryAddWithoutValidation("X-Request-Id", "drop-me");
        using var customResponse = await custom.Client.SendAsync(customRequest);
        var customEcho = await customResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal(string.Empty, customEcho!["realIp"]);
        Assert.Equal(string.Empty, customEcho["requestId"]);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public HttpProxyHost Host = null!;
        public HttpClient Client = null!;
        public int Requests;
        public string Origin = "http://panel.test";
        public readonly TaskCompletionSource ReleaseStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string resources = Path.Combine(Path.GetTempPath(), "relaylink-http-" + Guid.NewGuid().ToString("N"));
        private X509Certificate2? root;
        private X509Certificate2? upstreamCertificate;
        private WebApplication upstream = null!;
        private readonly TcpListener ingress = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
        private readonly List<Task> connections = [];

        public static async Task<Fixture> CreateAsync(bool https = false, long maxBody = 30 * 1024 * 1024, bool httpsUpstream = false, bool trustUpstream = true, string upstreamAuthority = "upstream.test",
            string hostMode = "fixed", string? fixedHost = null, bool trustEntry = true, bool intermediateCertificate = false,
            int maxConnections = 10, int activityTimeoutSeconds = 120, int webSocketActivityTimeoutSeconds = 300, int maxWebSocketConnections = 0,
            IReadOnlyList<string>? stripRequestHeaders = null)
        {
            var fixture = new Fixture();
            if (https || httpsUpstream)
            {
                if (https) fixture.Origin = "https://panel.test";
                Directory.CreateDirectory(fixture.resources);
                using var rootKey = RSA.Create(2048);
                var rootRequest = new CertificateRequest("CN=RelayLink Test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                fixture.root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=panel.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("panel.test"); san.AddDnsName("upstream.test"); request.CertificateExtensions.Add(san.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                using var intermediateKey = RSA.Create(2048);
                var intermediateRequest = new CertificateRequest("CN=RelayLink Test Intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                using var intermediate = intermediateRequest.Create(fixture.root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2), RandomNumberGenerator.GetBytes(16));
                using var signer = intermediate.CopyWithPrivateKey(intermediateKey);
                using var certificate = request.Create(intermediateCertificate ? signer : fixture.root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
                await File.WriteAllTextAsync(Path.Combine(fixture.resources, "test.crt.pem"), certificate.ExportCertificatePem() + (intermediateCertificate ? "\n" + intermediate.ExportCertificatePem() : string.Empty));
                await File.WriteAllTextAsync(Path.Combine(fixture.resources, "test.key.pem"), key.ExportPkcs8PrivateKeyPem());
                await File.WriteAllTextAsync(Path.Combine(fixture.resources, "test.ca.pem"), fixture.root.ExportCertificatePem());
                if (httpsUpstream)
                {
                    using var withKey = certificate.CopyWithPrivateKey(key);
                    fixture.upstreamCertificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null,
                        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet);
                }
            }
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            if (httpsUpstream) builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(fixture.upstreamCertificate!)));
            fixture.upstream = builder.Build();
            if (!httpsUpstream) fixture.upstream.Urls.Add("http://127.0.0.1:0");
            fixture.upstream.UseWebSockets();
            fixture.upstream.Run(async context =>
            {
                Interlocked.Increment(ref fixture.Requests);
                if (context.Request.Path == "/abort")
                {
                    await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                    context.Abort(); return;
                }
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var socket = await context.WebSockets.AcceptWebSocketAsync("echo");
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                        if (received.MessageType == WebSocketMessageType.Close) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted); return; }
                        await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
                    }
                }
                if (context.Request.Path == "/hold")
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                    return;
                }
                if (context.Request.Path == "/stream")
                {
                    context.Response.ContentType = "text/event-stream";
                    await context.Response.WriteAsync("one"); await context.Response.Body.FlushAsync();
                    await fixture.ReleaseStream.Task.WaitAsync(context.RequestAborted);
                    await context.Response.WriteAsync("two"); return;
                }
                await context.Response.WriteAsJsonAsync(new Dictionary<string, string>
                {
                    ["host"] = context.Request.Host.Value!, ["origin"] = context.Request.Headers.Origin.ToString(),
                    ["for"] = context.Request.Headers["X-Forwarded-For"].ToString(),
                    ["forwardedHost"] = context.Request.Headers["X-Forwarded-Host"].ToString(),
                    ["proto"] = context.Request.Headers["X-Forwarded-Proto"].ToString(),
                    ["forwarded"] = context.Request.Headers["Forwarded"].ToString(), ["query"] = context.Request.QueryString.Value ?? string.Empty,
                    ["realIp"] = context.Request.Headers["X-Real-IP"].ToString(),
                    ["trueClient"] = context.Request.Headers["True-Client-IP"].ToString(),
                    ["clientIp"] = context.Request.Headers["X-Client-IP"].ToString(),
                    ["cfIp"] = context.Request.Headers["CF-Connecting-IP"].ToString(),
                    ["requestId"] = context.Request.Headers["X-Request-Id"].ToString()
                });
            });
            await fixture.upstream.StartAsync(fixture.lifetime.Token);
            var port = new Uri(fixture.upstream.Urls.Single()).Port;
            var options = new HttpProxyOptions
            {
                PublicEntryScheme = https ? "https" : "http", CertificateRef = https ? "test" : null, PublicAllowedAuthorities = ["panel.test"], PublicAllowedOrigins = [fixture.Origin],
                MaxRequestBodyBytes = maxBody, UpstreamScheme = httpsUpstream ? "https" : "http", UpstreamTrustRef = httpsUpstream && trustUpstream ? "test" : null, UpstreamAuthority = upstreamAuthority,
                HostMode = hostMode, HostValue = hostMode == "fixed" ? fixedHost ?? upstreamAuthority : null, OriginMode = "fixed", OriginValue = "http://localhost",
                ActivityTimeoutSeconds = activityTimeoutSeconds, WebSocketActivityTimeoutSeconds = webSocketActivityTimeoutSeconds, MaxWebSocketConnections = maxWebSocketConnections,
                StripRequestHeaders = stripRequestHeaders ?? HttpProxyPolicy.DefaultStripRequestHeaders
            };
            var channel = new ChannelSnapshot("web", "web", true, "127.0.0.1", port, maxConnections, 5) { ApplicationProtocol = "http", Http = options };
            fixture.Host = await HttpProxyHost.CreateAsync(channel, fixture.resources, fixture.lifetime.Token);
            fixture.ingress.Start();
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(fixture.ingress.LocalEndpoint, token);
                    var accepted = await fixture.ingress.AcceptSocketAsync(token);
                    var task = fixture.Host.ServeAsync(new NetworkStream(accepted, true), new HttpIngressContext("public", https ? "https" : "http", "192.0.2.10"), fixture.lifetime.Token);
                    fixture.connections.Add(task);
                    return new NetworkStream(socket, true);
                }
            };
            if (fixture.root is not null && trustEntry)
            {
                handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
                handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(fixture.root);
            }
            fixture.Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            return fixture;
        }

        public async Task<Stream> OpenRawAsync()
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(ingress.LocalEndpoint);
            var accepted = await ingress.AcceptSocketAsync(lifetime.Token);
            connections.Add(Host.ServeAsync(new NetworkStream(accepted, true), new HttpIngressContext("public", Origin.StartsWith("https", StringComparison.Ordinal) ? "https" : "http", "192.0.2.10"), lifetime.Token));
            return new NetworkStream(socket, true);
        }

        public async Task<(int Status, TimeSpan Elapsed)> SendUpgradeAsync(string upgrade, string? origin, string path = "/ws")
        {
            var started = Stopwatch.GetTimestamp();
            await using var stream = await OpenRawAsync();
            var originLine = origin is null ? string.Empty : $"Origin: {origin}\r\n";
            var payload = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: panel.test\r\nConnection: Upgrade\r\nUpgrade: {upgrade}\r\n{originLine}Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
            await stream.WriteAsync(payload);
            using var readLimit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var buffer = new byte[512];
            var read = 0;
            try
            {
                while (read < buffer.Length && buffer.AsSpan(0, read).IndexOf("\r\n"u8) < 0)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(read), readLimit.Token);
                    if (count == 0) break;
                    read += count;
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
            }

            return (ParseStatusCode(buffer.AsSpan(0, read)), Stopwatch.GetElapsedTime(started));
        }

        private static int ParseStatusCode(ReadOnlySpan<byte> data)
        {
            var lineEnd = data.IndexOf("\r\n"u8);
            if (lineEnd < 12) return 0;
            var line = data[..lineEnd];
            return line.StartsWith("HTTP/1.1 "u8) && int.TryParse(line.Slice(9, 3), out var code) ? code : 0;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); lifetime.Cancel(); ingress.Stop();
            await Host.DisposeAsync();
            await upstream.StopAsync(); await upstream.DisposeAsync();
            foreach (var task in connections) try { await task; } catch (OperationCanceledException) { } catch (IOException) { }
            lifetime.Dispose(); root?.Dispose(); upstreamCertificate?.Dispose();
            if (Directory.Exists(resources))
            {
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(resources), StringComparison.OrdinalIgnoreCase);
                Directory.Delete(resources, true);
            }
        }
    }
}
