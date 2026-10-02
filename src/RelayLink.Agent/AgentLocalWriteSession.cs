using System.Security.Cryptography;
using System.Text;

namespace RelayLink.Agent;

// A local browser needs a CSRF token for writes, but no management password.
public sealed class AgentLocalWriteSession
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly object gate = new();
    private readonly Dictionary<string, AgentWriteSession> sessions = new(StringComparer.Ordinal);

    public AgentWriteSession GetOrCreate(string? cookie)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (cookie is not null && sessions.TryGetValue(cookie, out var existing) && existing.ExpiresAtUtc > now)
                return existing;
            foreach (var expired in sessions.Where(item => item.Value.ExpiresAtUtc <= now).Select(item => item.Key).ToArray())
                sessions.Remove(expired);
            if (sessions.Count >= 128)
                sessions.Remove(sessions.MinBy(item => item.Value.ExpiresAtUtc).Key);
            var session = new AgentWriteSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), now.Add(Lifetime));
            sessions.Add(session.Token, session);
            return session;
        }
    }

    public AgentWriteSession? Get(string? cookie)
    {
        if (cookie is null) return null;
        lock (gate)
        {
            if (!sessions.TryGetValue(cookie, out var session)) return null;
            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow) return session;
            sessions.Remove(cookie);
            return null;
        }
    }

    public bool CheckCsrf(AgentWriteSession session, string? provided)
    {
        if (provided is null || provided.Length != session.CsrfToken.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(provided), Encoding.ASCII.GetBytes(session.CsrfToken));
    }

    public void Remove(string? cookie)
    {
        if (cookie is null) return;
        lock (gate) sessions.Remove(cookie);
    }
}

public sealed record AgentWriteSession(string Token, string CsrfToken, DateTimeOffset ExpiresAtUtc);
