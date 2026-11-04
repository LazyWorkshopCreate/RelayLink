using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RelayLink.Server.Configuration;
using RelayLink.Testing;

namespace RelayLink.IntegrationTests;

public sealed class HttpInspectionProcessTests
{
    [Theory]
    [InlineData("server-peer", false)]
    [InlineData("server-peer", true)]
    [InlineData("server-jump", false)]
    [InlineData("server-jump", true)]
    public async Task Every_pipelined_request_is_checked_across_real_server_and_agents(string mode, bool https)
    {
        using var directory = new TestDirectory("relaylink-http-requests-");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        using var certificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        var certificatePath = Path.Combine(directory.Path, "certificate.pem");
        var keyPath = Path.Combine(directory.Path, "key.pem");
        await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem());
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var targetPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var captured = new List<string>();
        var serving = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            Stream stream = accepted.GetStream();
            SslStream? tls = null;
            if (https)
            {
                tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, timeout.Token);
                stream = tls;
            }
            try
            {
                captured.Add(await ReadHeaderAsync(stream, timeout.Token));
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray(), timeout.Token);
                var next = new byte[4096];
                var count = await stream.ReadAsync(next, timeout.Token);
                if (count > 0) captured.Add(Encoding.ASCII.GetString(next, 0, count));
            }
            catch (IOException) { }
            finally { if (tls is not null) await tls.DisposeAsync(); }
        });
        var policy = new InspectionPolicy
        {
            Rules = [new InspectionRule { PathPrefix = "/secret", Action = "deny" }],
            Https = https ? new HttpsInspectionMaterial
            {
                CertificatePemPath = certificatePath, PrivateKeyPemPath = keyPath,
                UpstreamName = "127.0.0.1", UpstreamCaPemPath = certificatePath
            } : null
        };
        using var fixture = new PeerConnectionTests.Fixture();
        await fixture.StartAsync(inspectionMode: mode, jumpApplication: https ? "https" : "http", jumpTargetPort: targetPort,
            jumpInspection: policy, peerApplication: https ? "https" : "http", peerTargetPort: targetPort, peerInspection: policy);
        await fixture.WaitOnlineAsync(timeout.Token);
        var port = mode == "server-peer" ? await fixture.WaitPortAsync("to-visited", timeout.Token) : fixture.ProxyPort;
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        Stream caller = client.GetStream();
        SslStream? callerTls = null;
        if (https)
        {
            callerTls = new SslStream(caller, leaveInnerStreamOpen: true);
            await callerTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, presented, _, errors) => presented is not null &&
                    (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && presented.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData)
            }, timeout.Token);
            caller = callerTls;
        }
        try
        {
            await caller.WriteAsync("GET /health HTTP/1.1\r\nHost: example.test\r\n\r\nPOST /secret HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), timeout.Token);
            Assert.Contains("200", await ReadHeaderAsync(caller, timeout.Token), StringComparison.Ordinal);
            var body = new byte[2]; await caller.ReadExactlyAsync(body, timeout.Token);
            Assert.Equal("ok", Encoding.ASCII.GetString(body));
            Assert.Contains("403", await ReadHeaderAsync(caller, timeout.Token), StringComparison.Ordinal);
            client.Close();
            await serving.WaitAsync(timeout.Token);
            Assert.Single(captured);
            Assert.DoesNotContain("POST /secret", captured[0], StringComparison.Ordinal);
        }
        finally
        {
            timeout.Cancel();
            if (callerTls is not null) await callerTls.DisposeAsync();
            listener.Stop();
            try { await serving; } catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException) { }
        }
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var next = new byte[1];
        while (bytes.Count < 65536)
        {
            if (await stream.ReadAsync(next, token) == 0) throw new EndOfStreamException();
            bytes.Add(next[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException();
    }
}
