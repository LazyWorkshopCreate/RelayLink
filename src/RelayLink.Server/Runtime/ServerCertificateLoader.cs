using System.Security.Cryptography.X509Certificates;

namespace RelayLink.Server.Runtime;

public static class ServerCertificateLoader
{
    public static X509Certificate2 Load(string certificatePath, string privateKeyPath)
    {
        // Deployment remains PEM on every platform. Schannel cannot use the ephemeral
        // private key produced by CreateFromPemFile, so import an in-memory PFX into
        // the current user's key provider; no certificate-store installation is needed.
        var certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
        if (!OperatingSystem.IsWindows()) return certificate;
        using (certificate)
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }
}
