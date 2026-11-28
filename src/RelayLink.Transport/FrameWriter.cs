using System.Buffers.Binary;
using RelayLink.Protocol;

namespace RelayLink.Transport;

public sealed class FrameWriter(Stream stream, TimeSpan? blockedWriteTimeout = null, byte protocolVersion = ProtocolConstants.Version)
{
    public byte ProtocolVersion { get; } = protocolVersion is 3 or 4 or ProtocolConstants.Version
        ? protocolVersion : throw new ArgumentOutOfRangeException(nameof(protocolVersion));
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly TimeSpan blockedWriteTimeout = blockedWriteTimeout ?? TimeSpan.FromSeconds(120);
    private Exception? faultReason;

    public async ValueTask WriteAsync(Frame frame, CancellationToken cancellationToken)
    {
        FrameConstraints.ValidateForWrite(frame);
        if (Volatile.Read(ref faultReason) is { } priorFault)
            throw new FrameWriteException("A control frame writer is faulted.", priorFault);

        var header = new byte[ProtocolConstants.HeaderLength];
        "NTP1"u8.CopyTo(header);
        header[4] = ProtocolVersion;
        header[5] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), frame.Flags);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), checked((uint)frame.Payload.Length));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(blockedWriteTimeout);
        await writeLock.WaitAsync(timeout.Token);
        try
        {
            if (Volatile.Read(ref faultReason) is { } lockFault)
                throw new FrameWriteException("A control frame writer is faulted.", lockFault);
            try
            {
                await stream.WriteAsync(header, timeout.Token);
                if (!frame.Payload.IsEmpty)
                    await stream.WriteAsync(frame.Payload, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref faultReason, exception);
                throw new FrameWriteException("A control frame write did not finish.", exception);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }
}

public sealed class FrameWriteException(string message, Exception inner) : IOException(message, inner);
