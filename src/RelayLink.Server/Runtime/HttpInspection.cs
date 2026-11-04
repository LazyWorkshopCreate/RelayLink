using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static partial class HttpInspection
{
    public const int MaxHeaderBytes = 64 * 1024;

    public static async Task<Stream> OpenUpstreamAsync(Stream targetPipe, ChannelConfiguration channel, CancellationToken cancellationToken)
    {
        if (channel.Application != "https") return targetPipe;
        var https = channel.ResolvedInspection!.Https!;
        var upstreamName = string.IsNullOrWhiteSpace(https.UpstreamName) ? channel.TargetHost : https.UpstreamName;
        X509Certificate2? authority = null;
        SslStream? tls = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(https.UpstreamCaPemPath))
                authority = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(https.UpstreamCaPemPath, cancellationToken));
            tls = new SslStream(targetPipe, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = upstreamName,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = authority is null ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => ValidateUpstream(certificate, errors, authority)
            }, cancellationToken);
            if (!tls.IsEncrypted || tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http11)
                throw new AuthenticationException("Upstream TLS did not negotiate HTTP/1.1.");
            authority?.Dispose();
            return tls;
        }
        catch (Exception exception)
        {
            tls?.Dispose();
            authority?.Dispose();
            if (exception is AuthenticationException or IOException) throw;
            throw new AuthenticationException("Upstream TLS failed.", exception);
        }
    }

    public static string Match(InspectionPolicy? policy, string method, string host, string path)
    {
        var effective = policy ?? new InspectionPolicy();
        foreach (var rule in effective.Rules)
        {
            if (rule.Methods.Count > 0 && !rule.Methods.Contains(method, StringComparer.OrdinalIgnoreCase)) continue;
            if (rule.Host is not null && !string.Equals(rule.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.PathPrefix is not null && !path.StartsWith(rule.PathPrefix, StringComparison.Ordinal)) continue;
            return rule.Action;
        }
        if (effective.ApproveUnsafeMethods && method is not ("GET" or "HEAD" or "OPTIONS")) return "approve";
        return "allow";
    }

    private static async Task<(string Action, string Reason)> DecideAsync(InspectionPolicy? policy, string method, string host, string path, CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<IDisposable?>>? acquireApprovalLease = null,
        Func<CancellationToken, ValueTask>? onApprovalWaiting = null)
    {
        var action = Match(policy, method, host, path);
        if (action == "deny") return ("deny", "rule_deny");
        if (action != "approve") return ("allow", "allow");
        using var approval = InspectionApprovalGate.TryEnter();
        if (approval is null) return ("deny", "approval_capacity");
        using var operation = acquireApprovalLease is null ? null : await acquireApprovalLease(cancellationToken);
        if (acquireApprovalLease is not null && operation is null) return ("deny", "approval_capacity");
        if (onApprovalWaiting is not null) await onApprovalWaiting(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(policy?.ApprovalTimeoutSeconds ?? 60, 1, 300)), cancellationToken);
        return ("deny", "approval_timeout");
    }

    private static string Summary(string method, string host, string path)
    {
        if (path.Length > 256) path = path[..256];
        if (host.Length > 253) host = host[..253];
        return method + " " + host + " " + path;
    }

    private static bool ValidateUpstream(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2? authority)
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        if (authority is null) return errors == SslPolicyErrors.None;
        using var presented = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // Replacing the system trust root must not discard the TLS server purpose
        // check represented by SslStream's original chain validation errors.
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(presented);
    }

}
