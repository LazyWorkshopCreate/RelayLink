using System.Buffers.Binary;
using RelayLink.Protocol;

namespace RelayLink.Transport;

public sealed class FrameReader(Stream stream, TimeSpan? partialFrameTimeout = null, TimeProvider? timeProvider = null)
{
    public byte? ProtocolVersion { get; private set; }
    private readonly TimeSpan partialFrameTimeout = partialFrameTimeout ?? TimeSpan.FromSeconds(120);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    public async ValueTask<Frame?> ReadAsync(int maximumPayloadLength, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPayloadLength);
        var header = new byte[ProtocolConstants.HeaderLength];
        var headerRead = await ReadAtLeastAsync(header, allowEndOfStream: true, startTimeoutImmediately: false, cancellationToken);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead != header.Length)
        {
            throw new ProtocolException("Truncated frame header.");
        }

        if (header[0] != (byte)'N' || header[1] != (byte)'T' || header[2] != (byte)'P' || header[3] != (byte)'1')
        {
            throw new ProtocolException("Invalid frame magic.");
        }

        if (header[4] is not (3 or 4 or ProtocolConstants.Version) ||
            (ProtocolVersion is { } version && header[4] != version))
        {
            throw new ProtocolException("Unsupported protocol version.");
        }

        ProtocolVersion = header[4];
        var type = (FrameType)header[5];
        if (!Enum.IsDefined(type))
        {
            throw new ProtocolException("Unknown frame type.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
        if (flags != 0)
        {
            throw new ProtocolException("Frame flags must be zero.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (payloadLength > maximumPayloadLength || payloadLength > int.MaxValue)
        {
            throw new ProtocolException("Frame payload exceeds its allowed limit.");
        }

        FrameConstraints.ValidateLength(type, (int)payloadLength);
        var payload = new byte[(int)payloadLength];
        if (payload.Length > 0 && await ReadAtLeastAsync(payload, allowEndOfStream: false, startTimeoutImmediately: true, cancellationToken) != payload.Length)
        {
            throw new ProtocolException("Truncated frame payload.");
        }

        return new Frame(type, payload, flags);
    }

    private async ValueTask<int> ReadAtLeastAsync(Memory<byte> buffer, bool allowEndOfStream, bool startTimeoutImmediately, CancellationToken cancellationToken)
    {
        var total = 0;
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedTimeout = null;
        try
        {
            if (startTimeoutImmediately)
            {
                timeoutCts = new CancellationTokenSource(partialFrameTimeout, timeProvider);
                linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            }
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[total..], linkedTimeout?.Token ?? cancellationToken);
                if (read == 0)
                {
                    if (total == 0 && allowEndOfStream)
                    {
                        return 0;
                    }

                    return total;
                }

                total += read;
                if (total < buffer.Length)
                {
                    if (timeoutCts is null)
                    {
                        timeoutCts = new CancellationTokenSource(partialFrameTimeout, timeProvider);
                        linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    }
                    else
                    {
                        timeoutCts.CancelAfter(partialFrameTimeout);
                    }
                }
            }

            return total;
        }
        finally
        {
            linkedTimeout?.Dispose();
            timeoutCts?.Dispose();
        }
    }
}
