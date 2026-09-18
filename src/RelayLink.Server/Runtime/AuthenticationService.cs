using System.Security.Cryptography;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class AuthenticationService(ServerRuntime runtime)
{
    public bool TryAuthenticate(string clientId, string submittedSecret, out ClientConfiguration? client)
    {
        client = null;
        if (!runtime.Configuration.Clients.TryGetValue(clientId, out var candidate) || !candidate.Enabled)
        {
            return false;
        }

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
