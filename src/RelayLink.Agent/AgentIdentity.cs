using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelayLink.Agent;

internal sealed class AgentIdentity : IDisposable
{
    private AgentIdentity(X509Certificate2 certificate) => Certificate = certificate;
    public X509Certificate2 Certificate { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData));

    public static AgentIdentity LoadOrCreate(string path, string clientId)
    {
        if (File.Exists(path)) return Load(path);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=RelayLink-{clientId}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = CreatePrivateFile(temporary))
                file.Write(generated.Export(X509ContentType.Pkcs12));
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Load(path);
    }

    private static AgentIdentity Load(string path)
    {
        var keyStorage = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet
            : OperatingSystem.IsMacOS()
                ? X509KeyStorageFlags.DefaultKeySet
                : X509KeyStorageFlags.EphemeralKeySet;
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password: null, keyStorage);
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            certificate.Dispose();
            throw new CryptographicException("Agent E2E identity certificate is missing its private key or has expired.");
        }
        return new AgentIdentity(certificate);
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    public void Dispose() => Certificate.Dispose();
}
