using System.Net;
using System.Net.Sockets;
using RelayLink.Protocol;
using RelayLink.Transport;

namespace RelayLink.Server.Runtime;

// The data listener never accepts Register. A short bind exchange is followed by
// opaque TCP bytes for ordinary proxy tunnels; peer tunnels retain their inner TLS.
public sealed class DataAcceptorService(
    ServerRuntime runtime,
    PendingConnectionRegistry pendingConnections,
    PeerRelayRegistry peerRelays,
    ILogger<DataAcceptorService> logger) : BackgroundService
{
    private readonly SemaphoreSlim unauthenticatedLimit = new(runtime.Configuration.Server.Limits.MaxUnauthenticatedConnections);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new TcpListener(IPAddress.Parse(runtime.Configuration.Server.Tunnel.ListenAddress), runtime.Configuration.Server.Tunnel.EffectiveDataPort);
        listener.Start();
        logger.LogInformation("Data listener started on {Address}:{Port}.", runtime.Configuration.Server.Tunnel.ListenAddress, runtime.Configuration.Server.Tunnel.EffectiveDataPort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                client.NoDelay = true;
                _ = HandleAcceptedAsync(client, stoppingToken).ContinueWith(completed =>
                {
                    if (completed.IsFaulted) logger.LogError(completed.Exception, "Data connection handler failed.");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task HandleAcceptedAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var acquired = false;
        try
        {
            try { acquired = await unauthenticatedLimit.WaitAsync(0, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            if (!acquired) return;

        var released = false;
        try
        {
            TcpKeepAlive.Enable(client.Client);
            using (client)
            {
                var stream = client.GetStream();
                var reader = new FrameReader(stream);
                FrameWriter? writer = null;
                PendingConnection? boundPending = null;
                PeerRelay? boundRelay = null;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(runtime.Configuration.Server.Tunnel.HandshakeTimeoutSeconds));
                    var first = await reader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, deadline.Token);
                    writer = new FrameWriter(stream, protocolVersion: reader.ProtocolVersion ?? ProtocolConstants.Version);
                    if (first?.Type == FrameType.BindData)
                    {
                        var bind = JsonProtocolSerializer.Deserialize<BindDataMessage>(first.Payload.Span);
                        if (bind is null || !pendingConnections.TryBind(bind.SessionId, bind.ConnectionId, bind.ChannelId, bind.Token, out var pending, writer.ProtocolVersion) ||
                            pending is null)
                        {
                            await TryRejectAsync(writer, deadline.Token);
                            return;
                        }

                        await writer.WriteAsync(new Frame(FrameType.BindAccepted, JsonProtocolSerializer.Serialize(new BindAcceptedMessage(bind.ConnectionId))), deadline.Token);
                        if (!pending.TrySetTunnel(new DataTunnel(stream, reader, writer, client.Client)))
                        {
                            return;
                        }
                        boundPending = pending;
                    }
                    else if (first?.Type == FrameType.PeerBindData)
                    {
                        var bind = JsonProtocolSerializer.Deserialize<PeerBindDataMessage>(first.Payload.Span);
                        if (bind is null || !peerRelays.TryBind(bind, new DataTunnel(stream, reader, writer, client.Client), out var relay) || relay is null)
                        {
                            await TryRejectAsync(writer, deadline.Token);
                            return;
                        }
                        boundRelay = relay;
                    }
                    else
                    {
                        await TryRejectAsync(writer, deadline.Token);
                        return;
                    }
                }

                unauthenticatedLimit.Release();
                released = true;
                if (boundPending is not null) await boundPending.WaitForCompletionAsync();
                else if (boundRelay is not null) await boundRelay.WaitForCompletionAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ProtocolException or OperationCanceledException or SocketException)
        {
            logger.LogDebug(exception, "Data connection ended during bind or relay.");
        }
        finally
        {
            if (!released) unauthenticatedLimit.Release();
        }
        }
        finally
        {
            if (!acquired) client.Dispose();
        }
    }

    private static async Task TryRejectAsync(FrameWriter writer, CancellationToken cancellationToken)
    {
        try { await writer.WriteAsync(new Frame(FrameType.Error, JsonProtocolSerializer.Serialize(new ErrorMessage(ErrorCode.TokenInvalid))), cancellationToken); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
    }
}
