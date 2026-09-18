using System.Net.Sockets;
using RelayLink.Protocol;

namespace RelayLink.Transport;

public static class RelayPump
{
    public static async Task CompleteBidirectionalAsync(Task firstDirection, Task secondDirection, Action abort)
    {
        var firstCompleted = await Task.WhenAny(firstDirection, secondDirection);
        if (firstCompleted.IsFaulted || firstCompleted.IsCanceled)
        {
            abort();
        }

        await Task.WhenAll(firstDirection, secondDirection);
    }

    public static async Task CopySocketToFramesAsync(Socket source, FrameWriter writer, CancellationToken cancellationToken, Action<int>? bytesWritten = null)
    {
        var buffer = new byte[ProtocolConstants.MaxDataPayloadLength];
        while (true)
        {
            var read = await source.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (read == 0)
            {
                await writer.WriteAsync(new Frame(FrameType.Fin, ReadOnlyMemory<byte>.Empty), cancellationToken);
                return;
            }

            await writer.WriteAsync(new Frame(FrameType.Data, buffer.AsMemory(0, read).ToArray()), cancellationToken);
            bytesWritten?.Invoke(read);
        }
    }

    public static async Task CopyFramesToSocketAsync(FrameReader reader, Socket destination, CancellationToken cancellationToken, Action<int>? bytesWritten = null)
    {
        while (true)
        {
            var frame = await reader.ReadAsync(ProtocolConstants.MaxDataPayloadLength, cancellationToken)
                ?? throw new IOException("Data tunnel ended before FIN.");
            switch (frame.Type)
            {
                case FrameType.Data:
                    await SendAllAsync(destination, frame.Payload, cancellationToken);
                    bytesWritten?.Invoke(frame.Payload.Length);
                    break;
                case FrameType.Fin:
                    destination.Shutdown(SocketShutdown.Send);
                    return;
                case FrameType.Reset:
                    throw new IOException("Data tunnel was reset by its peer.");
                default:
                    throw new ProtocolException($"Unexpected data-plane frame {frame.Type}.");
            }
        }
    }

    private static async Task SendAllAsync(Socket destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        while (!payload.IsEmpty)
        {
            var sent = await destination.SendAsync(payload, SocketFlags.None, cancellationToken);
            if (sent == 0) throw new IOException("Socket closed during send.");
            payload = payload[sent..];
        }
    }
}
