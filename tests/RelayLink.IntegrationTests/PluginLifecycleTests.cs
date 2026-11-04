using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayLink.Server.Configuration;
using RelayLink.Testing;

namespace RelayLink.IntegrationTests;

#if PEER_TLS_TESTS
public sealed class PluginLifecycleTests
{
    [Theory]
    [InlineData("server-peer", false)]
    [InlineData("server-jump", false)]
    [InlineData("server-peer", true)]
    [InlineData("server-jump", true)]
    public async Task Revocation_during_approval_releases_pending_without_opening_target(string mode, bool changeConfiguration)
    {
        var policy = new InspectionPolicy { ApprovalTimeoutSeconds = 30, Rules = [new() { Action = "approve" }] };
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: mode, peerApplication: "http", peerInspection: policy,
            jumpApplication: "http", jumpInspection: policy, openTimeoutSeconds: 2, maxPendingConnections: 1, tcpIdleTimeoutSeconds: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await fixture.WaitOnlineAsync(deadline.Token);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort, deadline.Token);
        await caller.GetStream().WriteAsync("GET /waiting HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), deadline.Token);
        var channel = mode == "server-peer" ? "private" : "jump";
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri("http://" + fixture.DashboardAddress) };
        using var login = await http.PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = "test-password" }, deadline.Token);
        login.EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Add("X-RelayLink-CSRF", (await login.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("csrfToken").GetString());
        var path = $"/api/v1/clients/visited/channels/{channel}/connections";
        JsonElement connections;
        do
        {
            connections = (await http.GetFromJsonAsync<JsonElement>(path, deadline.Token)).GetProperty("connections");
            if (connections.GetArrayLength() == 0) await Task.Delay(50, deadline.Token);
        } while (connections.GetArrayLength() == 0);
        while (true)
        {
            var audit = await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?hours=1&eventType=inspection_decision&page=1&pageSize=100", deadline.Token);
            if (audit.GetProperty("events").EnumerateArray().Any(item => item.GetProperty("outcome").GetString() == "waiting")) break;
            await Task.Delay(50, deadline.Token);
        }
        if (changeConfiguration)
        {
            var channels = (await http.GetFromJsonAsync<JsonElement>("/api/v1/clients/visited/channels", deadline.Token)).GetProperty("channels");
            var current = channels.EnumerateArray().Single(item => item.GetProperty("channelId").GetString() == channel);
            using var changed = await http.PutAsJsonAsync($"/api/v1/admin/clients/visited/channels/{channel}", new
            {
                displayName = "Changed", enabled = true, listenAddress = current.GetProperty("listenAddress").GetString(), listenPort = current.GetProperty("listenPort").GetInt32(),
                targetHost = current.GetProperty("targetHost").GetString(), targetPort = current.GetProperty("targetPort").GetInt32(),
                maxConnections = current.GetProperty("maxConnections").GetInt32(), targetConnectTimeoutSeconds = current.GetProperty("targetConnectTimeoutSeconds").GetInt32() + 1
            }, deadline.Token);
            Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync(deadline.Token));
        }
        else
        {
            var id = connections[0].GetProperty("connectionId").GetString();
            using var disconnected = await http.DeleteAsync($"/api/v1/admin/clients/visited/channels/{channel}/connections/{id}", deadline.Token);
            disconnected.EnsureSuccessStatusCode();
        }
        using var reclaimed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = await http.GetFromJsonAsync<JsonElement>("/api/v1/clients/visited/channels", reclaimed.Token);
            var counters = snapshot.GetProperty("channels").EnumerateArray().Single(item => item.GetProperty("channelId").GetString() == channel);
            if (counters.GetProperty("pendingConnections").GetInt32() == 0 && counters.GetProperty("activeConnections").GetInt32() == 0) break;
            await Task.Delay(50, reclaimed.Token);
        }
        Assert.Equal(0, fixture.TargetConnections);
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>(path, deadline.Token)).GetProperty("connections").EnumerateArray());
    }

    [Theory]
    [InlineData("server-peer")]
    [InlineData("server-jump")]
    public async Task Approval_on_active_connection_outlives_business_idle_without_forwarding_second_request(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var captured = new List<string>();
        var serving = Task.Run(async () =>
        {
            using var accepted = await target.AcceptTcpClientAsync(deadline.Token);
            var stream = accepted.GetStream();
            captured.Add(await ReadHeaderAsync(stream, deadline.Token));
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(), deadline.Token);
            try
            {
                var bytes = new byte[1024];
                var count = await stream.ReadAsync(bytes, deadline.Token);
                if (count > 0) captured.Add(Encoding.ASCII.GetString(bytes, 0, count));
            }
            catch (IOException) { }
        });
        var policy = new InspectionPolicy { ApprovalTimeoutSeconds = 5,
            Rules = [new InspectionRule { PathPrefix = "/approval", Action = "approve" }] };
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: mode, peerApplication: "http", peerTargetPort: targetPort, peerInspection: policy,
            jumpApplication: "http", jumpTargetPort: targetPort, jumpInspection: policy, openTimeoutSeconds: 2, tcpIdleTimeoutSeconds: 1);
        await fixture.WaitOnlineAsync(deadline.Token);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort, deadline.Token);
        var callerStream = caller.GetStream();
        await callerStream.WriteAsync("GET /health HTTP/1.1\r\nHost: example.test\r\n\r\nGET /approval HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), deadline.Token);
        Assert.Contains("200", await ReadHeaderAsync(callerStream, deadline.Token));
        var body = new byte[2];
        await callerStream.ReadExactlyAsync(body, deadline.Token);
        Assert.Equal("ok", Encoding.ASCII.GetString(body));
        var watch = Stopwatch.StartNew();
        Assert.Contains("403", await ReadHeaderAsync(callerStream, deadline.Token));
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(4), $"Active approval ended after {watch.Elapsed}.");
        caller.Close();
        await serving;
        Assert.Single(captured);
        Assert.DoesNotContain("/approval", captured[0]);
    }

    [Theory]
    [InlineData("server-peer", "http", 40, 100, 2)]
    [InlineData("server-jump", "http", 40, 100, 2)]
    [InlineData("server-peer", "https", 40, 100, 2)]
    [InlineData("server-jump", "https", 40, 100, 2)]
    [InlineData("server-peer", "http", 30, 1000, 20)]
    public async Task Active_inspection_response_outlives_open_deadline(string mode, string application, int bodyLength, int intervalMilliseconds, int openSeconds)
    {
        using var directory = new TestDirectory("relaylink-plugin-tls-");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var upstreamCertificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        var authorityPath = Path.Combine(directory.Path, "upstream-ca.pem");
        await File.WriteAllTextAsync(authorityPath, upstreamCertificate.ExportCertificatePem());
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var port = ((IPEndPoint)target.LocalEndpoint).Port;
        var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bodyLength}\r\nConnection: close\r\n\r\n");
        var requestBytes = 0;
        var serving = Task.Run(async () =>
        {
            using var accepted = await target.AcceptTcpClientAsync(deadline.Token);
            using var tls = application == "https" ? new SslStream(accepted.GetStream(), leaveInnerStreamOpen: true) : null;
            if (tls is not null) await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            { ServerCertificate = upstreamCertificate, ApplicationProtocols = [SslApplicationProtocol.Http11] }, deadline.Token);
            Stream stream = tls is null ? accepted.GetStream() : tls;
            var header = new List<byte>();
            var single = new byte[1];
            while (header.Count < 8192)
            {
                await stream.ReadExactlyAsync(single, deadline.Token);
                header.Add(single[0]);
                if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
            }
            requestBytes = header.Count;
            await stream.WriteAsync(responseHeader, deadline.Token);
            for (var i = 0; i < bodyLength; i++)
            {
                await stream.WriteAsync(new byte[] { (byte)'x' }, deadline.Token);
                await Task.Delay(intervalMilliseconds, deadline.Token);
            }
        });
        using var fixture = new PeerConnectionTests.Fixture();
        var policy = application == "https" ? new InspectionPolicy { Https = new HttpsInspectionMaterial
        { CertificatePemPath = "fixture", PrivateKeyPemPath = "fixture", UpstreamName = "127.0.0.1", UpstreamCaPemPath = authorityPath } } : null;
        await fixture.StartAsync(inspectionMode: mode, peerApplication: application, peerTargetPort: port, peerInspection: policy,
            jumpApplication: application, jumpTargetPort: port, jumpInspection: policy, jumpUseServerCertificate: application == "https",
            openTimeoutSeconds: openSeconds, tcpIdleTimeoutSeconds: 2);
        await fixture.WaitOnlineAsync(deadline.Token);
        using var root = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(fixture.ServerCaPath, deadline.Token));
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        } };
        using var http = new HttpClient(handler);
        var entry = mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort;
        var watch = Stopwatch.StartNew();
        try { Assert.Equal(new string('x', bodyLength), await http.GetStringAsync($"{application}://127.0.0.1:{entry}/slow", deadline.Token)); }
        catch (HttpRequestException exception) { throw new InvalidOperationException(fixture.Logs() + " target=" + serving.Exception, exception); }
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(openSeconds));
        await serving;
        var status = await http.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", deadline.Token);
        var channelId = mode == "server-peer" ? "private" : "jump";
        var counters = status.GetProperty("channels").EnumerateArray().Single(item => item.GetProperty("channelId").GetString() == channelId);
        Assert.Equal(requestBytes, counters.GetProperty("applicationToTarget").GetInt64());
        Assert.Equal(responseHeader.Length + bodyLength, counters.GetProperty("applicationToCaller").GetInt64());
        Assert.Equal(0, counters.GetProperty("peerCiphertextToTarget").GetInt64());
    }

    [Theory]
    [InlineData("server-peer")]
    [InlineData("server-jump")]
    public async Task Approval_has_its_own_deadline_and_holds_pending_quota(string mode)
    {
        var policy = new InspectionPolicy { ApprovalTimeoutSeconds = 5, Rules = [new InspectionRule { Action = "approve" }] };
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: mode, peerApplication: "http", peerInspection: policy,
            jumpApplication: "http", jumpInspection: policy, openTimeoutSeconds: 2, maxPendingConnections: 1, tcpIdleTimeoutSeconds: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await fixture.WaitOnlineAsync(deadline.Token);
        var entry = mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort;
        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, entry, deadline.Token);
        await first.GetStream().WriteAsync("GET /approval HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), deadline.Token);
        var watch = Stopwatch.StartNew();
        using var http = new HttpClient();
        var channel = mode == "server-peer" ? "private" : "jump";
        await Task.Delay(700, deadline.Token);
        using (var second = new TcpClient())
        {
            await second.ConnectAsync(IPAddress.Loopback, entry, deadline.Token);
            try
            {
                await second.GetStream().WriteAsync("GET /second HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), deadline.Token);
                using var shortWait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], shortWait.Token));
            }
            catch (IOException) { }
        }
        var response = new byte[8192];
        try
        {
            var read = await first.GetStream().ReadAsync(response, deadline.Token);
            Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(4), $"Approval ended after {watch.Elapsed}.");
            if (read > 0) Assert.Contains("403", Encoding.ASCII.GetString(response, 0, read));
        }
        catch (IOException) { Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(4)); }
        Assert.Equal(0, fixture.TargetConnections);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var snapshot = await http.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels/{channel}/connections", deadline.Token);
            if (snapshot.GetProperty("connections").GetArrayLength() == 0) return;
            await Task.Delay(100, deadline.Token);
        }
        Assert.Fail("Pending connection was not reclaimed after approval timeout.");
    }

    [Theory]
    [InlineData("server-jump", "http")]
    [InlineData("server-jump", "https")]
    [InlineData("server-peer", "http")]
    [InlineData("server-peer", "https")]
    public async Task Client_half_close_drains_the_reverse_response_and_ends_completed(string mode, string application)
    {
        using var directory = new TestDirectory("relaylink-plugin-half-close-");
        string? authorityPath = null;
        using var upstreamCertificate = application == "https" ? CreateUpstreamCertificate(directory.Path, out authorityPath) : null;
        var body = new string('h', 64);
        var responseBytes = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        var request = Encoding.ASCII.GetBytes("GET /half-close HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var serving = Task.Run(async () =>
        {
            using var accepted = await target.AcceptTcpClientAsync(deadline.Token);
            if (upstreamCertificate is null)
            {
                await ServeHalfCloseResponseAsync(accepted.GetStream(), responseBytes, deadline.Token);
                return;
            }
            using var tls = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = upstreamCertificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            }, deadline.Token);
            await ServeHalfCloseResponseAsync(tls, responseBytes, deadline.Token);
        });
        using var fixture = new PeerConnectionTests.Fixture();
        var policy = application == "https" ? new InspectionPolicy
        {
            Https = new HttpsInspectionMaterial
            { CertificatePemPath = "fixture", PrivateKeyPemPath = "fixture", UpstreamName = "127.0.0.1", UpstreamCaPemPath = authorityPath }
        } : null;
        await fixture.StartAsync(inspectionMode: mode, peerApplication: application, peerTargetPort: targetPort, peerInspection: policy,
            jumpApplication: application, jumpTargetPort: targetPort, jumpInspection: policy, jumpUseServerCertificate: application == "https",
            openTimeoutSeconds: 5, tcpIdleTimeoutSeconds: 3);
        await fixture.WaitOnlineAsync(deadline.Token);
        var entry = mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort;
        using var caller = await fixture.ConnectLocalAsync(entry, deadline.Token);
        Stream stream = caller.GetStream();
        if (application == "https")
        {
            var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = ValidateServerCertificate(fixture.ServerCaPath)
            }, deadline.Token);
            stream = tls;
        }
        var watch = Stopwatch.StartNew();
        await stream.WriteAsync(request, deadline.Token);
        await stream.FlushAsync(deadline.Token);
        // Give the server a bounded window to read the request before the FIN arrives;
        // some platforms coalesce an immediate FIN with the request and abort the reply.
        await Task.Delay(150, deadline.Token);
        // The client finishes its request direction but keeps reading the response.
        caller.Client.Shutdown(SocketShutdown.Send);
        using var received = new MemoryStream();
        var buffer = new byte[256];
        var endOfStream = false;
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, readDeadline.Token);
                if (read == 0) { endOfStream = true; break; }
                received.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
        {
            Assert.Fail($"Half-close did not finish within 10 seconds. {fixture.Logs()}");
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Half-close ended in a transport failure instead of EOF. {fixture.Logs()}", exception);
        }
        watch.Stop();
        Assert.True(endOfStream, "The connection did not reach EOF after the response.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The half-closed relay ended after {watch.Elapsed}.");
        var response = Encoding.ASCII.GetString(received.ToArray());
        Assert.Contains("HTTP/1.1 200", response, StringComparison.Ordinal);
        Assert.Contains($"Content-Length: {body.Length}", response, StringComparison.Ordinal);
        Assert.True(response.EndsWith(body, StringComparison.Ordinal), $"The reverse response body was not drained completely: {response}");
        await serving;
        var channelId = mode == "server-peer" ? "private" : "jump";
        using var http = await LoginAsync(fixture, deadline.Token);
        var channel = await WaitForCountersAsync(http, channelId, deadline.Token);
        Assert.Equal(0, channel.GetProperty("pendingConnections").GetInt32());
        Assert.Equal(0, channel.GetProperty("activeConnections").GetInt32());
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>($"/api/v1/clients/visited/channels/{channelId}/connections", deadline.Token))
            .GetProperty("connections").EnumerateArray());
        var closedType = mode == "server-peer" ? "peer_connection_closed" : "proxy_connection_closed";
        var completed = false;
        for (var attempt = 0; attempt < 100 && !completed; attempt++)
        {
            var events = await FetchAuditAsync(http, $"&eventType={closedType}", deadline.Token);
            completed = events.Any(item => item.GetProperty("outcome").GetString() == "completed");
            if (!completed) await Task.Delay(100, deadline.Token);
        }
        Assert.True(completed, $"No completed {closedType} termination audit for the half-closed relay. {fixture.Logs()}");
    }

    [Theory]
    [InlineData("server-jump")]
    [InlineData("server-peer")]
    public async Task Target_disconnect_during_forwarding_ends_bounded_and_releases_capacity(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var accepted = 0;
        var serving = Task.Run(async () =>
        {
            using var connection = await target.AcceptTcpClientAsync(deadline.Token);
            Interlocked.Increment(ref accepted);
            var header = await ReadHeaderAsync(connection.GetStream(), deadline.Token);
            Assert.Contains("GET /disconnect", header, StringComparison.Ordinal);
            // Send a partial response body and then reset the connection while the
            // proxy is still forwarding it to the caller.
            await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4096\r\n\r\npartial"), deadline.Token);
            await Task.Delay(50, deadline.Token);
            connection.Client.LingerState = new LingerOption(true, 0);
            connection.Client.Dispose();
        });
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: mode, peerApplication: "http", peerTargetPort: targetPort,
            jumpApplication: "http", jumpTargetPort: targetPort, openTimeoutSeconds: 5, tcpIdleTimeoutSeconds: 3);
        await fixture.WaitOnlineAsync(deadline.Token);
        var entry = mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort;
        var channelId = mode == "server-peer" ? "private" : "jump";
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, entry, deadline.Token);
        var stream = caller.GetStream();
        await stream.WriteAsync("GET /disconnect HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), deadline.Token);
        var watch = Stopwatch.StartNew();
        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var buffer = new byte[1024];
            while (true)
            {
                if (await stream.ReadAsync(buffer, readDeadline.Token) == 0) break;
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
        {
            Assert.Fail($"The connection did not end within 10 seconds after the target reset. {fixture.Logs()}");
        }
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The connection ended after {watch.Elapsed}.");
        await serving;
        Assert.Equal(1, Volatile.Read(ref accepted));
        using var http = new HttpClient { BaseAddress = new Uri("http://" + fixture.DashboardAddress) };
        var channel = await WaitForCountersAsync(http, channelId, deadline.Token);
        Assert.Equal(0, channel.GetProperty("pendingConnections").GetInt32());
        Assert.Equal(0, channel.GetProperty("activeConnections").GetInt32());
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>($"/api/v1/clients/visited/channels/{channelId}/connections", deadline.Token))
            .GetProperty("connections").EnumerateArray());
        if (mode == "server-jump") Assert.True(channel.GetProperty("abortedTotal").GetInt64() >= 1, "The aborted channel counter did not increase.");
        using var admin = await LoginAsync(fixture, deadline.Token);
        var closedType = mode == "server-peer" ? "peer_connection_closed" : "proxy_connection_closed";
        var aborted = false;
        for (var attempt = 0; attempt < 100 && !aborted; attempt++)
        {
            var events = await FetchAuditAsync(admin, $"&eventType={closedType}", deadline.Token);
            aborted = events.Any(item => item.GetProperty("outcome").GetString() == "aborted")
                && events.Any(item => item.GetProperty("connectionId").ValueKind == JsonValueKind.String);
            if (!aborted) await Task.Delay(100, deadline.Token);
        }
        Assert.True(aborted, $"No aborted {closedType} termination audit after the target reset. {fixture.Logs()}");
    }

    [Theory]
    [InlineData("server-peer")]
    [InlineData("server-jump")]
    public async Task Approval_on_active_connection_keeps_active_one_and_pending_zero(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var captured = new List<string>();
        var serving = Task.Run(async () =>
        {
            using var accepted = await target.AcceptTcpClientAsync(deadline.Token);
            var stream = accepted.GetStream();
            captured.Add(await ReadHeaderAsync(stream, deadline.Token));
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(), deadline.Token);
            try
            {
                var buffer = new byte[1024];
                var count = await stream.ReadAsync(buffer, deadline.Token);
                if (count > 0) captured.Add(Encoding.ASCII.GetString(buffer, 0, count));
            }
            catch (IOException) { }
        });
        var policy = new InspectionPolicy { ApprovalTimeoutSeconds = 5, Rules = [new InspectionRule { PathPrefix = "/approval", Action = "approve" }] };
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: mode, peerApplication: "http", peerTargetPort: targetPort, peerInspection: policy,
            jumpApplication: "http", jumpTargetPort: targetPort, jumpInspection: policy, openTimeoutSeconds: 2, maxPendingConnections: 1, tcpIdleTimeoutSeconds: 1);
        await fixture.WaitOnlineAsync(deadline.Token);
        var entry = mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort;
        var channelId = mode == "server-peer" ? "private" : "jump";
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, entry, deadline.Token);
        var stream = caller.GetStream();
        var watch = Stopwatch.StartNew();
        await stream.WriteAsync("GET /health HTTP/1.1\r\nHost: example.test\r\n\r\nGET /approval HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), deadline.Token);
        Assert.Contains("200", await ReadHeaderAsync(stream, deadline.Token));
        var body = new byte[2];
        await stream.ReadExactlyAsync(body, deadline.Token);
        Assert.Equal("ok", Encoding.ASCII.GetString(body));
        using var http = await LoginAsync(fixture, deadline.Token);
        var waiting = false;
        for (var attempt = 0; attempt < 100 && !waiting; attempt++)
        {
            var events = await FetchAuditAsync(http, "&eventType=inspection_decision", deadline.Token);
            waiting = events.Any(item => item.GetProperty("outcome").GetString() == "waiting");
            if (!waiting) await Task.Delay(50, deadline.Token);
        }
        Assert.True(waiting, $"The second request never entered approval waiting. {fixture.Logs()}");
        // While the follow-up request waits for approval, the existing active
        // connection must stay counted once and must not add a pending connection.
        var snapshot = await http.GetFromJsonAsync<JsonElement>("/api/v1/clients/visited/channels", deadline.Token);
        var counters = snapshot.GetProperty("channels").EnumerateArray().Single(item => item.GetProperty("channelId").GetString() == channelId);
        Assert.Equal(1, counters.GetProperty("activeConnections").GetInt32());
        Assert.Equal(0, counters.GetProperty("pendingConnections").GetInt32());
        var connections = (await http.GetFromJsonAsync<JsonElement>($"/api/v1/clients/visited/channels/{channelId}/connections", deadline.Token))
            .GetProperty("connections");
        Assert.Single(connections.EnumerateArray());
        Assert.Contains("403", await ReadHeaderAsync(stream, deadline.Token));
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(4), $"The active approval ended after {watch.Elapsed}.");
        caller.Close();
        await serving;
        Assert.Single(captured);
        Assert.DoesNotContain("/approval", captured[0], StringComparison.Ordinal);
        await WaitForCountersAsync(http, channelId, deadline.Token);
        Assert.Equal(0, fixture.TargetConnections);
    }

    [Theory]
    [InlineData("server-jump")]
    [InlineData("server-peer")]
    public async Task Http_audit_records_decision_and_response_rows_without_fabricated_approval(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var targetRequests = new List<string>();
        var serving = Task.Run(async () =>
        {
            using var accepted = await target.AcceptTcpClientAsync(deadline.Token);
            var stream = accepted.GetStream();
            targetRequests.Add(await ReadHeaderAsync(stream, deadline.Token));
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(), deadline.Token);
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(3));
                var buffer = new byte[1024];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, wait.Token);
                    if (count == 0) break;
                    targetRequests.Add(Encoding.ASCII.GetString(buffer, 0, count));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        });
        var policy = new InspectionPolicy { Rules = [new InspectionRule { PathPrefix = "/secret", Action = "deny" }] };
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: mode, peerApplication: "http", peerTargetPort: targetPort, peerInspection: policy,
            jumpApplication: "http", jumpTargetPort: targetPort, jumpInspection: policy, openTimeoutSeconds: 2, tcpIdleTimeoutSeconds: 2);
        await fixture.WaitOnlineAsync(deadline.Token);
        var entry = mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort;
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, entry, deadline.Token);
        var stream = caller.GetStream();
        await stream.WriteAsync("GET /health HTTP/1.1\r\nHost: example.test\r\n\r\nGET /secret HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray(), deadline.Token);
        Assert.Contains("200", await ReadHeaderAsync(stream, deadline.Token));
        var body = new byte[2];
        await stream.ReadExactlyAsync(body, deadline.Token);
        Assert.Equal("ok", Encoding.ASCII.GetString(body));
        Assert.Contains("403", await ReadHeaderAsync(stream, deadline.Token));
        caller.Close();
        using var http = await LoginAsync(fixture, deadline.Token);
        JsonElement[] audited = [];
        var ready = false;
        for (var attempt = 0; attempt < 100 && !ready; attempt++)
        {
            audited = await FetchAuditAsync(http, "", deadline.Token);
            if (audited.Count(item => item.GetProperty("eventType").GetString() == "inspection_decision") >= 2 &&
                audited.Count(item => item.GetProperty("eventType").GetString() == "inspection_response") >= 2) { ready = true; break; }
            await Task.Delay(100, deadline.Token);
        }
        Assert.True(ready, $"The inspection decision/response audit rows were not persisted. {fixture.Logs()}");
        await Task.Delay(100, deadline.Token);
        audited = await FetchAuditAsync(http, "", deadline.Token);
        var decisions = audited.Where(item => item.GetProperty("eventType").GetString() == "inspection_decision").ToArray();
        var responses = audited.Where(item => item.GetProperty("eventType").GetString() == "inspection_response").ToArray();
        Assert.Equal(2, decisions.Length);
        Assert.Equal(2, responses.Length);
        var allowedDecision = decisions.Single(item => item.GetProperty("outcome").GetString() == "success");
        var deniedDecision = decisions.Single(item => item.GetProperty("outcome").GetString() == "denied");
        var allowedReason = allowedDecision.GetProperty("reasonCode").GetString();
        var deniedReason = deniedDecision.GetProperty("reasonCode").GetString();
        Assert.NotNull(allowedReason);
        Assert.NotNull(deniedReason);
        Assert.Contains("request:", allowedReason);
        Assert.Contains("GET example.test /health", allowedReason);
        Assert.Contains("request:", deniedReason);
        Assert.Contains("GET example.test /secret", deniedReason);
        Assert.Contains("rule_deny", deniedReason);
        var allowedResponse = responses.Single(item => item.GetProperty("outcome").GetString() == "success");
        var deniedResponse = responses.Single(item => item.GetProperty("outcome").GetString() == "denied");
        var allowedResponseReason = allowedResponse.GetProperty("reasonCode").GetString();
        var deniedResponseReason = deniedResponse.GetProperty("reasonCode").GetString();
        Assert.NotNull(allowedResponseReason);
        Assert.NotNull(deniedResponseReason);
        Assert.Equal(RequestTag(allowedReason), RequestTag(allowedResponseReason));
        Assert.Equal(RequestTag(deniedReason), RequestTag(deniedResponseReason));
        Assert.Contains("200", allowedResponseReason);
        Assert.Contains("403", deniedResponseReason);
        Assert.Contains("rule_deny", deniedResponseReason);
        Assert.DoesNotContain(audited, item =>
            item.GetProperty("eventType").GetString()?.Contains("approval_granted", StringComparison.Ordinal) == true);
        await serving;
        Assert.Single(targetRequests);
        Assert.DoesNotContain("/secret", targetRequests[0], StringComparison.Ordinal);
    }

    private static async Task ServeHalfCloseResponseAsync(Stream stream, byte[] response, CancellationToken token)
    {
        var header = await ReadHeaderAsync(stream, token);
        Assert.Contains("GET /half-close", header, StringComparison.Ordinal);
        await stream.WriteAsync(response, token);
        await stream.FlushAsync(token);
        // Keep the target socket alive briefly so the proxy can drain the whole
        // response before the normal close.
        await Task.Delay(100, token);
    }

    private static X509Certificate2 CreateUpstreamCertificate(string directory, out string authorityPath)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        authorityPath = Path.Combine(directory, "upstream-ca.pem");
        File.WriteAllText(authorityPath, signed.ExportCertificatePem());
        return X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }

    private static RemoteCertificateValidationCallback ValidateServerCertificate(string authorityPath) => (_, certificate, _, errors) =>
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        using var presented = new X509Certificate2(certificate);
        using var authority = X509Certificate2.CreateFromPem(File.ReadAllText(authorityPath));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(presented);
    };

    private static async Task<HttpClient> LoginAsync(PeerConnectionTests.Fixture fixture, CancellationToken token)
    {
        var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new Uri("http://" + fixture.DashboardAddress) };
        using var login = await http.PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = "test-password" }, token);
        login.EnsureSuccessStatusCode();
        http.DefaultRequestHeaders.Add("X-RelayLink-CSRF", (await login.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("csrfToken").GetString());
        return http;
    }

    private static async Task<JsonElement[]> FetchAuditAsync(HttpClient http, string query, CancellationToken token) =>
        (await http.GetFromJsonAsync<JsonElement>("/api/v1/admin/audit?hours=1&page=1&pageSize=100" + query, token))
        .GetProperty("events").EnumerateArray().ToArray();

    private static async Task<JsonElement> WaitForCountersAsync(HttpClient http, string channelId, CancellationToken token)
    {
        using var reclaim = CancellationTokenSource.CreateLinkedTokenSource(token);
        reclaim.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var snapshot = await http.GetFromJsonAsync<JsonElement>("/api/v1/clients/visited/channels", reclaim.Token);
            var counters = snapshot.GetProperty("channels").EnumerateArray().Single(item => item.GetProperty("channelId").GetString() == channelId);
            if (counters.GetProperty("pendingConnections").GetInt32() == 0 && counters.GetProperty("activeConnections").GetInt32() == 0) return counters;
            await Task.Delay(100, reclaim.Token);
        }
    }

    private static string RequestTag(string reason)
    {
        var tag = reason.Split(' ').FirstOrDefault(part => part.StartsWith("request:", StringComparison.Ordinal));
        Assert.NotNull(tag);
        return tag;
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < 65536)
        {
            if (await stream.ReadAsync(single, token) == 0) throw new EndOfStreamException();
            bytes.Add(single[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException();
    }
}
#endif
