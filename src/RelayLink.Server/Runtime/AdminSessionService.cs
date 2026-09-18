using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public sealed class AdminSessionService(ServerRuntime runtime)
{
    public const string CookieName = "relaylink_admin_session";
    private readonly ConcurrentDictionary<string, AdminSession> sessions = new(StringComparer.Ordinal);

    public bool TryLogin(string? username, string? password, out AdminSession? session)
    {
        session = null;
        var admin = runtime.Configuration.Server.Dashboard.Admin;
        if (!string.Equals(username, admin.Username, StringComparison.Ordinal) || !VerifyPassword(password, admin.PasswordHash)) return false;

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        session = new AdminSession(id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddMinutes(admin.SessionLifetimeMinutes));
        sessions[id] = session;
        return true;
    }

    public bool TryAuthorize(HttpRequest request, bool requireCsrf, out AdminSession? session)
    {
        session = null;
        if (!request.Cookies.TryGetValue(CookieName, out var id) || !sessions.TryGetValue(id, out var candidate)) return false;
        if (candidate.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            sessions.TryRemove(id, out _);
            return false;
        }

        if (requireCsrf && (!request.Headers.TryGetValue("X-RelayLink-CSRF", out var csrf) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(csrf.ToString()), Encoding.UTF8.GetBytes(candidate.CsrfToken)))) return false;
        session = candidate;
        return true;
    }

    public void Logout(HttpRequest request)
    {
        if (request.Cookies.TryGetValue(CookieName, out var id)) sessions.TryRemove(id, out _);
    }

    private static bool VerifyPassword(string? password, string encoded)
    {
        if (password is null) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2-SHA256" || !int.TryParse(parts[1], out var iterations) || iterations < 100_000) return false;
        try
        {
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

public sealed record AdminSession(string Id, string CsrfToken, DateTimeOffset ExpiresAtUtc);
