using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelayLink.Protocol;
using RelayLink.Server.Configuration;
using RelayLink.Transport;

namespace RelayLink.Server.Runtime;

public sealed class ProxyListenerService(ServerRuntime runtime, PendingConnectionRegistry pendingConnections, MetricsRegistry metrics, ILogger<ProxyListenerService> logger) : BackgroundService
{
    private readonly SemaphoreSlim configurationLock = new(1, 1);
    private readonly Dictionary<string, ListenerBinding> bindings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> clientLimits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> clientPendingLimits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> activeConnections = new();
    private readonly SemaphoreSlim globalLimit = runtime.GlobalConnectionLimit;
    private readonly SemaphoreSlim globalPendingLimit = runtime.GlobalPendingLimit;
    private long connectionTaskSequence;
    private CancellationToken serviceStoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        serviceStoppingToken = stoppingToken;
        await ApplyConfigurationAsync(runtime.Configuration, stoppingToken);
        runtime.IsReady = true;
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public async Task ApplyConfigurationAsync(LoadedConfiguration configuration, CancellationToken cancellationToken)
    {
        await configurationLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var client in configuration.Clients.Values)
            {
                clientLimits.GetOrAdd(client.ClientId, _ => new SemaphoreSlim(client.MaxConnections));
                clientPendingLimits.GetOrAdd(client.ClientId, _ => new SemaphoreSlim(client.MaxPendingConnections));
                foreach (var channel in client.Channels) metrics.For(client.ClientId, channel.ChannelId);
            }
            var desired = configuration.Clients.Values.Where(client => client.Enabled).SelectMany(client => client.Channels.Where(channel => channel.Enabled && !channel.AuthorizedClientsOnly).Select(channel => new ListenerBinding(client, channel))).ToDictionary(binding => binding.Key, StringComparer.Ordinal);
            var additions = desired.Values.Where(candidate => !bindings.TryGetValue(candidate.Key, out var existing) || !existing.HasSameListener(candidate)).ToArray();
            var started = new List<ListenerBinding>();
            // A wildcard listener overlaps a loopback listener on the same port. Release only
            // listeners being replaced/removed; keep established connections alive.
            var retired = bindings.Values.Where(existing =>
                (!desired.TryGetValue(existing.Key, out var replacement) || !existing.HasSameListener(replacement)) &&
                additions.Any(candidate => EndpointsOverlap(existing.Channel, candidate.Channel))).ToArray();
            foreach (var binding in retired) binding.Stop();
            try { foreach (var binding in additions) { binding.Start(); started.Add(binding); } }
            catch (Exception exception)
            {
                foreach (var binding in started) binding.Stop();
                foreach (var binding in retired)
                {
                    var restored = new ListenerBinding(binding.Client, binding.Channel);
                    try
                    {
                        restored.Start();
                        bindings[binding.Key] = restored;
                        _ = AcceptLoopAsync(restored, serviceStoppingToken);
                    }
                    catch (SocketException restoreException)
                    {
                        logger.LogCritical(restoreException, "Could not restore proxy listener for {ClientId}/{ChannelId}.", binding.Client.ClientId, binding.Channel.ChannelId);
                    }
                }
                if (exception is SocketException) throw new ConfigurationException($"Unable to bind channel listener: {exception.Message}");
                throw;
            }

            foreach (var candidate in desired.Values)
            {
                if (bindings.TryGetValue(candidate.Key, out var existing) && existing.HasSameListener(candidate)) existing.Update(candidate.Client, candidate.Channel);
            }

