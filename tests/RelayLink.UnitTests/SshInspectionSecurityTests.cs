using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;
using RelayLink.Testing;

namespace RelayLink.UnitTests;

public sealed class SshInspectionSecurityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_or_missing_target_key_never_receives_target_password(bool missing)
    {
        await using var harness = await Harness.CreateAsync();
        using var other = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        try
        {
            await harness.ConnectAsync(Policy(missing ? null : Fingerprint(other)));
            using var channel = await harness.Client.OpenChannelAsync(harness.Token);
            Assert.False(await channel.RequestAsync(new CommandRequestMessage("echo safe") { WantReply = true }, harness.Token));
        }
        catch (Exception exception) when (exception is SshConnectionException or SshChannelException or IOException) { }
        Assert.Equal(0, harness.PasswordRequests);
        Assert.Empty(harness.Executions);
    }

    [Fact]
    public async Task Audit_capacity_failure_before_first_exec_does_not_open_target()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        var capacity = (SemaphoreSlim)typeof(AuditService).GetField("capacity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Audit)!;
        while (capacity.Wait(0)) { }
        try
        {
            try { Assert.False(await channel.RequestAsync(new CommandRequestMessage("echo blocked") { WantReply = true }, harness.Token)); }
            catch (Exception exception) when (exception is SshConnectionException or SshChannelException or IOException) { }
            Assert.Equal(0, harness.TargetOpens);
            Assert.Empty(harness.Executions);
        }
        finally { capacity.Release(1024); }
    }

    [Fact]
    public async Task Later_exec_cannot_bypass_policy_after_first_channel_is_bridged()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint, "blocked"));
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        using var stream = new SshStream(channel);
        Assert.True(await channel.RequestAsync(new CommandRequestMessage("echo safe") { WantReply = true }, harness.Token));
        Assert.False(await channel.RequestAsync(new CommandRequestMessage("blocked action") { WantReply = true }, harness.Token));
        Assert.Equal(["echo safe"], harness.Executions.ToArray());
    }

    [Fact]
    public async Task Terminal_setup_is_isolated_between_channels()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var first = await harness.Client.OpenChannelAsync(harness.Token);
        using var firstStream = new SshStream(first);
        Assert.True(await first.RequestAsync(new TerminalRequestMessage { Term = "first", Columns = 80, Rows = 24, WantReply = true }, harness.Token));
        Assert.True(await first.RequestAsync(new CommandRequestMessage("first command") { WantReply = true }, harness.Token));
        using var second = await harness.Client.OpenChannelAsync(harness.Token);
        using var secondStream = new SshStream(second);
        Assert.True(await second.RequestAsync(new TerminalRequestMessage { Term = "second", Columns = 120, Rows = 40, WantReply = true }, harness.Token));
        Assert.True(await second.RequestAsync(new CommandRequestMessage("second command") { WantReply = true }, harness.Token));
        Assert.Equal(new[] { "first", "second" }, harness.Terminals.Select(item => item.Term!).ToArray());
        Assert.Equal(2, harness.Terminals.Select(item => item.Channel).Distinct().Count());
    }

    [Fact]
    public async Task Initialization_requests_have_a_per_channel_count_bound()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        for (var index = 0; index < 32; index++)
            Assert.True(await channel.RequestAsync(new ChannelRequestMessage { RequestType = "env", WantReply = true }, harness.Token));
        Assert.False(await channel.RequestAsync(new ChannelRequestMessage { RequestType = "env", WantReply = true }, harness.Token));
        Assert.Equal(0, harness.TargetOpens);
    }

    [Fact]
    public async Task Environment_requests_stay_with_their_channel_and_are_not_replayed_after_start()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var first = await harness.Client.OpenChannelAsync(harness.Token);
        using var firstStream = new SshStream(first);
        Assert.True(await first.RequestAsync(new EnvironmentRequest("FIRST", "one"), harness.Token));
        Assert.True(await first.RequestAsync(new CommandRequestMessage("first command") { WantReply = true }, harness.Token));
        using var second = await harness.Client.OpenChannelAsync(harness.Token);
        using var secondStream = new SshStream(second);
        Assert.True(await second.RequestAsync(new EnvironmentRequest("SECOND", "two"), harness.Token));
        Assert.True(await second.RequestAsync(new CommandRequestMessage("second command") { WantReply = true }, harness.Token));

        var firstTarget = harness.TargetChannelFor("first command");
        var secondTarget = harness.TargetChannelFor("second command");
        Assert.NotEqual(firstTarget, secondTarget);
        Assert.Equal(2, harness.Environments.Count);
        var firstEnvironment = Assert.Single(harness.Environments, item => item.Channel == firstTarget);
        Assert.Equal(("FIRST", "one"), (firstEnvironment.Name, firstEnvironment.Value));
        var secondEnvironment = Assert.Single(harness.Environments, item => item.Channel == secondTarget);
        Assert.Equal(("SECOND", "two"), (secondEnvironment.Name, secondEnvironment.Value));
        Assert.Equal(1, harness.TargetOpens);

        // Initialization is a one-time setup phase: later env requests are denied and must
        // neither reach the target nor be replayed onto the other channel.
        Assert.False(await first.RequestAsync(new EnvironmentRequest("LATE", "late"), harness.Token));
        Assert.False(await second.RequestAsync(new EnvironmentRequest("LATE", "late"), harness.Token));
        Assert.Equal(2, harness.Environments.Count);
        Assert.DoesNotContain(harness.Environments, item => item.Name == "LATE");
    }

    [Fact]
    public async Task Signal_requests_are_forwarded_only_to_their_owning_channel()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var first = await harness.Client.OpenChannelAsync(harness.Token);
        using var firstStream = new SshStream(first);
        Assert.True(await first.RequestAsync(new CommandRequestMessage("first command") { WantReply = true }, harness.Token));
        using var second = await harness.Client.OpenChannelAsync(harness.Token);
        using var secondStream = new SshStream(second);
        Assert.True(await second.RequestAsync(new CommandRequestMessage("second command") { WantReply = true }, harness.Token));
        Assert.True(await first.RequestAsync(new ChannelSignalMessage { Signal = "INT" }, harness.Token));
        Assert.True(await second.RequestAsync(new ChannelSignalMessage { Signal = "TERM" }, harness.Token));
        // A window-change round trip orders the signal behind the channel's own request chain.
        Assert.True(await first.RequestAsync(new ChannelRequestMessage { RequestType = "window-change", WantReply = true }, harness.Token));
        Assert.True(await second.RequestAsync(new ChannelRequestMessage { RequestType = "window-change", WantReply = true }, harness.Token));

        var firstTarget = harness.TargetChannelFor("first command");
        var secondTarget = harness.TargetChannelFor("second command");
        Assert.Equal(["INT"], harness.Signals.Where(item => item.Channel == firstTarget).Select(item => item.Signal).ToArray());
        Assert.Equal(["TERM"], harness.Signals.Where(item => item.Channel == secondTarget).Select(item => item.Signal).ToArray());
        Assert.Equal(2, harness.Signals.Count);
    }

    [Fact]
    public async Task Per_channel_in_flight_request_limit_aborts_the_session()
    {
        await using var harness = await Harness.CreateAsync();
        var policy = Policy(harness.TargetFingerprint) with { ApprovalTimeoutSeconds = 300 };
        policy = policy with { Ssh = policy.Ssh! with { RequireApproval = true } };
        await harness.ConnectAsync(policy);
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        var requests = new List<Task<bool>>
        {
            // The approval wait keeps the channel gate while the remaining requests queue.
            RequestOrClosedAsync(channel, new CommandRequestMessage("approval") { WantReply = true }, harness.Token)
        };
        for (var index = 0; index < 64; index++)
            requests.Add(RequestOrClosedAsync(channel, new EnvironmentRequest($"QUEUED{index}", "value"), harness.Token));

        var terminal = await AwaitRelayCompletionAsync(harness.RelayCompletion, TimeSpan.FromSeconds(10));
        Assert.True(terminal is null or OperationCanceledException, terminal?.Message ?? "relay completed");
        Assert.Equal(0, harness.TargetOpens);
        // The first request was still parked in its approval wait while the queued requests
        // pushed the per-channel in-flight count past the limit.
        var decisions = await harness.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1),
            "inspection_decision", null, 1, 50, CancellationToken.None);
        Assert.Contains(decisions.Events, item => item.ReasonCode?.Contains("approval_waiting", StringComparison.Ordinal) == true);
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(results, result => result);
    }

    [Fact]
    public async Task Cancelling_with_two_active_channels_finishes_the_relay_without_residual_tasks()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var first = await harness.Client.OpenChannelAsync(harness.Token);
        using var firstStream = new SshStream(first);
        using var second = await harness.Client.OpenChannelAsync(harness.Token);
        using var secondStream = new SshStream(second);
        Assert.True(await first.RequestAsync(new CommandRequestMessage("first command") { WantReply = true }, harness.Token));
        Assert.True(await second.RequestAsync(new CommandRequestMessage("second command") { WantReply = true }, harness.Token));
        Assert.Equal(1, harness.TargetOpens);
        var clientClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.Closed += (_, _) => clientClosed.TrySetResult();

        await harness.CancelRelayAsync();
        var terminal = await AwaitRelayCompletionAsync(harness.RelayCompletion, TimeSpan.FromSeconds(5));
        // Cancellation may surface as OperationCanceledException or as the transport reporting a lost connection.
        Assert.True(terminal is null or OperationCanceledException or SshConnectionException or IOException, terminal?.Message ?? "relay completed");
        await harness.TargetChannelsClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await clientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Every pump finishes and records its own close audit before the relay reports completion.
        AuditPage closedPage;
        var closedDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            closedPage = await harness.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1),
                "inspection_session_closed", null, 1, 50, CancellationToken.None);
            if (closedPage.Events.Count >= 2) break;
            await Task.Delay(50);
        }
        while (DateTimeOffset.UtcNow < closedDeadline);
        Assert.Equal(2, closedPage.Events.Count);
        Assert.Equal(1, harness.TargetOpens);
        Assert.True(harness.Client.IsClosed);
        var rejected = await Record.ExceptionAsync(async () =>
            await harness.Client.OpenChannelAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(rejected);
        Assert.IsNotType<TimeoutException>(rejected);
    }

    [Fact]
    public void Ssh_policy_passes_application_specific_schema_validation()
    {
        using var host = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        InspectionPolicy.Validate(Channel(Policy(Fingerprint(host))), "test/ssh", Path.GetTempPath());
    }

    [Theory]
    [InlineData("AllowTcpForwarding")]
    [InlineData("AllowAgentForwarding")]
    [InlineData("AllowX11Forwarding")]
    public void Unsupported_forwarding_cannot_be_enabled(string option)
    {
        var policy = JsonSerializer.Deserialize<InspectionPolicy>("{\"Ssh\":{\"" + option + "\":true}}")!;
        Assert.Throws<ConfigurationException>(() => InspectionPolicy.Validate(Channel(policy), "test/ssh", Path.GetTempPath()));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_host_fingerprints_are_rejected(string fingerprint)
    {
        Assert.Throws<ConfigurationException>(() => InspectionPolicy.Validate(Channel(Policy(fingerprint)), "test/ssh", Path.GetTempPath()));
    }

    [Fact]
    public void Missing_legacy_trust_remains_loadable_but_cannot_connect()
    {
        InspectionPolicy.Validate(Channel(new InspectionPolicy { Ssh = new SshInspectionPolicy() }), "test/ssh", Path.GetTempPath());
    }

    [Fact]
    public async Task Audit_failure_on_new_channel_cannot_execute_after_target_authentication()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var first = await harness.Client.OpenChannelAsync(harness.Token);
        using var firstStream = new SshStream(first);
        Assert.True(await first.RequestAsync(new CommandRequestMessage("first") { WantReply = true }, harness.Token));
        using var second = await harness.Client.OpenChannelAsync(harness.Token);
        var capacity = (SemaphoreSlim)typeof(AuditService).GetField("capacity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Audit)!;
        while (capacity.Wait(0)) { }
        try
        {
            try { Assert.False(await second.RequestAsync(new CommandRequestMessage("second") { WantReply = true }, harness.Token)); }
            catch (Exception exception) when (exception is SshConnectionException or SshChannelException or IOException) { }
            Assert.Equal(new[] { "first" }, harness.Executions.ToArray());
            Assert.Equal(1, harness.TargetOpens);
        }
        finally { capacity.Release(1024); }
    }

    [Fact]
    public async Task Closing_one_channel_preserves_other_channels_and_target_authentication()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var first = await harness.Client.OpenChannelAsync(harness.Token);
        using var firstStream = new SshStream(first);
        Assert.True(await first.RequestAsync(new CommandRequestMessage("first") { WantReply = true }, harness.Token));
        using var second = await harness.Client.OpenChannelAsync(harness.Token);
        using var secondStream = new SshStream(second);
        Assert.True(await second.RequestAsync(new CommandRequestMessage("second") { WantReply = true }, harness.Token));
        await first.CloseAsync(harness.Token);
        Assert.False(await harness.Client.RequestAsync(new SessionRequestMessage { RequestType = "tcpip-forward", WantReply = true }, harness.Token));
        Assert.True(await second.RequestAsync(new ChannelRequestMessage { RequestType = "window-change", WantReply = true }, harness.Token));
        Assert.Equal(1, harness.TargetOpens);
        Assert.Equal(1, harness.PasswordRequests);
    }

    [Fact]
    public async Task Initialization_bytes_are_bounded_and_not_replayed_to_target_before_allow()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        Assert.True(await channel.RequestAsync(new EnvironmentRequest("ONE", new string('a', 24 * 1024)), harness.Token));
        Assert.True(await channel.RequestAsync(new EnvironmentRequest("TWO", new string('b', 24 * 1024)), harness.Token));
        Assert.False(await channel.RequestAsync(new EnvironmentRequest("THREE", new string('c', 24 * 1024)), harness.Token));
        Assert.Equal(0, harness.TargetOpens);
    }

    [Fact]
    public async Task Session_channel_count_is_bounded()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        var channels = new List<SshChannel>();
        try
        {
            for (var index = 0; index < SshInspection.MaxChannelsPerSession; index++)
                channels.Add(await harness.Client.OpenChannelAsync(harness.Token));
            await Assert.ThrowsAsync<SshChannelException>(() => harness.Client.OpenChannelAsync(harness.Token));
            Assert.Equal(0, harness.TargetOpens);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }

    [Fact]
    public async Task Approval_wait_can_outlive_setup_deadline_and_never_opens_target()
    {
        await using var harness = await Harness.CreateAsync();
        var policy = Policy(harness.TargetFingerprint) with { ApprovalTimeoutSeconds = 2 };
        policy = policy with { Ssh = policy.Ssh! with { RequireApproval = true } };
        await harness.ConnectAsync(policy, setup: TimeSpan.FromSeconds(1));
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        var started = Stopwatch.GetTimestamp();
        Assert.False(await channel.RequestAsync(new CommandRequestMessage("wait") { WantReply = true }, harness.Token));
        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 1.7, 6);
        Assert.Equal(0, harness.TargetOpens);
        var page = await harness.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "inspection_approval_expired", null, 1, 50, harness.Token);
        Assert.Single(page.Events);
        Assert.False(await harness.RelayCompletion.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Payload_is_counted_and_target_authentication_callback_runs_once()
    {
        await using var harness = await Harness.CreateAsync();
        harness.ExpectedInputBytes = 7;
        harness.Output = "reply"u8.ToArray();
        harness.ExitStatus = 42;
        var toTarget = 0;
        var toCaller = 0;
        var authenticated = 0;
        var countedTarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countedCaller = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.ConnectAsync(Policy(harness.TargetFingerprint), count =>
            { if (Interlocked.Add(ref toTarget, count) >= 7) countedTarget.TrySetResult(); }, count =>
            { if (Interlocked.Add(ref toCaller, count) >= 5) countedCaller.TrySetResult(); },
            _ => { Interlocked.Increment(ref authenticated); return Task.CompletedTask; });
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        using var stream = new SshStream(channel);
        Assert.True(await channel.RequestAsync(new CommandRequestMessage("echo") { WantReply = true }, harness.Token));
        await stream.WriteAsync("request"u8.ToArray(), harness.Token);
        var reply = new byte[5];
        await stream.ReadExactlyAsync(reply, harness.Token);
        await Task.WhenAll(countedTarget.Task, countedCaller.Task).WaitAsync(harness.Token);
        Assert.Equal("reply"u8.ToArray(), reply);
        Assert.Equal(7, Volatile.Read(ref toTarget));
        Assert.Equal(5, Volatile.Read(ref toCaller));
        Assert.Equal(1, Volatile.Read(ref authenticated));
        // Wait for the application channel to close, then await session-owned audit work.
        Assert.Equal(0, await stream.ReadAsync(new byte[1], harness.Token));
        await harness.CompleteAsync();
        var audit = await harness.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "inspection_session_closed", null, 1, 50, harness.Token);
        var closed = Assert.Single(audit.Events);
        Assert.Equal(7, closed.BytesToTarget);
        Assert.Equal(5, closed.BytesToCaller);
        Assert.Contains("exit_status=42", closed.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_ssh_payload_can_arrive_after_setup_timeout()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Output = "late"u8.ToArray();
        harness.OutputDelay = TimeSpan.FromSeconds(2);
        await harness.ConnectAsync(Policy(harness.TargetFingerprint), setup: TimeSpan.FromSeconds(1));
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        using var stream = new SshStream(channel);
        Assert.True(await channel.RequestAsync(new CommandRequestMessage("slow output") { WantReply = true }, harness.Token));
        var output = new byte[4];
        await stream.ReadExactlyAsync(output, harness.Token);
        Assert.Equal("late"u8.ToArray(), output);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("exec")]
    [InlineData("subsystem")]
    public async Task Disabled_action_never_opens_a_target(string kind)
    {
        await using var harness = await Harness.CreateAsync();
        var policy = Policy(harness.TargetFingerprint);
        policy = policy with { Ssh = policy.Ssh! with { AllowShell = false, AllowExec = false, AllowSubsystem = false } };
        await harness.ConnectAsync(policy);
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        ChannelRequestMessage request = kind switch
        {
            "exec" => new CommandRequestMessage("blocked") { WantReply = true },
            "subsystem" => new SubsystemRequestMessage("sftp"),
            _ => new ChannelRequestMessage { RequestType = "shell", WantReply = true }
        };
        Assert.False(await channel.RequestAsync(request, harness.Token));
        Assert.Equal(0, harness.TargetOpens);
    }

    [Fact]
    public async Task Subsystem_name_is_audited_before_opening_its_target_channel()
    {
        await using var harness = await Harness.CreateAsync();
        var policy = Policy(harness.TargetFingerprint);
        policy = policy with { Ssh = policy.Ssh! with { AllowSubsystem = true } };
        await harness.ConnectAsync(policy);
        using var channel = await harness.Client.OpenChannelAsync(harness.Token);
        using var stream = new SshStream(channel);
        Assert.True(await channel.RequestAsync(new SubsystemRequestMessage("sftp"), harness.Token));
        var page = await harness.Audit.ReadAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), "inspection_decision", null, 1, 50, harness.Token);
        Assert.Contains(page.Events, item => item.ReasonCode?.Contains("sftp allow", StringComparison.Ordinal) == true);
        Assert.Equal(1, harness.TargetOpens);
    }

    [Fact]
    public async Task Denied_forwarding_audit_failure_is_observed_by_the_session_owner()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.ConnectAsync(Policy(harness.TargetFingerprint));
        var capacity = (SemaphoreSlim)typeof(AuditService).GetField("capacity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Audit)!;
        while (capacity.Wait(0)) { }
        try
        {
            try { Assert.False(await harness.Client.RequestAsync(new SessionRequestMessage { RequestType = "tcpip-forward", WantReply = true }, harness.Token)); }
            catch (Exception exception) when (exception is SshConnectionException or SshChannelException or IOException) { }
            await Assert.ThrowsAsync<AuditUnavailableException>(async () => await harness.RelayCompletion.WaitAsync(harness.Token));
            Assert.Equal(0, harness.TargetOpens);
        }
        finally { capacity.Release(1024); }
    }

    private sealed class SubsystemRequestMessage : ChannelRequestMessage
    {
        private readonly string subsystem;
        public SubsystemRequestMessage(string subsystem)
        {
            this.subsystem = subsystem;
            RequestType = "subsystem"; WantReply = true;
        }
        protected override void OnWrite(ref SshDataWriter writer)
        {
            base.OnWrite(ref writer);
            writer.Write(subsystem, System.Text.Encoding.UTF8);
        }
    }

    private sealed class EnvironmentRequest : ChannelRequestMessage
    {
        public EnvironmentRequest() { }
        public EnvironmentRequest(string name, string value)
        {
            Name = name; Value = value; RequestType = "env"; WantReply = true;
        }
        public string? Name { get; private set; }
        public string? Value { get; private set; }
        protected override void OnWrite(ref SshDataWriter writer)
        {
            base.OnWrite(ref writer);
            writer.Write(Name!, System.Text.Encoding.UTF8);
            writer.Write(Value!, System.Text.Encoding.UTF8);
        }
        protected override void OnRead(ref SshDataReader reader)
        {
            base.OnRead(ref reader);
            Name = reader.ReadString(System.Text.Encoding.UTF8);
            Value = reader.ReadString(System.Text.Encoding.UTF8);
        }
    }

    private static async Task<bool> RequestOrClosedAsync(SshChannel channel, ChannelRequestMessage request, CancellationToken token)
    {
        try { return await channel.RequestAsync(request, token); }
        catch (Exception exception) when (exception is SshConnectionException or SshChannelException or IOException or
            OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task<Exception?> AwaitRelayCompletionAsync(Task<bool> relay, TimeSpan timeout)
    {
        try { await relay.WaitAsync(timeout); return null; }
        catch (TimeoutException) { throw; }
        catch (Exception exception) { return exception; }
    }

    private static InspectionPolicy Policy(string? fingerprint, string? deniedPrefix = null) =>
        JsonSerializer.Deserialize<InspectionPolicy>(JsonSerializer.Serialize(new
        {
            Ssh = new { TargetHostKeySha256 = fingerprint, DeniedCommandPrefixes = deniedPrefix is null ? Array.Empty<string>() : [deniedPrefix] }
        }))!;

    private static string Fingerprint(IKeyPair key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(key.GetPublicKeyBytes().ToArray())).TrimEnd('=');

    private static ChannelConfiguration Channel(InspectionPolicy policy) =>
        new("jump", "Jump", true, "127.0.0.1", 1, "127.0.0.1", 9, 2, 5)
        { InspectionMode = "server-jump", Tags = ["app:ssh"], Inspection = policy };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TestDirectory directory = new("relaylink-ssh-security-");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
        private readonly TcpListener targetListener = new(IPAddress.Loopback, 0);
        private readonly IKeyPair targetKey = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        private readonly ConcurrentBag<IDisposable> owned = [];
        private Task serving = Task.CompletedTask;
        private Task<bool>? relay;
        private CredentialStore store = null!;
        public SshClientSession Client { get; private set; } = null!;
        public AuditService Audit { get; private set; } = null!;
        public CancellationToken Token => lifetime.Token;
        public string TargetFingerprint => Fingerprint(targetKey);
        public int PasswordRequests;
        public int TargetOpens;
        public int ExpectedInputBytes;
        public byte[] Output = [];
        public TimeSpan OutputDelay;
        public uint? ExitStatus;
        public Task<bool> RelayCompletion => relay!;
        public ConcurrentQueue<string> Executions { get; } = new();
        public ConcurrentQueue<(uint Channel, string Command)> ChannelCommands { get; } = new();
        public ConcurrentQueue<(uint Channel, string Name, string Value)> Environments { get; } = new();
        public ConcurrentQueue<(uint Channel, string Signal)> Signals { get; } = new();
        public ConcurrentQueue<(uint Channel, string? Term)> Terminals { get; } = new();
        private readonly TaskCompletionSource allTargetChannelsClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int targetChannelsOpened;
        private int targetChannelsClosed;
        public Task TargetChannelsClosed => allTargetChannelsClosed.Task;
        public Task CancelRelayAsync() => lifetime.CancelAsync();
        public uint TargetChannelFor(string command) => ChannelCommands.Single(item => item.Command == command).Channel;

        public static async Task<Harness> CreateAsync()
        {
            var result = new Harness();
            var config = Path.Combine(result.directory.Path, "server.json");
            await File.WriteAllTextAsync(config, "{}");
            result.store = CredentialStore.Open(config);
            await result.store.SaveAsync("visited", "jump", "door", "door", "front-door", "password", "test-door"u8.ToArray(), result.Token);
            await result.store.SaveAsync("visited", "jump", "root", "root", "target", "password", "test-target"u8.ToArray(), result.Token);
            var server = new ServerConfiguration(1,
                new TunnelConfiguration("127.0.0.1", 7443, false, "", "", 10, 15, 45),
                new DashboardConfiguration("127.0.0.1", 18080, 5, new DashboardAdminConfiguration("admin", "unused", 60)),
                "clients", new LimitsConfiguration(100, 20, 10, 10, 20, 120, 300), null)
                { Audit = new AuditConfiguration(Path.Combine(result.directory.Path, "audit.db")) };
            result.Audit = new AuditService(new ServerRuntime(new LoadedConfiguration(server, new Dictionary<string, ClientConfiguration>()), new SessionRegistry()), NullLogger<AuditService>.Instance);
            await result.Audit.StartAsync(result.Token);
            result.targetListener.Start();
            result.serving = result.ServeAsync();
            return result;
        }

        public async Task ConnectAsync(InspectionPolicy policy, Action<int>? fromCaller = null, Action<int>? fromTarget = null,
            Func<CancellationToken, Task>? onAuthenticated = null, TimeSpan? setup = null)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var caller = new TcpClient();
            owned.Add(caller);
            var connecting = caller.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Token);
            var accepted = await listener.AcceptTcpClientAsync(Token);
            owned.Add(accepted);
            await connecting;
            relay = SshInspection.RelayAsync(accepted.GetStream(), async token =>
            {
                Interlocked.Increment(ref TargetOpens);
                var target = new TcpClient();
                owned.Add(target);
                await target.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)targetListener.LocalEndpoint).Port, token);
                return target.GetStream();
            }, "visited", "jump", Channel(policy), store, Audit, Guid.NewGuid(), Token,
                fromCaller: fromCaller, fromTarget: fromTarget, onAuthenticated: onAuthenticated, setupTimeout: setup);
            Client = new SshClientSession(SshSessionConfiguration.Default, Quiet());
            Client.Authenticating += (_, args) =>
            {
                if (args.AuthenticationType == SshAuthenticationType.ServerPublicKey)
                    args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(Principal());
            };
            await Client.ConnectAsync(caller.GetStream(), Token);
            Assert.True(await Client.AuthenticateAsync(new SshClientCredentials("door", "test-door"), Token));
        }

        private async Task ServeAsync()
        {
            using var accepted = await targetListener.AcceptTcpClientAsync(Token);
            using var target = new SshServerSession(SshSessionConfiguration.Default, Quiet());
            target.Credentials = new SshServerCredentials(targetKey);
            target.Authenticating += (_, args) =>
            {
                if (args.AuthenticationType == SshAuthenticationType.ClientPassword)
                    Interlocked.Increment(ref PasswordRequests);
                args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(args.Password == "test-target" ? Principal() : null);
            };
            target.ChannelOpening += (_, args) =>
            {
                var stream = new SshStream(args.Channel);
                owned.Add(stream);
                Interlocked.Increment(ref targetChannelsOpened);
                args.Channel.Closed += (_, _) =>
                {
                    if (Interlocked.Increment(ref targetChannelsClosed) == Volatile.Read(ref targetChannelsOpened))
                        allTargetChannelsClosed.TrySetResult();
                };
                args.Channel.Request += (_, request) =>
                {
                    if (request.RequestType == ChannelRequestTypes.Command)
                    {
                        var command = request.Request.ConvertTo<CommandRequestMessage>().Command!;
                        Executions.Enqueue(command);
                        ChannelCommands.Enqueue((args.Channel.ChannelId, command));
                        if (Output.Length != 0)
                            request.ResponseContinuation = async _ =>
                            {
                                if (OutputDelay > TimeSpan.Zero) await Task.Delay(OutputDelay, Token);
                                await stream.ReadExactlyAsync(new byte[ExpectedInputBytes], Token);
                                await stream.WriteAsync(Output, Token);
                                if (ExitStatus is { } status) await args.Channel.CloseAsync(status, Token);
                            };
                    }
                    if (request.RequestType == "env")
                    {
                        var environment = request.Request.ConvertTo<EnvironmentRequest>();
                        Environments.Enqueue((args.Channel.ChannelId, environment.Name!, environment.Value!));
                    }
                    if (request.RequestType == ChannelRequestTypes.Signal)
                        Signals.Enqueue((args.Channel.ChannelId, request.Request.ConvertTo<ChannelSignalMessage>().Signal!));
                    if (request.RequestType == ChannelRequestTypes.Terminal)
                        Terminals.Enqueue((args.Channel.ChannelId, request.Request.ConvertTo<TerminalRequestMessage>().Term));
                    request.IsAuthorized = true;
                };
            };
            await target.ConnectAsync(accepted.GetStream(), Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, Token);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await lifetime.CancelAsync();
            targetListener.Stop();
            if (relay is not null)
                try { await relay.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception exception) when (exception is OperationCanceledException or SshConnectionException or SshChannelException or IOException or AuditUnavailableException) { }
            try { await serving; }
            catch (Exception exception) when (exception is OperationCanceledException or SshConnectionException or IOException or SocketException) { }
            foreach (var item in owned) item.Dispose();
            targetKey.Dispose();
            SqliteConnection.ClearAllPools();
            lifetime.Dispose();
            directory.Dispose();
        }

        public async Task CompleteAsync()
        {
            Client.Dispose();
            if (relay is not null) await relay.WaitAsync(Token);
        }

        private static TraceSource Quiet() => new("ssh-security-test") { Switch = { Level = SourceLevels.Off } };
        private static ClaimsPrincipal Principal() => new(new ClaimsIdentity("test"));
    }
}
