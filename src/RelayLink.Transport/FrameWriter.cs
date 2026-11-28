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

    public ValueTask WriteAsync(Frame frame, CancellationToken cancellationToken) =>
        WriteFrameAsync(frame, cancellationToken, cancellationToken);

    // The caller can stop waiting at its deadline even if we must finish a frame on the shared stream.
    public async ValueTask WriteControlAsync(Frame frame, CancellationToken operationToken, CancellationToken connectionToken)
    {
        var writing = WriteFrameAsync(frame, operationToken, connectionToken).AsTask();
        try { await writing.WaitAsync(operationToken); }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            // WriteFrameAsync owns the write lock and records any partial-frame fault for the next writer.
            _ = ObserveCompletionAsync(writing);
            throw;
        }
        operationToken.ThrowIfCancellationRequested();
    }

    private static async Task ObserveCompletionAsync(Task write) =>
        await write.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private async ValueTask WriteFrameAsync(Frame frame, CancellationToken queueCancellation, CancellationToken connectionCancellation)
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellation);
        timeout.CancelAfter(blockedWriteTimeout);
        using var queue = queueCancellation != connectionCancellation
            ? CancellationTokenSource.CreateLinkedTokenSource(queueCancellation, timeout.Token) : null;
        var queueToken = queue?.Token ?? timeout.Token;
        await writeLock.WaitAsync(queueToken);
        try
        {
            queueToken.ThrowIfCancellationRequested();
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
