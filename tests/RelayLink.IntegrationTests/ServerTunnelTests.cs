using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.IntegrationTests;

public sealed class ServerTunnelTests
{
    [Fact(Skip = "Requires a target host with a usable Schannel or OpenSSL TLS client credential provider; the current Windows sandbox cannot create one.")]
    public async Task Server_forwards_bytes_after_authenticated_data_tunnel_binds()
    {
        using var fixture = new TunnelFixture();
        await fixture.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cancellation.Token;

        using var controlClient = new TcpClient();
        await controlClient.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        await using var controlTls = CreateTrustedTestTls(controlClient);
        await controlTls.AuthenticateAsClientAsync(TestClientOptions, token);
        var controlReader = new FrameReader(controlTls);
        var controlWriter = new FrameWriter(controlTls);
        await controlWriter.WriteAsync(new Frame(FrameType.Register, JsonProtocolSerializer.Serialize(new RegisterMessage("test-agent", fixture.Secret, "integration-test"))), token);
        var accepted = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.RegisterAccepted, accepted?.Type);
        var registered = JsonProtocolSerializer.Deserialize<RegisterAcceptedMessage>(accepted!.Payload.Span);
        await controlWriter.WriteAsync(new Frame(FrameType.ConfigAck, JsonProtocolSerializer.Serialize(new ConfigAckMessage(registered.SessionId, registered.ConfigVersion))), token);
        var ready = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.Ready, ready?.Type);

        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, fixture.ProxyPort, token);
        var openFrame = await controlReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token);
        Assert.Equal(FrameType.Open, openFrame?.Type);
        var open = JsonProtocolSerializer.Deserialize<OpenMessage>(openFrame!.Payload.Span);

        using var dataClient = new TcpClient();
        await dataClient.ConnectAsync(IPAddress.Loopback, fixture.TunnelPort, token);
        await using var dataTls = CreateTrustedTestTls(dataClient);
        await dataTls.AuthenticateAsClientAsync(TestClientOptions, token);
        var dataReader = new FrameReader(dataTls);
        var dataWriter = new FrameWriter(dataTls);
        await dataWriter.WriteAsync(new Frame(FrameType.BindData, JsonProtocolSerializer.Serialize(new BindDataMessage(open.SessionId, open.ConnectionId, open.ChannelId, open.Token))), token);
        Assert.Equal(FrameType.BindAccepted, (await dataReader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, token))?.Type);

        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, fixture.TargetPort, token);
        await dataWriter.WriteAsync(new Frame(FrameType.TargetReady, JsonProtocolSerializer.Serialize(new TargetReadyMessage(open.ConnectionId, 1))), token);
        Assert.Equal(FrameType.Start, (await dataReader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, token))?.Type);

        var input = RandomNumberGenerator.GetBytes(4096);
        await caller.GetStream().WriteAsync(input, token);
        var toTarget = await dataReader.ReadAsync(ProtocolConstants.MaxDataPayloadLength, token);
        Assert.Equal(FrameType.Data, toTarget?.Type);
        Assert.Equal(input, toTarget!.Payload.ToArray());
        await target.GetStream().WriteAsync(toTarget.Payload, token);
        var echoed = new byte[input.Length];
        await ReadExactlyAsync(target.GetStream(), echoed, token);
        await dataWriter.WriteAsync(new Frame(FrameType.Data, echoed), token);
        var received = new byte[input.Length];
        await ReadExactlyAsync(caller.GetStream(), received, token);
        Assert.Equal(input, received);
    }

    private static SslStream CreateTrustedTestTls(TcpClient client) => new(client.GetStream(), false, static (_, _, _, _) => true);
    private static readonly SslClientAuthenticationOptions TestClientOptions = new()
    {
        TargetHost = "localhost",
        EnabledSslProtocols = SslProtocols.Tls12,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    };

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new IOException("Unexpected EOF.");
            offset += read;
        }
    }

    private sealed class TunnelFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"relaylink-integration-{Guid.NewGuid():N}");
        private readonly TcpListener targetListener = new(IPAddress.Loopback, 0);
        private Process? server;
        private readonly StringBuilder serverOutput = new();
        private readonly StringBuilder serverError = new();
        private Task? echoTask;
        public int TunnelPort { get; } = GetFreePort();
        public int ProxyPort { get; } = GetFreePort();
        public int DashboardPort { get; } = GetFreePort();
        public int TargetPort => ((IPEndPoint)targetListener.LocalEndpoint).Port;
        public string Secret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        public async Task StartAsync()
        {
            Directory.CreateDirectory(Path.Combine(directory, "clients"));
            targetListener.Start();
            echoTask = EchoOnceAsync();
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            await File.WriteAllTextAsync(Path.Combine(directory, "certificate.pem"), certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(Path.Combine(directory, "clients", "test-agent.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, clientId = "test-agent", displayName = "Test Agent", enabled = true, secret = Secret, maxConnections = 10, maxPendingConnections = 5,
                channels = new[] { new { channelId = "echo", displayName = "Echo", enabled = true, listenAddress = "127.0.0.1", listenPort = ProxyPort, targetHost = "127.0.0.1", targetPort = TargetPort, maxConnections = 5, targetConnectTimeoutSeconds = 5 } }
            }));
            var configPath = Path.Combine(directory, "server.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                tunnel = new { listenAddress = "127.0.0.1", port = TunnelPort, certificatePemPath = Path.Combine(directory, "certificate.pem"), privateKeyPemPath = Path.Combine(directory, "key.pem"), handshakeTimeoutSeconds = 10, heartbeatIntervalSeconds = 15, heartbeatTimeoutSeconds = 45 },
                dashboard = new { listenAddress = "127.0.0.1", port = DashboardPort, refreshSeconds = 5 },
                clientsDirectory = Path.Combine(directory, "clients"),
                limits = new { maxConnections = 20, maxPendingConnections = 10, maxUnauthenticatedConnections = 10, maxChannelsPerClient = 10, openTimeoutSeconds = 10, blockedWriteTimeoutSeconds = 120, halfCloseDrainTimeoutSeconds = 300 }
            }));
            var assembly = typeof(RelayLink.Server.Configuration.ConfigurationLoader).Assembly.Location;
            server = Process.Start(new ProcessStartInfo("dotnet", $"\"{assembly}\" --config \"{configPath}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true })!;
            server.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (serverOutput) serverOutput.AppendLine(eventArgs.Data); };
            server.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (serverError) serverError.AppendLine(eventArgs.Data); };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var http = new HttpClient();
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));
                    if ((await http.GetAsync($"http://127.0.0.1:{DashboardPort}/health/ready", requestTimeout.Token)).IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException)
                {
                    if (timeout.IsCancellationRequested) break;
                }
                if (server.HasExited) throw new InvalidOperationException(DiagnosticOutput());
                try { await Task.Delay(100, timeout.Token); }
                catch (OperationCanceledException) { break; }
            }
            throw new TimeoutException($"Server did not become ready. {DiagnosticOutput()}");
        }

        private async Task EchoOnceAsync()
        {
            using var client = await targetListener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer);
            if (read > 0) await stream.WriteAsync(buffer.AsMemory(0, read));
        }

        public void Dispose()
        {
            targetListener.Stop();
            if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
            server?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }

        public string DiagnosticOutput()
        {
            if (server is null) return "Server process was not created.";
            lock (serverOutput)
            lock (serverError)
            {
                return $"Server running: {!server.HasExited}; exit code: {(server.HasExited ? server.ExitCode : -1)}; stdout: {serverOutput}; stderr: {serverError}";
            }
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
