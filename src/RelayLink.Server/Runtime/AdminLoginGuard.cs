namespace RelayLink.Server.Runtime;

public sealed class AdminLoginGuard
{
    public const int MaxPasswordUtf8Bytes = 1024;
    public const int FailureThreshold = 5;
    public const int IpFailureThreshold = 20;
    public const int MaxTrackedStates = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumLock = TimeSpan.FromMinutes(15);
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, IpBucket> states = new(StringComparer.Ordinal);
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
        System.Text.Encoding.UTF8.GetByteCount(password ?? string.Empty) <= MaxPasswordUtf8Bytes;

    public bool IsLocked(string? remoteIp, string? username, out TimeSpan retryAfter)
    {
        lock (gate) return IsLockedCore(remoteIp, username, clock.GetUtcNow(), out retryAfter);
    }

    // Reserves one password check. A locked user or address, a full table, or an
    // in-flight attempt that would pass the threshold is rejected. A full table does
    // not evict unexpired history and is reported separately from a lock.
    public bool TryReserve(string? remoteIp, string? username, out TimeSpan retryAfter) =>
        TryReserve(remoteIp, username, out retryAfter, out _);

    public bool TryReserve(string? remoteIp, string? username, out TimeSpan retryAfter, out bool capacityExhausted)
    {
        lock (gate)
        {
            capacityExhausted = false;
            var now = clock.GetUtcNow();
            var address = IpKey(remoteIp);
            var name = UserKey(username);
            var exists = states.TryGetValue(address, out var bucket);
            if (exists && IsIdle(bucket!, now))
            {
                states.Remove(address);
                exists = false;
            }

            if (!exists)
            {
                if (!TryMakeRoom(now))
                {
                    capacityExhausted = true;
                    retryAfter = TimeSpan.FromSeconds(1);
                    return false;
                }

                bucket = new IpBucket();
                states[address] = bucket;
            }

            if (IsLockedCore(remoteIp, username, now, out retryAfter)) return false;
            var user = FreshUser(bucket!, name, now);
            if (Holding(user.Failures, user.Reserved, FailureThreshold) ||
                Holding(AggregateFailures(bucket!), AggregateReserved(bucket!), IpFailureThreshold))
            {
                retryAfter = TimeSpan.FromSeconds(1);
                if (bucket!.Users.Count == 0) states.Remove(address);
                return false;
            }

            bucket!.Users[name] = user with { Reserved = user.Reserved + 1, LastActivity = now };
            bucket.Aggregate = bucket.Aggregate with { LastActivity = now };
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void CompleteAttempt(string? remoteIp, string? username, bool success)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var address = IpKey(remoteIp);
            var name = UserKey(username);
            if (!states.TryGetValue(address, out var bucket)) return;
            ReleaseReservation(bucket, name);
            if (success)
            {
                bucket.Users.Remove(name);
                RefreshAggregate(bucket, now, extendLock: false);
                if (bucket.Users.Count == 0 && bucket.Aggregate.Failures == 0 && bucket.Aggregate.Reserved == 0)
                    states.Remove(address);
                return;
            }

            RecordUser(bucket, name, now);
            RefreshAggregate(bucket, now, extendLock: true);
        }
    }

    private bool IsLockedCore(string? remoteIp, string? username, DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!states.TryGetValue(IpKey(remoteIp), out var bucket)) return false;
        var remaining = ActiveLock(bucket.Aggregate, now);
        if (bucket.Users.TryGetValue(UserKey(username), out var user))
        {
            var userRemaining = ActiveLock(user, now);
            if (userRemaining > remaining) remaining = userRemaining;
        }

