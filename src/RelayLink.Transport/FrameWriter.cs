using System.Buffers.Binary;
using RelayLink.Protocol;

namespace RelayLink.Transport;

public sealed class FrameWriter(Stream stream, TimeSpan? blockedWriteTimeout = null)
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly TimeSpan blockedWriteTimeout = blockedWriteTimeout ?? TimeSpan.FromSeconds(120);

    public async ValueTask WriteAsync(Frame frame, CancellationToken cancellationToken)
    {
        if (frame.Flags != 0 || frame.Payload.Length > ProtocolConstants.MaxControlPayloadLength)
        {
            throw new ProtocolException("Invalid frame to write.");
        }

        var header = new byte[ProtocolConstants.HeaderLength];
        "NTP1"u8.CopyTo(header);
        header[4] = ProtocolConstants.Version;
        header[5] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), frame.Flags);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), checked((uint)frame.Payload.Length));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(blockedWriteTimeout);
        await writeLock.WaitAsync(timeout.Token);
        try
        {
            await stream.WriteAsync(header, timeout.Token);
            if (!frame.Payload.IsEmpty)
            {
                await stream.WriteAsync(frame.Payload, timeout.Token);
            }

            await stream.FlushAsync(timeout.Token);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
