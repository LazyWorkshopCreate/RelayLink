using System.Collections.Concurrent;
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
            foreach (var candidate in desired.Values)
            {
                if (bindings.TryGetValue(candidate.Key, out var existing) && existing.HasSameListener(candidate)) existing.Update(candidate.Client, candidate.Channel);
            }
            var additions = desired.Values.Where(candidate => !bindings.TryGetValue(candidate.Key, out var existing) || !existing.HasSameListener(candidate)).ToArray();
            var started = new List<ListenerBinding>();
            try { foreach (var binding in additions) { binding.Start(); started.Add(binding); } }
            catch { foreach (var binding in started) binding.Stop(); throw; }

            foreach (var binding in additions)
            {
                bindings.TryGetValue(binding.Key, out var replaced);
                bindings[binding.Key] = binding;
                _ = AcceptLoopAsync(binding, serviceStoppingToken);
                logger.LogInformation("Proxy listener started for {ClientId}/{ChannelId} at {Address}:{Port}.", binding.Client.ClientId, binding.Channel.ChannelId, binding.Channel.ListenAddress, binding.Channel.ListenPort);
                replaced?.Stop();
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

    private async Task AcceptLoopAsync(ListenerBinding binding, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var caller = await binding.Listener.AcceptSocketAsync(cancellationToken);
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
        var client = binding.Client; var channel = binding.Channel;
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["clientId"] = client.ClientId, ["channelId"] = channel.ChannelId });
        try
        {
            var channelMetrics = metrics.For(client.ClientId, channel.ChannelId);
            channelMetrics.Accepted();
            if (!runtime.Sessions.TryGet(client.ClientId, out var session) || session is null) { caller.Dispose(); return; }
            globalHeld = await globalLimit.WaitAsync(0, cancellationToken); if (!globalHeld) { caller.Dispose(); return; }
            clientHeld = await clientLimits[client.ClientId].WaitAsync(0, cancellationToken); if (!clientHeld) { caller.Dispose(); return; }
            channelHeld = await binding.ChannelLimit.WaitAsync(0, cancellationToken); if (!channelHeld) { caller.Dispose(); return; }
            globalPendingHeld = await globalPendingLimit.WaitAsync(0, cancellationToken); if (!globalPendingHeld) { caller.Dispose(); return; }
            clientPendingHeld = await clientPendingLimits[client.ClientId].WaitAsync(0, cancellationToken); if (!clientPendingHeld) { caller.Dispose(); return; }
            pending = pendingConnections.Create(session, channel, caller, TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds));
            await session.SendAsync(new Frame(FrameType.Open, JsonProtocolSerializer.Serialize(new OpenMessage(session.SessionId, pending.ConnectionId, channel.ChannelId, session.ConfigVersion, pending.Token, runtime.Configuration.Server.Limits.OpenTimeoutSeconds * 1000))), cancellationToken);
            using var openTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            openTimeout.CancelAfter(TimeSpan.FromSeconds(runtime.Configuration.Server.Limits.OpenTimeoutSeconds));
            var tunnel = await pending.WaitForTunnelAsync(openTimeout.Token);
            var targetReady = await tunnel.Reader.ReadAsync(ProtocolConstants.MaxControlPayloadLength, openTimeout.Token);
            if (targetReady?.Type != FrameType.TargetReady || JsonProtocolSerializer.Deserialize<TargetReadyMessage>(targetReady.Payload.Span).ConnectionId != pending.ConnectionId) throw new ProtocolException("Data tunnel did not report TargetReady.");
            channelMetrics.MarkTargetSuccess();
            await tunnel.Writer.WriteAsync(new Frame(FrameType.Start, JsonProtocolSerializer.Serialize(new StartMessage(pending.ConnectionId))), openTimeout.Token);
            globalPendingLimit.Release(); globalPendingHeld = false; clientPendingLimits[client.ClientId].Release(); clientPendingHeld = false;
            channelMetrics.Opened(); relaying = true;
            using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var toAgent = RelayPump.CopySocketToFramesAsync(caller, tunnel.Writer, relayCancellation.Token, channelMetrics.AddToTarget);
            var toCaller = RelayPump.CopyFramesToSocketAsync(tunnel.Reader, caller, relayCancellation.Token, channelMetrics.AddToCaller);
            await RelayPump.CompleteBidirectionalAsync(toAgent, toCaller, () => { relayCancellation.Cancel(); caller.Dispose(); });
            relayCompletedNormally = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogDebug(exception, "Proxy connection failed for {ClientId}/{ChannelId}.", client.ClientId, channel.ChannelId); }
        finally
        {
            if (pending is not null) { pending.Complete(); pendingConnections.Remove(pending); }
            var channelMetrics = metrics.For(client.ClientId, channel.ChannelId);
            if (relaying) channelMetrics.Closed(relayCompletedNormally); else channelMetrics.OpenFailed();
            if (channelHeld) binding.ChannelLimit.Release(); if (clientHeld) clientLimits[client.ClientId].Release(); if (globalHeld) globalLimit.Release(); if (clientPendingHeld) clientPendingLimits[client.ClientId].Release(); if (globalPendingHeld) globalPendingLimit.Release();
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
