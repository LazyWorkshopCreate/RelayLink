using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Keys;
using Microsoft.DevTunnels.Ssh.Messages;
using RelayLink.Server.Configuration;
using RelayLink.Transport;
using SshBuffer = Microsoft.DevTunnels.Ssh.Buffer;

namespace RelayLink.Server.Runtime;

// One owner for protocol decisions, data pumps and shutdown. In particular, the
// library's channel PipeAsync must not overwrite our request authorization handler.
internal sealed class SshInspectionSession(Stream caller, Func<CancellationToken, Task<Stream>> openTarget, string clientId, string channelId,
    ChannelConfiguration channel, CredentialStore store, AuditService? audit, Guid connectionId, CancellationToken cancellationToken,
    Action<int>? fromCaller, Action<int>? fromTarget, Func<CancellationToken, Task>? onAuthenticated, Action? onActivity,
    TimeSpan setupTimeout, TimeSpan writeTimeout, TimeSpan halfCloseTimeout, Func<CancellationToken, ValueTask<IDisposable?>>? acquireApprovalLease,
    Action<string>? onRejected, ILogger? logger)
{
    private readonly SshInspectionPolicy policy = channel.Inspection?.Ssh ?? new();
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly SemaphoreSlim targetGate = new(1, 1);
    private readonly object sync = new();
    private readonly HashSet<Task> work = [];
    private readonly Dictionary<uint, ChannelState> channels = [];
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TraceSource trace = new(nameof(SshInspection)) { Switch = { Level = SourceLevels.Off } };
    private SshServerSession? server;
    private SshClientSession? target;
    private Stream? targetPipe;
    private CancellationTokenSource? opening;
    private Exception? failure;
    private int openingOperations;
    private bool authenticatedTarget;
    private byte[] trustedHash = [];

    public async Task<bool> RunAsync()
    {
        Exception? runFailure = null;
        IKeyPair? hostKey = null;
        try
        {
            if (!SshInspectionPolicy.TryParseHostKeyHash(policy.TargetHostKeySha256, out trustedHash) ||
                policy.AllowTcpForwarding || policy.AllowAgentForwarding || policy.AllowX11Forwarding)
            {
                await AuditAsync("inspection_decision", "denied", "ssh unavailable_trust_or_forwarding", lifetime.Token);
                onRejected?.Invoke("inspection_unavailable");
                return false;
            }
            var pem = await store.GetOrCreateHostKeyAsync(clientId, channelId, CreateHostKey, lifetime.Token);
            try { hostKey = KeyPair.ImportKey(Encoding.UTF8.GetString(pem), null, KeyFormat.Pkcs8, KeyEncoding.Pem); }
            finally { CryptographicOperations.ZeroMemory(pem); }
            server = new SshServerSession(SshSessionConfiguration.Default, trace) { Credentials = new SshServerCredentials(hostKey) };
            server.Authenticating += (_, args) => args.AuthenticationTask = Track(AuthenticateCallerAsync(args));
            server.Request += (_, args) => args.ResponseTask = Track(DenySessionRequestAsync(args));
            server.ChannelOpening += OnChannelOpening;
            server.Closed += (_, _) => closed.TrySetResult();
            opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            opening.CancelAfter(setupTimeout);
            using var openingRegistration = opening.Token.Register(Abort);
            using var lifetimeRegistration = lifetime.Token.Register(Abort);
            await server.ConnectAsync(caller, opening.Token);
            await closed.Task.WaitAsync(lifetime.Token);
        }
        catch (Exception exception) { runFailure = exception; }
        finally
        {
            Abort();
            Task[] remaining;
            lock (sync) remaining = work.ToArray();
            try { await Task.WhenAll(remaining); }
            catch (Exception exception) { runFailure ??= exception; }
            ChannelState[] states;
            lock (sync) { states = channels.Values.ToArray(); channels.Clear(); }
            foreach (var state in states) state.Dispose();
            target?.Dispose();
            targetPipe?.Dispose();
            server?.Dispose();
            caller.Dispose();
            opening?.Dispose();
            lifetime.Dispose();
            targetGate.Dispose();
            hostKey?.Dispose();
            trace.Close();
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (runFailure is not null) ExceptionDispatchInfo.Capture(runFailure).Throw();
        return authenticatedTarget;
    }

    private void OnChannelOpening(object? sender, SshChannelOpeningEventArgs args)
    {
        if (!args.IsRemoteRequest) return;
        lock (sync)
        {
            if (args.Request.ChannelType != SshChannel.SessionChannelType || channels.Count >= SshInspection.MaxChannelsPerSession)
            {
                args.OpeningTask = Track(DenyChannelAsync(args.Request.ChannelType, args.Cancellation));
                return;
            }
            args.Channel.MaxWindowSize = SshInspection.MaxInitializationBytes;
            var state = new ChannelState(args.Channel, lifetime.Token);
            channels.Add(args.Channel.ChannelId, state);
            args.Channel.Request += (_, request) =>
            {
                // SSH reuses incoming buffers: copy opaque extension bytes before queuing.
                var message = request.Request.ConvertTo<ChannelRequestMessage>(copy: true);
                request.ResponseTask = Track(HandleRequestAsync(state, message));
                request.ResponseContinuation = response =>
                {
                    // Send action acceptance before data: callers commonly attach their
                    // data reader only after receiving CHANNEL_SUCCESS.
                    if (response is ChannelSuccessMessage && state.ActionAccepted && Interlocked.CompareExchange(ref state.PumpStarted, 1, 0) == 0)
                        Track(PumpAsync(state));
                    if (state.CloseAfterResponse)
                    {
                        state.Dispose();
                        if (!authenticatedTarget) { server?.Dispose(); closed.TrySetResult(); }
                    }
                    return Task.CompletedTask;
                };
            };
            args.Channel.Closed += (_, _) =>
            {
                lock (sync) channels.Remove(args.Channel.ChannelId);
                state.Dispose();
            };
        }
    }

    private async Task<ClaimsPrincipal?> AuthenticateCallerAsync(SshAuthenticatingEventArgs args)
    {
        if (args.AuthenticationType != SshAuthenticationType.ClientPassword || string.IsNullOrEmpty(args.Username) || args.Password is null) return null;
        var presented = Encoding.UTF8.GetBytes(args.Password);
        try
        {
            foreach (var item in await store.ListAsync(clientId, channelId, lifetime.Token))
            {
                if (item.Purpose != "front-door" || item.Type != "password" || item.Username != args.Username) continue;
                using var material = await store.OpenAsync(clientId, channelId, item.CredentialId, lifetime.Token);
                if (material is null || !CryptographicOperations.FixedTimeEquals(presented, material.Secret)) continue;
                await AuditAsync("inspection_decision", "success", "ssh auth " + Clip(args.Username), lifetime.Token);
                return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, args.Username)], "password"));
            }
        }
        finally { CryptographicOperations.ZeroMemory(presented); }
        await AuditAsync("inspection_decision", "denied", "ssh auth " + Clip(args.Username), lifetime.Token);
        return null;
    }

    private async Task<SshMessage> DenySessionRequestAsync(SshRequestEventArgs<SessionRequestMessage> args)
    {
        await AuditAsync("inspection_decision", "denied", "ssh forward " + Clip(args.RequestType), lifetime.Token);
        return new SessionRequestFailureMessage();
    }

    private async Task<ChannelMessage> DenyChannelAsync(string? type, CancellationToken token)
    {
        await AuditAsync("inspection_decision", "denied", "ssh channel " + Clip(type ?? "unknown"), token);
        return new ChannelOpenFailureMessage { ReasonCode = SshChannelOpenFailureReason.AdministrativelyProhibited };
    }

    private async Task<SshMessage> HandleRequestAsync(ChannelState state, ChannelRequestMessage request)
    {
        if (Interlocked.Increment(ref state.RequestsInFlight) > 64)
        {
            Interlocked.Decrement(ref state.RequestsInFlight);
            Abort();
            return new ChannelFailureMessage();
        }
        var held = false;
        try
        {
            await state.Gate.WaitAsync(state.Token);
            held = true;
            if (state.Caller.IsClosed) return new ChannelFailureMessage();
            var type = request.RequestType;
            if (request.ToBuffer().Count > SshInspection.MaxInitializationBytes) return await DenyAsync("request_too_large");
            if (type is "pty-req" or "env")
            {
                var bytes = request.ToBuffer().Count;
                if (state.Target is not null || state.Initialization.Count >= SshInspection.MaxInitializationRequests || bytes > SshInspection.MaxInitializationBytes - state.InitializationBytes)
                    return await DenyAsync("initialization_limit_or_phase");
                state.Initialization.Add(request);
                state.InitializationBytes += bytes;
                return new ChannelSuccessMessage();
            }
            if (type is "window-change" or ChannelRequestTypes.Signal)
            {
                if (state.Target is null) return await DenyAsync("runtime_request_before_start");
                await AuditAsync("inspection_decision", "success", Summary(state, type), state.Token);
                var accepted = await SendRequestAsync(state.Target, request, state.Token);
                if (accepted) onActivity?.Invoke();
                return accepted ? new ChannelSuccessMessage() : new ChannelFailureMessage();
            }
            var kind = type == ChannelRequestTypes.Command ? "exec" : type == ChannelRequestTypes.Shell ? "shell" : type == "subsystem" ? "subsystem" : null;
            if (kind is null) return await DenyAsync("unsupported " + Clip(type ?? "unknown"));
            var command = kind == "exec" ? request.ConvertTo<CommandRequestMessage>().Command : null;
            var detail = kind == "subsystem" ? request.ConvertTo<SubsystemRequest>().Subsystem : command;
            var summary = Summary(state, kind) + (detail is null ? "" : " " + Clip(detail));
            if (state.Target is not null || !SessionAllowed(policy, kind, command)) return await DenyAsync(summary);

            BeginOpeningOperation();
            try
            {
                if (policy.RequireApproval)
                {
                    using var approval = InspectionApprovalGate.TryEnter();
                    if (approval is null) return await EndWaitAsync("approval_capacity");
                    using var pending = acquireApprovalLease is null ? null : await acquireApprovalLease(state.Token);
                    if (acquireApprovalLease is not null && pending is null) return await EndWaitAsync("approval_capacity");
                    await AuditAsync("inspection_decision", "waiting", summary + " approval_waiting", state.Token);
                    await Task.Delay(TimeSpan.FromSeconds(channel.Inspection?.ApprovalTimeoutSeconds ?? 60), state.Token);
                    await AuditAsync("inspection_approval_expired", "denied", summary + " approval_timeout", state.Token);
                    state.CloseAfterResponse = true;
                    if (!authenticatedTarget) onRejected?.Invoke("approval_timeout");
                    return new ChannelFailureMessage();
                }

                // Persist permission before the target connection or any remote action.
                await AuditAsync("inspection_decision", "success", summary + " allow", state.Token);
                await EnsureTargetAsync(state.Token);
                using var setup = Deadline(state.Token, setupTimeout);
                state.Target = await target!.OpenChannelAsync(setup.Token);
                state.TargetStream = new SshStream(state.Target);
                state.Target.Closed += (_, args) =>
                {
                    // Local cancellation also disposes the target channel. Do not report
                    // that synthetic close as a successful remote application exit.
                    if (!state.Token.IsCancellationRequested) state.TargetClose = args;
                };
                state.Target.Request += (_, args) => args.IsAuthorized = false;
                foreach (var earlier in state.Initialization)
                    if (!await SendRequestAsync(state.Target, earlier, setup.Token)) throw new InvalidOperationException("Target rejected SSH initialization.");
                state.Initialization.Clear();
                state.InitializationBytes = 0;
                if (!await SendRequestAsync(state.Target, request, setup.Token)) throw new InvalidOperationException("Target rejected the SSH action.");
                onActivity?.Invoke();
                await AuditAsync("inspection_action_result", "success", summary + " accepted", state.Token);
                state.ActionAccepted = true;
                return new ChannelSuccessMessage();
            }
            finally { EndOpeningOperation(); }
        }
        catch (OperationCanceledException) when (state.Token.IsCancellationRequested) { return new ChannelFailureMessage(); }
        catch (Exception exception) when (exception is IOException or SshConnectionException or SshChannelException or SocketException or
            InvalidOperationException or CredentialStoreException or AuthenticationException or OperationCanceledException)
        {
            state.Target?.Dispose();
            await AuditAsync("inspection_action_result", "denied", Summary(state, "target_failed"), lifetime.Token);
            state.CloseAfterResponse = true;
            if (!authenticatedTarget) onRejected?.Invoke("target_auth_failed");
            return new ChannelFailureMessage();
        }
        finally
        {
            if (held) state.Gate.Release();
            Interlocked.Decrement(ref state.RequestsInFlight);
        }

        async Task<SshMessage> DenyAsync(string reason)
        {
            await AuditAsync("inspection_decision", "denied", Summary(state, Clip(reason)), state.Token);
            return new ChannelFailureMessage();
        }
        async Task<SshMessage> EndWaitAsync(string reason)
        {
            state.CloseAfterResponse = true;
            if (!authenticatedTarget) onRejected?.Invoke(reason);
            return await DenyAsync(reason);
        }
    }

    private async Task EnsureTargetAsync(CancellationToken token)
    {
        await targetGate.WaitAsync(token);
        try
        {
            if (authenticatedTarget) return;
            using var setup = Deadline(token, setupTimeout);
            targetPipe = await openTarget(setup.Token);
            var candidate = new SshClientSession(SshSessionConfiguration.Default, trace);
            try
            {
                candidate.Authenticating += (_, args) =>
                {
                    if (args.AuthenticationType != SshAuthenticationType.ServerPublicKey) return;
                    var actual = args.PublicKey is null ? [] : SHA256.HashData(args.PublicKey.GetPublicKeyBytes().ToArray());
                    args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(CryptographicOperations.FixedTimeEquals(trustedHash, actual)
                        ? new ClaimsPrincipal(new ClaimsIdentity("pinned-target-host")) : null);
                };
                candidate.ChannelOpening += (_, args) =>
                {
                    if (args.IsRemoteRequest) args.FailureReason = SshChannelOpenFailureReason.AdministrativelyProhibited;
                };
                await candidate.ConnectAsync(targetPipe, setup.Token);
                if (!await candidate.AuthenticateServerAsync(setup.Token)) throw new AuthenticationException("SSH target host key mismatch.");
                // Do not even decrypt the credential until the target host is authenticated.
                using var material = await TargetCredentialsAsync(setup.Token);
                if (!await candidate.AuthenticateClientAsync(material.Credentials, setup.Token)) throw new AuthenticationException("SSH target authentication failed.");
                await AuditAsync("inspection_target_auth", "success", "ssh target_authenticated", setup.Token);
                if (onAuthenticated is not null) await onAuthenticated(setup.Token);
                target = candidate;
                lock (sync)
                {
                    authenticatedTarget = true;
                    opening?.CancelAfter(Timeout.InfiniteTimeSpan);
                }
            }
            catch
            {
                candidate.Dispose();
                targetPipe.Dispose();
                targetPipe = null;
                throw;
            }
        }
        finally { targetGate.Release(); }
    }

    private async Task PumpAsync(ChannelState state)
    {
        var toTarget = CopyAsync(state.CallerStream, state.Target!, fromCaller, false);
        var toCaller = CopyAsync(state.TargetStream!, state.Caller, fromTarget, true);
        try { await RelayPump.CompleteBidirectionalAsync(toTarget, toCaller, state.Dispose, halfCloseTimeout); }
        catch (Exception exception) when (state.Token.IsCancellationRequested && exception is OperationCanceledException or SshChannelException or ObjectDisposedException) { }
        finally
        {
            if (audit is not null)
            {
                using var deadline = Deadline(CancellationToken.None, writeTimeout);
                try
                {
                    var reason = Summary(state, "closed");
                    if (state.TargetClose?.ExitStatus is { } status) reason += " exit_status=" + status;
                    if (state.TargetClose?.ExitSignal is { } signal) reason += " exit_signal=" + Clip(signal);
                    await audit.RecordAsync(new AuditEvent("inspection_session_closed", state.TargetClose is { Exception: null } ? "completed" : "aborted")
                    {
                        ReasonCode = reason, ConnectionId = connectionId, ClientId = clientId, ChannelId = channelId,
                        BytesToTarget = Interlocked.Read(ref state.BytesToTarget), BytesToCaller = Interlocked.Read(ref state.BytesToCaller),
                        DurationMs = (long)Stopwatch.GetElapsedTime(state.Started).TotalMilliseconds
                    }, deadline.Token);
                }
                catch (Exception exception) when (exception is AuditUnavailableException or OperationCanceledException)
                {
                    logger?.LogWarning(exception, "SSH channel close audit failed for {ConnectionId}/{SshChannelId}.", connectionId, state.Caller.ChannelId);
                }
            }
        }

        async Task CopyAsync(SshStream source, SshChannel destination, Action<int>? count, bool targetToCaller)
        {
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, 0, buffer.Length, state.Token);
                if (read == 0)
                {
                    using var ending = Deadline(state.Token, writeTimeout);
                    if (!destination.IsClosed) await destination.SendAsync(SshBuffer.Empty, ending.Token);
                    if (targetToCaller && source.Channel.IsClosed && !destination.IsClosed)
                    {
                        if (state.TargetClose?.ExitStatus is { } status) await destination.CloseAsync(status, ending.Token);
                        else if (state.TargetClose?.ExitSignal is { } signal) await destination.CloseAsync(signal, state.TargetClose.ErrorMessage, ending.Token);
                        else await destination.CloseAsync(ending.Token);
                    }
                    return;
                }
                using var writing = Deadline(state.Token, writeTimeout);
                await destination.SendAsync(SshBuffer.From(buffer, 0, read), writing.Token);
                if (targetToCaller) Interlocked.Add(ref state.BytesToCaller, read);
                else Interlocked.Add(ref state.BytesToTarget, read);
                count?.Invoke(read);
                onActivity?.Invoke();
            }
        }
    }

    private async Task<bool> SendRequestAsync(SshChannel destination, ChannelRequestMessage request, CancellationToken token)
    {
        using var deadline = Deadline(token, writeTimeout);
        return await destination.RequestAsync(request, deadline.Token);
    }

    private void BeginOpeningOperation()
    {
        lock (sync) { openingOperations++; if (!authenticatedTarget) opening?.CancelAfter(Timeout.InfiniteTimeSpan); }
    }

    private void EndOpeningOperation()
    {
        lock (sync)
        {
            openingOperations--;
            if (!authenticatedTarget && openingOperations == 0 && !lifetime.IsCancellationRequested) opening?.CancelAfter(setupTimeout);
        }
    }

    private Task<T> Track<T>(Task<T> task) { Track((Task)task); return task; }
    private void Track(Task task)
    {
        lock (sync) work.Add(task);
        // Every task is retained until completion and has an exception observer.
        _ = ObserveAsync(task);
    }
    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); Abort(); }
        finally { lock (sync) work.Remove(task); }
    }

    private void Abort()
    {
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        server?.Dispose();
        target?.Dispose();
        caller.Dispose();
        closed.TrySetResult();
    }

    private async Task AuditAsync(string eventType, string outcome, string summary, CancellationToken token)
    {
        if (audit is null) return;
        using var deadline = Deadline(token, writeTimeout);
        await audit.RecordAsync(new AuditEvent(eventType, outcome)
        {
            ReasonCode = summary, ConnectionId = connectionId, ClientId = clientId, ChannelId = channelId
        }, deadline.Token);
    }

    private async Task<TargetMaterial> TargetCredentialsAsync(CancellationToken token)
    {
        var selected = (await store.ListAsync(clientId, channelId, token)).FirstOrDefault(item => item.Purpose == "target" && item.Type is "password" or "private-key")
            ?? throw new CredentialStoreException("SSH target credential is missing.");
        using var material = await store.OpenAsync(clientId, channelId, selected.CredentialId, token) ?? throw new CredentialStoreException("SSH target credential is missing.");
        if (material.Type == "password") return new TargetMaterial(new SshClientCredentials(material.Username, Encoding.UTF8.GetString(material.Secret)), null);
        var key = KeyPair.ImportKey(Encoding.UTF8.GetString(material.Secret), null, KeyFormat.Default, KeyEncoding.Default);
        return new TargetMaterial(new SshClientCredentials(material.Username, [key]) { PrivateKeyProvider = (_, _) => Task.FromResult<IKeyPair?>(key) }, key);
    }

    private sealed record TargetMaterial(SshClientCredentials Credentials, IKeyPair? PrivateKey) : IDisposable
    {
        public void Dispose() => PrivateKey?.Dispose();
    }

    private sealed class ChannelState : IDisposable
    {
        private readonly CancellationTokenSource lifetime;
        private int disposed;
        public ChannelState(SshChannel caller, CancellationToken parent)
        {
            Caller = caller; CallerStream = new SshStream(caller);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(parent); Token = lifetime.Token;
        }
        public SshChannel Caller { get; }
        public SshStream CallerStream { get; }
        public SshChannel? Target { get; set; }
        public SshStream? TargetStream { get; set; }
        public SshChannelClosedEventArgs? TargetClose { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationToken Token { get; }
        public List<ChannelRequestMessage> Initialization { get; } = [];
        public int InitializationBytes;
        public int RequestsInFlight;
        public bool ActionAccepted;
        public int PumpStarted;
        public bool CloseAfterResponse;
        public long Started { get; } = Stopwatch.GetTimestamp();
        public long BytesToTarget;
        public long BytesToCaller;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel(); Target?.Dispose(); Caller.Dispose();
            TargetStream?.Dispose(); CallerStream.Dispose(); lifetime.Dispose();
            // Pending cancelled handlers may still release Gate; no OS wait handle is used.
        }
    }

    private sealed class SubsystemRequest : ChannelRequestMessage
    {
        public string? Subsystem { get; private set; }
        protected override void OnRead(ref SshDataReader reader)
        {
            base.OnRead(ref reader);
            Subsystem = reader.ReadString(Encoding.UTF8);
        }
    }

    private static bool SessionAllowed(SshInspectionPolicy policy, string kind, string? command) => kind switch
    {
        "shell" => policy.AllowShell,
        "exec" => policy.AllowExec && !policy.DeniedCommandPrefixes.Any(prefix => prefix.Length > 0 && command?.StartsWith(prefix, StringComparison.Ordinal) == true),
        "subsystem" => policy.AllowSubsystem,
        _ => false
    };
    private static byte[] CreateHostKey()
    {
        using var key = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        return Encoding.UTF8.GetBytes(KeyPair.ExportPrivateKey(key, null, KeyFormat.Pkcs8, KeyEncoding.Pem));
    }
    private static CancellationTokenSource Deadline(CancellationToken parent, TimeSpan timeout)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(parent);
        result.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(20));
        return result;
    }
    private static string Summary(ChannelState state, string action) => "ssh " + action + " channel=" + state.Caller.ChannelId;
    private static string Clip(string value)
    {
        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ');
        return cleaned.Length <= 1024 ? cleaned : cleaned[..1024];
    }
}
