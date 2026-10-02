using System.Security.Cryptography;
using RelayLink.Server.Configuration;
using RelayLink.Protocol;

namespace RelayLink.Server.Runtime;

public sealed class AuthenticationService(ServerRuntime runtime)
{
    public bool TryAuthenticate(string clientId, string submittedSecret, out ClientConfiguration? client)
        => TryAuthenticate(clientId, submittedSecret, out client, out _);

    public bool TryAuthenticate(string clientId, string submittedSecret, out ClientConfiguration? client, out ControlRejectionReason reason)
    {
        client = null;
        reason = ControlRejectionReason.ClientNotFound;
        if (string.IsNullOrEmpty(clientId) || !runtime.Configuration.Clients.TryGetValue(clientId, out var candidate))
        {
            return false;
        }
        if (!candidate.Enabled) { reason = ControlRejectionReason.ClientDisabled; return false; }
        reason = ControlRejectionReason.InvalidSecret;
        if (submittedSecret is null) return false;

        byte[] submitted;
        try { submitted = Convert.FromBase64String(submittedSecret); }
        catch (FormatException) { return false; }

        var expected = ConfigurationLoader.DecodeSecret(candidate.Secret, candidate.ClientId);
        if (submitted.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(submitted, expected))
        {
            return false;
        }

        client = candidate;
        return true;
    }
}
