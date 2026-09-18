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
                _ = HandleAcceptedAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task HandleAcceptedAsync(TcpClient client, CancellationToken stoppingToken)
    {
        if (!await unauthenticatedLimit.WaitAsync(0, stoppingToken))
        {
            client.Dispose();
            return;
        }

        var released = false;
        try
        {
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(runtime.Configuration.Server.Tunnel.HandshakeTimeoutSeconds));
                var stream = client.GetStream();
                var reader = new FrameReader(stream);
                var writer = new FrameWriter(stream);
                var first = await reader.ReadAsync(ProtocolConstants.MaxInitialPayloadLength, deadline.Token);
                if (first?.Type == FrameType.BindData)
                {
                    var bind = JsonProtocolSerializer.Deserialize<BindDataMessage>(first.Payload.Span);
                    if (!pendingConnections.TryBind(bind.SessionId, bind.ConnectionId, bind.ChannelId, bind.Token, out var pending) ||
                        pending is null || !pending.TrySetTunnel(new DataTunnel(stream, reader, writer, client.Client)))
                    {
                        await TryRejectAsync(writer, deadline.Token);
                        return;
                    }

                    await writer.WriteAsync(new Frame(FrameType.BindAccepted, JsonProtocolSerializer.Serialize(new BindAcceptedMessage(bind.ConnectionId))), deadline.Token);
                    unauthenticatedLimit.Release();
                    released = true;
                    await pending.WaitForCompletionAsync();
                    return;
                }

                if (first?.Type == FrameType.PeerBindData)
                {
                    var bind = JsonProtocolSerializer.Deserialize<PeerBindDataMessage>(first.Payload.Span);
                    if (!peerRelays.TryBind(bind, new DataTunnel(stream, reader, writer, client.Client), out var relay) || relay is null)
                    {
                        await TryRejectAsync(writer, deadline.Token);
                        return;
                    }

                    unauthenticatedLimit.Release();
                    released = true;
                    await relay.WaitForCompletionAsync();
                    return;
                }

                await TryRejectAsync(writer, deadline.Token);
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

    private static async Task TryRejectAsync(FrameWriter writer, CancellationToken cancellationToken)
    {
        try { await writer.WriteAsync(new Frame(FrameType.Error, JsonProtocolSerializer.Serialize(new ErrorMessage(ErrorCode.TokenInvalid))), cancellationToken); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
    }
}
