using System.Security.Cryptography;
using System.Text;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class HttpInspectionTests
{
    [Fact]
    public void Https_material_pem_reloads_with_its_private_key()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-https-material-").FullName;
        try
        {
            using var rootKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var rootRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            using var serverKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var serverRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            serverRequest.CertificateExtensions.Add(san.Build());
            serverRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, true));
            serverRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
            using var signed = serverRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2), RandomNumberGenerator.GetBytes(16));
            var certificatePath = Path.Combine(directory, "cert.pem");
            var keyPath = Path.Combine(directory, "key.pem");
            File.WriteAllText(certificatePath, signed.ExportCertificatePem());
            File.WriteAllText(keyPath, serverKey.ExportPkcs8PrivateKeyPem());
            using var loaded = ServerCertificateLoader.Load(certificatePath, keyPath);
            Assert.True(loaded.HasPrivateKey);
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    [Fact]
    public void Inspection_policy_stays_out_of_the_agent_snapshot()
    {
        var channel = Channel(new InspectionPolicy { Rules = [new InspectionRule { Host = "hidden.example", Action = "deny" }] });
        var client = new ClientConfiguration(1, "agent", "Agent", true, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 4, 2, [channel]);
        var snapshot = ConfigurationSnapshotFactory.Create(client).Snapshot;
        var json = Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(snapshot));
        Assert.DoesNotContain("hidden.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("certificatePemPath", json, StringComparison.Ordinal);
        Assert.Equal("http", snapshot.Channels.Single().Application);
    }

    private static ChannelConfiguration Channel(InspectionPolicy policy) =>
        new("jump", "Jump", true, "127.0.0.1", 1, "127.0.0.1", 9, 2, 5)
        { InspectionMode = "server-jump", Tags = ["app:http"], Inspection = policy };

}
