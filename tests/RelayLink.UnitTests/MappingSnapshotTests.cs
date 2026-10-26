using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Testing;

namespace RelayLink.UnitTests;

public sealed class MappingSnapshotTests
{
    [Fact]
    public async Task Mapping_create_uses_the_disk_snapshot_reloaded_inside_the_write_lock()
    {
        using var directory = new TestDirectory("relaylink-mapping-snapshot-");
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(directory.Path, "server.crt");
        var keyPath = Path.Combine(directory.Path, "server.key");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());

        var clientsDirectory = Path.Combine(directory.Path, "clients");
        Directory.CreateDirectory(clientsDirectory);
        var oldSecret = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray());
        var newSecret = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        var oldFingerprint = new string('a', 64);
        var newFingerprint = new string('b', 64);
        var callerSecret = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        var visitedSecret = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());
        var callerPath = Path.Combine(clientsDirectory, "caller.json");
        var visitedPath = Path.Combine(clientsDirectory, "visited.json");
        File.WriteAllText(callerPath, ClientJson("caller", "Caller", callerSecret, oldFingerprint, null));
        File.WriteAllText(visitedPath, ClientJson("visited", "Visited", visitedSecret, oldFingerprint, oldSecret));
        var serverPath = Path.Combine(directory.Path, "server.json");
        File.WriteAllText(serverPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            tunnel = new
            {
                listenAddress = "127.0.0.1",
                port = FreePort(),
                dataPort = FreePort(),
                tlsEnabled = true,
                certificatePemPath = certificatePath,
                privateKeyPemPath = keyPath,
                handshakeTimeoutSeconds = 10,
                heartbeatIntervalSeconds = 15,
                heartbeatTimeoutSeconds = 45
            },
            dashboard = new
            {
                listenAddress = "127.0.0.1",
                port = FreePort(),
                refreshSeconds = 5,
                admin = new { username = "admin", passwordHash = "PBKDF2-SHA256$100000$AA==$AA==", sessionLifetimeMinutes = 60 }
            },
            clientsDirectory,
            limits = new
            {
                maxConnections = 20,
                maxPendingConnections = 10,
                maxUnauthenticatedConnections = 10,
                maxChannelsPerClient = 10,
                openTimeoutSeconds = 10,
                blockedWriteTimeoutSeconds = 120,
                halfCloseDrainTimeoutSeconds = 30
            }
        }));

        var loaded = new ConfigurationLoader().Load(serverPath);
        var runtime = new ServerRuntime(loaded, new SessionRegistry());
        var service = new ProxyListenerService(runtime, new PendingConnectionRegistry(), new MetricsRegistry(loaded),
            new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<ProxyListenerService>.Instance);
        await service.ApplyConfigurationAsync(loaded, CancellationToken.None);
        try
        {
            var editor = new ChannelConfigurationEditor(serverPath, new ConfigurationLoader(), runtime, service,
                new PeerRelayRegistry(runtime, new MetricsRegistry(loaded), new AuditService(runtime, NullLogger<AuditService>.Instance), NullLogger<PeerRelayRegistry>.Instance),
                runtime.Sessions, new ConfigurationWriteLock());
            Assert.Equal(oldSecret, runtime.Configuration.Clients["visited"].Channels[0].AccessSecret);
            Assert.Equal(oldFingerprint, runtime.Configuration.Clients["visited"].E2eCertificateSha256);
            File.WriteAllText(visitedPath, ClientJson("visited", "Visited", visitedSecret, newFingerprint, newSecret));

            var (mapping, _) = await editor.CreateMappingAsync("caller", new MappingCreateRequest("visited", "private"), CancellationToken.None);

            Assert.Equal(newSecret, mapping.AccessSecret);
            Assert.Equal(newFingerprint, mapping.TargetCertificateSha256);
            var saved = await File.ReadAllTextAsync(callerPath);
            Assert.Contains(newSecret, saved, StringComparison.Ordinal);
            Assert.DoesNotContain(oldSecret, saved, StringComparison.Ordinal);
            Assert.Equal(newFingerprint, runtime.Configuration.Clients["visited"].E2eCertificateSha256);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string ClientJson(string clientId, string displayName, string secret, string fingerprint, string? accessSecret)
    {
        object[] channels = accessSecret is null ? [] :
        [
            new
            {
                channelId = "private",
                displayName = "Private",
                enabled = true,
                authorizedClientsOnly = true,
                endToEndEncryptionEnabled = true,
                accessSecret,
                listenAddress = "127.0.0.1",
                listenPort = 1,
                targetHost = "127.0.0.1",
                targetPort = 9,
                maxConnections = 2,
                targetConnectTimeoutSeconds = 5
            }
        ];
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            clientId,
            displayName,
            enabled = true,
            secret,
            maxConnections = 4,
            maxPendingConnections = 2,
            e2eCertificateSha256 = fingerprint,
            channels,
            outboundMappings = Array.Empty<object>()
        });
    }
}
