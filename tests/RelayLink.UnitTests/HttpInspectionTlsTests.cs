using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class HttpInspectionTlsTests
{
    [Theory]
    [InlineData("1.3.6.1.5.5.7.3.1", true)]
    [InlineData("1.3.6.1.5.5.7.3.2", false)]
    [InlineData(null, true)]
    public void Custom_root_trust_still_requires_a_server_authentication_certificate(string? purpose, bool expected)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Inspection Test CA", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=target.test", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("target.test");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (purpose is not null) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(purpose)], true));
        using var certificate = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        var callback = typeof(HttpInspection).GetMethod("ValidateUpstream", BindingFlags.NonPublic | BindingFlags.Static)!;

        // SslStream reports a chain error for an explicitly configured root that is not
        // in the OS store. Rebuilding that chain must retain the TLS server EKU policy.
        Assert.Equal(expected, (bool)callback.Invoke(null, [certificate, SslPolicyErrors.RemoteCertificateChainErrors, root])!);
        Assert.False((bool)callback.Invoke(null, [certificate, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, root])!);
    }
}