            foreach (var binding in additions)
            {
                bindings.TryGetValue(binding.Key, out var replaced);
                bindings[binding.Key] = binding;
                _ = AcceptLoopAsync(binding, serviceStoppingToken);
                logger.LogInformation("Proxy listener started for {ClientId}/{ChannelId} at {Address}:{Port}.", binding.Client.ClientId, binding.Channel.ChannelId, binding.Channel.ListenAddress, binding.Channel.ListenPort);
                if (replaced is { Stopped: false }) replaced.Stop();
            }
            foreach (var obsolete in bindings.Values.Where(binding => !desired.ContainsKey(binding.Key)).ToArray())
            {
                bindings.Remove(obsolete.Key);
                obsolete.Stop();
                logger.LogInformation("Proxy listener stopped for {ClientId}/{ChannelId}.", obsolete.Client.ClientId, obsolete.Channel.ChannelId);
            }
        }
        finally { configurationLock.Release(); }
    }

    private static bool EndpointsOverlap(ChannelConfiguration first, ChannelConfiguration second)
    {
        if (first.ListenPort != second.ListenPort) return false;
        var left = IPAddress.Parse(first.ListenAddress);
        var right = IPAddress.Parse(second.ListenAddress);
        return left.AddressFamily == right.AddressFamily &&
            (left.Equals(right) || left.Equals(IPAddress.Any) || right.Equals(IPAddress.Any) ||
             left.Equals(IPAddress.IPv6Any) || right.Equals(IPAddress.IPv6Any));
    }

    private async Task AcceptLoopAsync(ListenerBinding binding, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var caller = await binding.Listener.AcceptSocketAsync(cancellationToken);
                caller.NoDelay = true;
                TrackConnection(HandleCallerAsync(caller, binding, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (binding.Stopped) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var binding in bindings.Values) binding.Stop();
        var active = activeConnections.Values.ToArray();
        if (active.Length > 0) await Task.WhenAny(Task.WhenAll(active), Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
        await base.StopAsync(cancellationToken);
    }

    private void TrackConnection(Task task)
    {
        var id = Interlocked.Increment(ref connectionTaskSequence);
        activeConnections.TryAdd(id, task);
        _ = task.ContinueWith(_ => activeConnections.TryRemove(id, out Task? _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleCallerAsync(Socket caller, ListenerBinding binding, CancellationToken cancellationToken)
    {
        var globalHeld = false; var clientHeld = false; var channelHeld = false; var globalPendingHeld = false; var clientPendingHeld = false;
        PendingConnection? pending = null; var relaying = false; var relayCompletedNormally = false;
        var started = Stopwatch.GetTimestamp();
        var phase = "accepted";
        var outcome = "rejected";
        long toAgentBytes = 0, toCallerBytes = 0;
        var lastToAgent = started;
        var lastToCaller = started;
        var client = binding.Client; var channel = binding.Channel;
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["clientId"] = client.ClientId, ["channelId"] = channel.ChannelId });
        try
        {
            var channelMetrics = metrics.For(client.ClientId, channel.ChannelId);
            channelMetrics.Accepted();
            if (!runtime.Sessions.TryGet(client.ClientId, out var session) || session is not { IsReady: true }) { outcome = "agent_offline"; caller.Dispose(); return; }
            globalHeld = await globalLimit.WaitAsync(0, cancellationToken); if (!globalHeld) { caller.Dispose(); return; }
            clientHeld = await clientLimits[client.ClientId].WaitAsync(0, cancellationToken); if (!clientHeld) { caller.Dispose(); return; }
            channelHeld = await binding.ChannelLimit.WaitAsync(0, cancellationToken); if (!channelHeld) { caller.Dispose(); return; }
            globalPendingHeld = await globalPendingLimit.WaitAsync(0, cancellationToken); if (!globalPendingHeld) { caller.Dispose(); return; }
            clientPendingHeld = await clientPendingLimits[client.ClientId].WaitAsync(0, cancellationToken); if (!clientPendingHeld) { caller.Dispose(); return; }
            pending = pendingConnections.Create(session, channel, caller, TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds));
            phase = "open_sent";
            logger.LogInformation("Proxy {ConnectionId} opened for {ClientId}/{ChannelId}.", pending.ConnectionId, client.ClientId, channel.ChannelId);
            await session.SendAsync(new Frame(FrameType.Open, JsonProtocolSerializer.Serialize(new OpenMessage(session.SessionId, pending.ConnectionId, channel.ChannelId, session.ConfigVersion, pending.Token, runtime.Configuration.Server.Limits.OpenTimeoutSeconds * 1000))), cancellationToken);
            using var openTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeToken);
            openTimeout.CancelAfter(TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds));
            var tunnel = await pending.WaitForTunnelAsync(openTimeout.Token);
            phase = "data_bound";
            logger.LogInformation("Proxy {ConnectionId} data bound after {ElapsedMs} ms.", pending.ConnectionId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var targetConnectMs = await pending.WaitForTargetReadyAsync(openTimeout.Token);
            phase = "target_ready";
            logger.LogInformation("Proxy {ConnectionId} target ready after {ElapsedMs} ms; target connect {TargetConnectMs} ms.", pending.ConnectionId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, targetConnectMs);
            await session.SendAsync(new Frame(FrameType.Start, JsonProtocolSerializer.Serialize(new StartMessage(pending.ConnectionId))), openTimeout.Token);
            globalPendingLimit.Release(); globalPendingHeld = false; clientPendingLimits[client.ClientId].Release(); clientPendingHeld = false;
            channelMetrics.Opened(); relaying = true; phase = "relaying";
            logger.LogInformation("Proxy {ConnectionId} relay started after {ElapsedMs} ms.", pending.ConnectionId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeToken);
            using var progressCancellation = new CancellationTokenSource();
            var writeTimeout = TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.BlockedWriteTimeoutSeconds);
            var progress = ReportProgressAsync(pending.ConnectionId, started,
                () => (Interlocked.Read(ref toAgentBytes), Interlocked.Read(ref toCallerBytes), Interlocked.Read(ref lastToAgent), Interlocked.Read(ref lastToCaller)),
                progressCancellation.Token);
            try
            {
                var toAgent = CopyDirectionAsync(pending.ConnectionId, "caller_to_agent", caller, tunnel.Socket, relayCancellation.Token,
                    count => { channelMetrics.AddToTarget(count); Interlocked.Add(ref toAgentBytes, count); Interlocked.Exchange(ref lastToAgent, Stopwatch.GetTimestamp()); }, writeTimeout);
                var toCaller = CopyDirectionAsync(pending.ConnectionId, "agent_to_caller", tunnel.Socket, caller, relayCancellation.Token,
                    count => { channelMetrics.AddToCaller(count); Interlocked.Add(ref toCallerBytes, count); Interlocked.Exchange(ref lastToCaller, Stopwatch.GetTimestamp()); }, writeTimeout);
                await RelayPump.CompleteBidirectionalAsync(toAgent, toCaller, () => { relayCancellation.Cancel(); caller.Dispose(); tunnel.Socket.Dispose(); }, TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.HalfCloseDrainTimeoutSeconds));
                relayCompletedNormally = true;
                outcome = "completed";
            }
            finally { progressCancellation.Cancel(); await progress; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { outcome = "service_stopping"; }
        catch (Exception exception)
        {
            outcome = exception.GetType().Name;
            logger.LogWarning(exception, "Proxy {ConnectionId} failed in {Phase} for {ClientId}/{ChannelId}.", pending?.ConnectionId, phase, client.ClientId, channel.ChannelId);
        }
        finally
        {
            if (pending is not null)
                logger.LogInformation("Proxy {ConnectionId} ended: {Outcome}, phase {Phase}, to agent {ToAgentBytes} bytes, to caller {ToCallerBytes} bytes, elapsed {ElapsedMs} ms, idle {IdleAgentMs}/{IdleCallerMs} ms.",
                    pending.ConnectionId, outcome, phase, Interlocked.Read(ref toAgentBytes), Interlocked.Read(ref toCallerBytes),
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    (long)Stopwatch.GetElapsedTime(Interlocked.Read(ref lastToAgent)).TotalMilliseconds,
                    (long)Stopwatch.GetElapsedTime(Interlocked.Read(ref lastToCaller)).TotalMilliseconds);
            if (pending is not null) { pending.Complete(); pendingConnections.Remove(pending); }
            var channelMetrics = metrics.For(client.ClientId, channel.ChannelId);
            if (relaying) channelMetrics.Closed(relayCompletedNormally); else channelMetrics.OpenFailed();
            if (channelHeld) binding.ChannelLimit.Release(); if (clientHeld) clientLimits[client.ClientId].Release(); if (globalHeld) globalLimit.Release(); if (clientPendingHeld) clientPendingLimits[client.ClientId].Release(); if (globalPendingHeld) globalPendingLimit.Release();
        }
    }

    private async Task ReportProgressAsync(Guid connectionId, long started,
        Func<(long ToAgentBytes, long ToCallerBytes, long LastToAgent, long LastToCaller)> snapshot, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var (toAgent, toCaller, lastToAgent, lastToCaller) = snapshot();
                logger.LogInformation("Proxy {ConnectionId} progress: to agent {ToAgentBytes} bytes, to caller {ToCallerBytes} bytes, elapsed {ElapsedMs} ms, idle {IdleAgentMs}/{IdleCallerMs} ms.",
                    connectionId, toAgent, toCaller, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    (long)Stopwatch.GetElapsedTime(lastToAgent).TotalMilliseconds,
                    (long)Stopwatch.GetElapsedTime(lastToCaller).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task CopyDirectionAsync(Guid connectionId, string direction, Socket source, Socket destination,
        CancellationToken cancellationToken, Action<int> bytesWritten, TimeSpan writeTimeout)
    {
        try
        {
            await RelayPump.CopySocketToSocketAsync(source, destination, cancellationToken, bytesWritten, writeTimeout);
            logger.LogInformation("Proxy {ConnectionId} direction {Direction} reached EOF and propagated half-close.", connectionId, direction);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Proxy {ConnectionId} direction {Direction} cancelled.", connectionId, direction);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Proxy {ConnectionId} direction {Direction} failed.", connectionId, direction);
            throw;
        }
    }

    private sealed class ListenerBinding(ClientConfiguration client, ChannelConfiguration channel)
    {
        public string Key => $"{Client.ClientId}/{Channel.ChannelId}";
        public ClientConfiguration Client { get; private set; } = client;
        public ChannelConfiguration Channel { get; private set; } = channel;
        public TcpListener Listener { get; } = new(channel.ListenEndPoint);
        public SemaphoreSlim ChannelLimit { get; } = new(channel.MaxConnections);
        public bool Stopped { get; private set; }
        public void Start() => Listener.Start();
        public void Stop() { Stopped = true; Listener.Stop(); }
        public bool HasSameListener(ListenerBinding other) => Channel.ListenAddress == other.Channel.ListenAddress && Channel.ListenPort == other.Channel.ListenPort;
        public void Update(ClientConfiguration client, ChannelConfiguration channel) { Client = client; Channel = channel; }
    }
}
