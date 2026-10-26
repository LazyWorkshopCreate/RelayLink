using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class AdminPlaneTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("10.20.0.10", false)]
    public void Dashboard_loopback_policy_accepts_only_loopback_addresses(string address, bool loopback)
    {
        Assert.Equal(loopback, DashboardListenPolicy.IsLoopback(address));
        if (!loopback)
        {
            var warning = DashboardListenPolicy.NonLoopbackWarning(address);
            Assert.Contains("SECURITY:", warning);
            Assert.Contains(address, warning);
            Assert.Contains("127.0.0.1", warning);
        }
    }

    [Fact]
    public void Admin_cookie_is_host_only_and_secure_only_on_https()
    {
        var http = AdminSessionService.CreateCookieOptions(false, TimeSpan.FromMinutes(30));
        Assert.True(http.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, http.SameSite);
        Assert.False(http.Secure);
        Assert.Equal("/", http.Path);
        Assert.Equal(TimeSpan.FromMinutes(30), http.MaxAge);

        var https = AdminSessionService.CreateCookieOptions(true);
        Assert.True(https.Secure);
        Assert.True(https.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, https.SameSite);
    }

    [Fact]
    public void Oversized_password_is_rejected_without_replacing_the_session()
    {
        var sessions = new AdminSessionService(Runtime());
        Assert.True(sessions.TryLogin("admin", "test-password", out var first));
        Assert.False(sessions.TryLogin("admin", new string('p', AdminLoginGuard.MaxPasswordUtf8Bytes + 1), out _));
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + first!.Id;
        Assert.True(sessions.TryAuthorize(context.Request, requireCsrf: false, out var current));
        Assert.Equal(first.Id, current!.Id);
    }

    [Fact]
    public void Audit_failure_before_commit_keeps_the_existing_admin_session()
    {
        var sessions = new AdminSessionService(Runtime());
        Assert.True(sessions.TryLogin("admin", "test-password", out var existing));
        Assert.True(sessions.TryPrepareLogin("admin", "test-password", out var pending));
        Assert.NotEqual(existing!.Id, pending!.Id);

        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + existing.Id;
        Assert.True(sessions.TryAuthorize(context.Request, requireCsrf: false, out _));
        context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + pending.Id;
        Assert.False(sessions.TryAuthorize(context.Request, requireCsrf: false, out _));

        sessions.CommitLogin(pending);
        context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + existing.Id;
        Assert.False(sessions.TryAuthorize(context.Request, requireCsrf: false, out _));
        context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + pending.Id;
        Assert.True(sessions.TryAuthorize(context.Request, requireCsrf: false, out _));
    }

    [Fact]
    public void Successful_login_replaces_existing_admin_sessions()
    {
        var sessions = new AdminSessionService(Runtime());
        Assert.True(sessions.TryLogin("admin", "test-password", out var first));
        Assert.True(sessions.TryLogin("admin", "test-password", out var second));
        Assert.NotEqual(first!.Id, second!.Id);
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + first.Id;
        Assert.False(sessions.TryAuthorize(context.Request, requireCsrf: false, out _));
        context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + second.Id;
        Assert.True(sessions.TryAuthorize(context.Request, requireCsrf: false, out _));
    }

    [Fact]
    public void Wrong_username_does_not_open_a_session()
    {
        var sessions = new AdminSessionService(Runtime());
        Assert.False(sessions.TryLogin("someone-else", "test-password", out _));
        Assert.False(sessions.TryLogin("admin", "wrong-password", out _));
    }

    [Fact]
    public void Login_failures_lock_the_source_address_across_usernames()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-15T00:00:00Z"));
        var guard = new AdminLoginGuard(clock);
        for (var attempt = 0; attempt < AdminLoginGuard.FailureThreshold; attempt++)
        {
            Assert.True(guard.TryReserve("203.0.113.8", attempt == 0 ? "admin" : "other-name", out _));
            guard.CompleteAttempt("203.0.113.8", attempt == 0 ? "admin" : "other-name", success: false);
        }

        Assert.True(guard.IsLocked("203.0.113.8", "admin", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(guard.IsLocked("203.0.113.8", "unused-name", out _));
        Assert.False(guard.IsLocked("203.0.113.9", "admin", out _));

        guard.CompleteAttempt("203.0.113.8", "admin", success: true);
        Assert.False(guard.IsLocked("203.0.113.8", "admin", out _));
        Assert.False(guard.IsLocked("203.0.113.8", "other-name", out _));
    }

    [Fact]
    public void Login_lock_expires_and_then_counts_again()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-15T00:00:00Z"));
        var guard = new AdminLoginGuard(clock);
        for (var attempt = 0; attempt < AdminLoginGuard.FailureThreshold; attempt++)
        {
            Assert.True(guard.TryReserve("198.51.100.4", "admin", out _));
            guard.CompleteAttempt("198.51.100.4", "admin", success: false);
        }
        Assert.True(guard.IsLocked("198.51.100.4", "admin", out var firstLock));
        clock.Advance(firstLock + TimeSpan.FromMilliseconds(1));
        Assert.False(guard.IsLocked("198.51.100.4", "admin", out _));
        Assert.True(guard.TryReserve("198.51.100.4", "admin", out _));
        Assert.False(guard.TryReserve("198.51.100.4", "admin", out _));
        guard.CompleteAttempt("198.51.100.4", "admin", success: false);
        Assert.True(guard.IsLocked("198.51.100.4", "admin", out var longerLock));
        Assert.True(longerLock > firstLock);

        clock.Advance(longerLock + TimeSpan.FromMilliseconds(1));
        Assert.True(guard.TryReserve("198.51.100.4", "admin", out _));
        guard.CompleteAttempt("198.51.100.4", "admin", success: true);
        Assert.False(guard.IsLocked("198.51.100.4", "admin", out _));
        Assert.True(guard.TryReserve("198.51.100.4", "admin", out _));
    }

    [Theory]
    [InlineData(ControlRejectionReason.ClientNotFound)]
    [InlineData(ControlRejectionReason.ClientDisabled)]
    [InlineData(ControlRejectionReason.InvalidSecret)]
    public void Unauthenticated_auth_failures_share_one_wire_reason(ControlRejectionReason reason)
    {
        Assert.Equal(ControlRejectionReason.AuthenticationFailed, reason.ToUnauthenticatedWireReason());
        Assert.DoesNotContain("not registered", ControlRejectionReason.AuthenticationFailed.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", ControlRejectionReason.AuthenticationFailed.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", ControlRejectionReason.AuthenticationFailed.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Concurrent_successful_logins_leave_one_session()
    {
        var sessions = new AdminSessionService(Runtime());
        for (var round = 0; round < 8; round++)
        {
            var started = new Barrier(8);
            var ids = new string[8];
            Parallel.For(0, 8, index =>
            {
                started.SignalAndWait();
                Assert.True(sessions.TryLogin("admin", "test-password", out var session));
                ids[index] = session!.Id;
            });
            var authorized = ids.Count(id =>
            {
                var context = new DefaultHttpContext();
                context.Request.Headers.Cookie = AdminSessionService.CookieName + "=" + id;
                return sessions.TryAuthorize(context.Request, requireCsrf: false, out _);
            });
            Assert.Equal(1, authorized);
        }
    }

    [Fact]
    public void Concurrent_failures_cannot_pass_the_reserve_limit()
    {
        var guard = new AdminLoginGuard(new ManualTimeProvider(DateTimeOffset.Parse("2026-10-15T00:00:00Z")));
        var reserved = 0;
        Parallel.For(0, 32, _ =>
        {
            if (guard.TryReserve("203.0.113.20", "admin", out var _)) Interlocked.Increment(ref reserved);
        });
        Assert.Equal(AdminLoginGuard.FailureThreshold, reserved);
        Assert.False(guard.TryReserve("203.0.113.20", "rotated-name", out _));
    }

    [Fact]
    public void Locked_source_does_not_track_new_usernames_and_a_full_cache_keeps_locks()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-15T00:00:00Z"));
        var guard = new AdminLoginGuard(clock);
        for (var attempt = 0; attempt < AdminLoginGuard.FailureThreshold; attempt++)
        {
            Assert.True(guard.TryReserve("203.0.113.30", "admin", out _));
            guard.CompleteAttempt("203.0.113.30", "admin", success: false);
        }

        Assert.True(guard.IsLocked("203.0.113.30", "admin", out _));
        Assert.Equal(1, guard.TrackedStateCount);
        for (var name = 0; name < 40; name++)
        {
            var username = "user-" + name;
            Assert.True(guard.IsLocked("203.0.113.30", username, out _));
            Assert.False(guard.TryReserve("203.0.113.30", username, out _));
            guard.CompleteAttempt("203.0.113.30", username, success: false);
        }

        Assert.Equal(1, guard.TrackedStateCount);
        Assert.False(guard.TryReserve("203.0.113.30", "brand-new-user", out _));
        Assert.True(guard.IsLocked("203.0.113.30", "brand-new-user", out _));

        for (var index = 0; index < AdminLoginGuard.MaxTrackedStates; index++)
        {
            var address = $"198.51.100.{index / 250}.{index % 250}";
            if (!guard.TryReserve(address, $"name-{index}", out _)) break;
            guard.CompleteAttempt(address, $"name-{index}", success: false);
        }

        Assert.Equal(AdminLoginGuard.MaxTrackedStates, guard.TrackedStateCount);
        Assert.False(guard.TryReserve("192.0.2.10", "admin", out _));
        Assert.Equal(AdminLoginGuard.MaxTrackedStates, guard.TrackedStateCount);
        Assert.True(guard.IsLocked("203.0.113.30", "admin", out _));
        Assert.True(guard.IsLocked("203.0.113.30", "still-rotating", out _));

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(guard.IsLocked("203.0.113.30", "admin", out _));
        Assert.True(guard.TryReserve("192.0.2.99", "admin", out _));
        Assert.Equal(1, guard.TrackedStateCount);
    }

    [Fact]
    public void Sustained_failures_reach_the_maximum_lock_with_one_retry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-15T00:00:00Z"));
        var started = clock.GetUtcNow();
        var guard = new AdminLoginGuard(clock);
        for (var attempt = 0; attempt < AdminLoginGuard.FailureThreshold; attempt++)
        {
            Assert.True(guard.TryReserve("203.0.113.40", "admin", out _));
            guard.CompleteAttempt("203.0.113.40", "admin", success: false);
        }

        var reachedMaximum = false;
        for (var step = 0; step < 20 && !reachedMaximum; step++)
        {
            Assert.True(guard.IsLocked("203.0.113.40", "admin", out var current));
            clock.Advance(current + TimeSpan.FromMilliseconds(1));
            Assert.False(guard.IsLocked("203.0.113.40", "admin", out _));
            Assert.True(guard.TryReserve("203.0.113.40", "user-" + step, out _));
            Assert.False(guard.TryReserve("203.0.113.40", "other-" + step, out _));
            guard.CompleteAttempt("203.0.113.40", "user-" + step, success: false);
            Assert.True(guard.IsLocked("203.0.113.40", "admin", out var next));
            reachedMaximum = next >= TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1);
        }

        Assert.True(reachedMaximum);
        Assert.True(clock.GetUtcNow() - started > TimeSpan.FromMinutes(15));
        Assert.True(guard.IsLocked("203.0.113.40", "rotated", out var maximum));
        Assert.True(maximum >= TimeSpan.FromSeconds(899));
        clock.Advance(maximum + TimeSpan.FromMilliseconds(1));
        Assert.True(guard.TryReserve("203.0.113.40", "once", out _));
        Assert.False(guard.TryReserve("203.0.113.40", "twice", out _));
        guard.CompleteAttempt("203.0.113.40", "once", success: false);
        Assert.True(guard.IsLocked("203.0.113.40", "once", out var stillMaximum));
        Assert.True(stillMaximum >= TimeSpan.FromSeconds(899));
        Assert.Equal(1, guard.TrackedStateCount);
    }

    [Fact]
    public void Full_cache_preserves_unexpired_failures_and_refuses_new_sources()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-15T00:00:00Z"));
        var guard = new AdminLoginGuard(clock);
        const string kept = "203.0.113.77";
        Assert.True(guard.TryReserve(kept, "admin", out _));
        guard.CompleteAttempt(kept, "admin", success: false);

        for (var index = 0; index < AdminLoginGuard.MaxTrackedStates - 1; index++)
        {
            var address = $"198.51.100.{index / 250}.{index % 250}";
            Assert.True(guard.TryReserve(address, "admin", out _));
            guard.CompleteAttempt(address, "admin", success: false);
        }

        Assert.Equal(AdminLoginGuard.MaxTrackedStates, guard.TrackedStateCount);
        for (var attempt = 0; attempt < AdminLoginGuard.FailureThreshold - 1; attempt++)
        {
            Assert.True(guard.TryReserve(kept, "admin", out _));
            guard.CompleteAttempt(kept, "admin", success: false);
        }

        Assert.True(guard.IsLocked(kept, "admin", out _));
        Assert.Equal(AdminLoginGuard.MaxTrackedStates, guard.TrackedStateCount);
        Assert.False(guard.TryReserve("192.0.2.10", "new", out _));
        Assert.Equal(AdminLoginGuard.MaxTrackedStates, guard.TrackedStateCount);
        Assert.True(guard.IsLocked(kept, "admin", out _));

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(guard.IsLocked(kept, "admin", out _));
        Assert.True(guard.TryReserve("192.0.2.99", "admin", out _));
        Assert.Equal(1, guard.TrackedStateCount);
    }

    [Fact]
    public void Idle_source_regains_a_fresh_attempt_budget()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-15T00:00:00Z"));
        var guard = new AdminLoginGuard(clock);
        for (var attempt = 0; attempt < AdminLoginGuard.FailureThreshold; attempt++)
        {
            Assert.True(guard.TryReserve("203.0.113.41", "admin", out _));
            guard.CompleteAttempt("203.0.113.41", "admin", success: false);
        }

        clock.Advance(TimeSpan.FromMinutes(16));
        var reserved = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (guard.TryReserve("203.0.113.41", "user-" + attempt, out _)) reserved++;
        }

        Assert.Equal(AdminLoginGuard.FailureThreshold, reserved);
    }

    [Fact]
    public void Authenticated_rejection_reasons_stay_specific()
    {
        Assert.Equal(ControlRejectionReason.IdentityMismatch, ControlRejectionReason.IdentityMismatch.ToUnauthenticatedWireReason());
        Assert.Equal(ControlRejectionReason.DuplicateSession, ControlRejectionReason.DuplicateSession.ToUnauthenticatedWireReason());
    }

    private static ServerRuntime Runtime()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 100_000, HashAlgorithmName.SHA256, 32);
        var encoded = $"PBKDF2-SHA256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        var server = new ServerConfiguration(1,
            new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 10, 15, 45),
            new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", encoded, 60)),
            "clients", new LimitsConfiguration(10, 5, 5, 5, 20, 120, 300), null);
        return new ServerRuntime(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>()), new SessionRegistry());
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset utc = start;
        public override DateTimeOffset GetUtcNow() => utc;
        public void Advance(TimeSpan by) => utc += by;
    }
}
