using System.Text;

namespace RelayLink.Server.Runtime;

public sealed class AdminLoginGuard
{
    public const int MaxPasswordUtf8Bytes = 1024;
    public const int FailureThreshold = 5;
    public const int MaxTrackedStates = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumLock = TimeSpan.FromMinutes(15);
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, FailureState> states = new(StringComparer.Ordinal);
    private DateTimeOffset nextPruneNotBefore;

    public int TrackedStateCount
    {
        get { lock (gate) return states.Count; }
    }

    public AdminLoginGuard()
        : this(TimeProvider.System)
    {
    }

    public AdminLoginGuard(TimeProvider clock) => this.clock = clock;

    public static bool PasswordWithinLimit(string? password) =>
        Encoding.UTF8.GetByteCount(password ?? string.Empty) <= MaxPasswordUtf8Bytes;

    public bool IsLocked(string? remoteIp, string? username, out TimeSpan retryAfter)
    {
        lock (gate) return IsLockedCore(remoteIp, username, clock.GetUtcNow(), out retryAfter);
    }

    // Reserves one password check for this source before PBKDF2. Locked, full, or
    // already-reserved sources are rejected without creating a new username entry.
    public bool TryReserve(string? remoteIp, string? username, out TimeSpan retryAfter)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (IsLockedCore(remoteIp, username, now, out retryAfter)) return false;
            var key = IpKey(remoteIp);
            var exists = states.TryGetValue(key, out var state);
            if (!exists || IdleExpired(state!, now))
            {
                if (!exists && !TryMakeRoom(now))
                {
                    retryAfter = TimeSpan.FromSeconds(1);
                    return false;
                }

                state = new FailureState(now, 0, 0, null);
            }

            // Before the threshold, in-flight attempts count toward it. After a lock
            // expires, failures stay so the next miss backs off further, but exactly
            // one retry is allowed through. Idle expiry is based on the last attempt,
            // so a source that keeps failing does not lose its backoff history.
            var holdingLimit = state!.Failures >= FailureThreshold
                ? state.Reserved > 0
                : state.Failures + state.Reserved >= FailureThreshold;
            if (holdingLimit)
            {
                retryAfter = TimeSpan.FromSeconds(1);
                return false;
            }

            states[key] = state with { Reserved = state.Reserved + 1, LastActivity = now };
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void CompleteAttempt(string? remoteIp, string? username, bool success)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            ReleaseReservation(IpKey(remoteIp));
            if (success) Clear(remoteIp, username);
            else RecordCore(remoteIp, username, now);
        }
    }

    private bool IsLockedCore(string? remoteIp, string? username, DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        foreach (var key in Keys(remoteIp, username))
        {
            if (!states.TryGetValue(key, out var state) || state.LockedUntil is not { } until || until <= now) continue;
            var remaining = until - now;
            if (remaining > retryAfter) retryAfter = remaining;
        }

        return retryAfter > TimeSpan.Zero;
    }

    private void RecordCore(string? remoteIp, string? username, DateTimeOffset now)
    {
        _ = username;
        if (IsLockedCore(remoteIp, username, now, out _)) return;
        var addressKey = IpKey(remoteIp);
        if (!states.ContainsKey(addressKey) && !TryMakeRoom(now)) return;
        Record(addressKey, now);
    }

    private void Record(string key, DateTimeOffset now)
    {
        if (!states.TryGetValue(key, out var state) || IdleExpired(state, now))
            state = new FailureState(now, 0, state?.Reserved ?? 0, null);
        var failures = state.Failures + 1;
        DateTimeOffset? locked = state.LockedUntil;
        if (failures >= FailureThreshold)
        {
            var shift = Math.Min(failures - FailureThreshold, 10);
            var delaySeconds = Math.Min(MaximumLock.TotalSeconds, 1 << shift);
            var until = now.AddSeconds(delaySeconds);
            if (locked is null || until > locked) locked = until;
        }

        states[key] = state with { LastActivity = now, Failures = failures, LockedUntil = locked };
    }

    private void ReleaseReservation(string key)
    {
        if (states.TryGetValue(key, out var state) && state.Reserved > 0)
            states[key] = state with { Reserved = state.Reserved - 1 };
    }

    private void Clear(string? remoteIp, string? username)
    {
        _ = username;
        states.Remove(IpKey(remoteIp));
    }

    private bool TryMakeRoom(DateTimeOffset now)
    {
        if (states.Count < MaxTrackedStates) return true;
        if (now < nextPruneNotBefore) return false;
        Prune(now);
        return states.Count < MaxTrackedStates;
    }

    private void Prune(DateTimeOffset now)
    {
        DateTimeOffset? earliest = null;
        var waitingForReservation = false;
        foreach (var pair in states.ToArray())
        {
            var state = pair.Value;
            if (IdleExpired(state, now) && state.Reserved == 0)
            {
                states.Remove(pair.Key);
                continue;
            }

            if (state.Reserved > 0 && IdleExpired(state, now)) waitingForReservation = true;
            var idleFrom = state.LastActivity;
            if (state.LockedUntil is { } until && until > idleFrom) idleFrom = until;
            var eligible = idleFrom + Window;
            if (earliest is null || eligible < earliest) earliest = eligible;
        }

        nextPruneNotBefore = waitingForReservation ? now : earliest ?? now.Add(Window);
    }

    private static bool IdleExpired(FailureState state, DateTimeOffset now)
    {
        if (state.LockedUntil is { } active && active > now) return false;
        var idleFrom = state.LastActivity;
        if (state.LockedUntil is { } expired && expired > idleFrom) idleFrom = expired;
        return now - idleFrom > Window;
    }

    private static string IpKey(string? remoteIp) => "ip:" + (string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp.Trim());

    private static IEnumerable<string> Keys(string? remoteIp, string? username)
    {
        _ = username;
        yield return IpKey(remoteIp);
    }

    private sealed record FailureState(DateTimeOffset LastActivity, int Failures, int Reserved, DateTimeOffset? LockedUntil);
}