        if (remaining <= TimeSpan.Zero) return false;
        retryAfter = remaining;
        return true;
    }

    private static FailureState FreshUser(IpBucket bucket, string username, DateTimeOffset now)
    {
        if (!bucket.Users.TryGetValue(username, out var state) || (state.Reserved == 0 && IdleExpired(state, now)))
            return new FailureState(now, 0, state?.Reserved ?? 0, null);
        return state;
    }

    private static void RecordUser(IpBucket bucket, string username, DateTimeOffset now)
    {
        var state = FreshUser(bucket, username, now);
        bucket.Users[username] = WithFailure(state, now, FailureThreshold);
    }

    private static void RefreshAggregate(IpBucket bucket, DateTimeOffset now, bool extendLock)
    {
        foreach (var user in bucket.Users.Where(pair => pair.Value.Reserved == 0 && IdleExpired(pair.Value, now)).Select(pair => pair.Key).ToArray())
            bucket.Users.Remove(user);
        var failures = AggregateFailures(bucket);
        var locked = bucket.Aggregate.LockedUntil;
        if (failures >= IpFailureThreshold)
        {
            if (extendLock)
            {
                var until = LockUntil(now, failures, IpFailureThreshold);
                if (locked is null || until > locked) locked = until;
            }
        }
        else locked = null;

        bucket.Aggregate = bucket.Aggregate with { LastActivity = now, Failures = failures, LockedUntil = locked };
    }

    private static FailureState WithFailure(FailureState state, DateTimeOffset now, int threshold)
    {
        var failures = state.Failures + 1;
        DateTimeOffset? locked = state.LockedUntil;
        if (failures >= threshold)
        {
            var until = LockUntil(now, failures, threshold);
            if (locked is null || until > locked) locked = until;
        }

        return state with { LastActivity = now, Failures = failures, LockedUntil = locked };
    }

    private static DateTimeOffset LockUntil(DateTimeOffset now, int failures, int threshold)
    {
        var shift = Math.Min(failures - threshold, 10);
        var delaySeconds = Math.Min(MaximumLock.TotalSeconds, 1 << shift);
        return now.AddSeconds(delaySeconds);
    }

    private static void ReleaseReservation(IpBucket bucket, string username)
    {
        if (bucket.Users.TryGetValue(username, out var state) && state.Reserved > 0)
            bucket.Users[username] = state with { Reserved = state.Reserved - 1 };
    }

    private static int AggregateFailures(IpBucket bucket) => bucket.Users.Values.Sum(user => user.Failures);

    private static int AggregateReserved(IpBucket bucket) => bucket.Users.Values.Sum(user => user.Reserved);

    private static bool Holding(int failures, int reserved, int threshold) =>
        failures >= threshold ? reserved > 0 : failures + reserved >= threshold;

    private static TimeSpan ActiveLock(FailureState state, DateTimeOffset now)
    {
        if (state.LockedUntil is not { } until || until <= now) return TimeSpan.Zero;
        return until - now;
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
            var bucket = pair.Value;
            foreach (var user in bucket.Users.Where(item => item.Value.Reserved == 0 && IdleExpired(item.Value, now)).Select(item => item.Key).ToArray())
                bucket.Users.Remove(user);
            var failures = AggregateFailures(bucket);
            bucket.Aggregate = bucket.Aggregate with { Failures = failures, LockedUntil = failures >= IpFailureThreshold ? bucket.Aggregate.LockedUntil : null };
            if (bucket.Users.Count == 0 && IsIdle(bucket, now))
            {
                states.Remove(pair.Key);
                continue;
            }

            if (bucket.Users.Values.Any(user => user.Reserved > 0) && IsIdle(bucket, now)) waitingForReservation = true;
            var idleFrom = LatestActivity(bucket);
            var eligible = idleFrom + Window;
            if (earliest is null || eligible < earliest) earliest = eligible;
        }

        nextPruneNotBefore = waitingForReservation ? now : earliest ?? now.Add(Window);
    }

    private static bool IsIdle(IpBucket bucket, DateTimeOffset now)
    {
        if (bucket.Users.Values.Any(user => user.Reserved > 0 || !IdleExpired(user, now))) return false;
        return IdleExpired(bucket.Aggregate, now);
    }

    private static DateTimeOffset LatestActivity(IpBucket bucket)
    {
        var idleFrom = bucket.Aggregate.LastActivity;
        if (bucket.Aggregate.LockedUntil is { } until && until > idleFrom) idleFrom = until;
        foreach (var user in bucket.Users.Values)
        {
            var userFrom = user.LastActivity;
            if (user.LockedUntil is { } userUntil && userUntil > userFrom) userFrom = userUntil;
            if (userFrom > idleFrom) idleFrom = userFrom;
        }

        return idleFrom;
    }

    private static bool IdleExpired(FailureState state, DateTimeOffset now)
    {
        if (state.LockedUntil is { } active && active > now) return false;
        var idleFrom = state.LastActivity;
        if (state.LockedUntil is { } expired && expired > idleFrom) idleFrom = expired;
        return now - idleFrom > Window;
    }

    private static string IpKey(string? remoteIp) => string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp.Trim();

    private static string UserKey(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return string.Empty;
        var trimmed = username.Trim();
        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

    private sealed class IpBucket
    {
        public FailureState Aggregate { get; set; } = new(DateTimeOffset.UnixEpoch, 0, 0, null);
        public Dictionary<string, FailureState> Users { get; } = new(StringComparer.Ordinal);
    }

    private sealed record FailureState(DateTimeOffset LastActivity, int Failures, int Reserved, DateTimeOffset? LockedUntil);
}
