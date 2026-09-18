using System.Buffers.Binary;
using RelayLink.Protocol;

namespace RelayLink.Transport;

public sealed class FrameReader(Stream stream, TimeSpan? partialFrameTimeout = null)
{
    private readonly TimeSpan partialFrameTimeout = partialFrameTimeout ?? TimeSpan.FromSeconds(120);
    public async ValueTask<Frame?> ReadAsync(int maximumPayloadLength, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPayloadLength);
        var header = new byte[ProtocolConstants.HeaderLength];
        var headerRead = await ReadAtLeastAsync(header, allowEndOfStream: true, cancellationToken);
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

        if (header[4] != ProtocolConstants.Version)
        {
            throw new ProtocolException("Unsupported protocol version.");
        }

        var type = (FrameType)header[5];
        if (!Enum.IsDefined(type))
        {
            throw new ProtocolException("Unknown frame type.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
        if (flags != 0)
        {
            throw new ProtocolException("Frame flags must be zero for protocol v1.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (payloadLength > maximumPayloadLength || payloadLength > int.MaxValue)
        {
            throw new ProtocolException("Frame payload exceeds its allowed limit.");
        }

        ValidateLength(type, (int)payloadLength);
        var payload = new byte[(int)payloadLength];
        if (payload.Length > 0 && await ReadAtLeastAsync(payload, allowEndOfStream: false, cancellationToken) != payload.Length)
        {
            throw new ProtocolException("Truncated frame payload.");
        }

        return new Frame(type, payload, flags);
    }

    private static void ValidateLength(FrameType type, int length)
    {
        if (type == FrameType.Data && (length < 1 || length > ProtocolConstants.MaxDataPayloadLength))
        {
            throw new ProtocolException("DATA payload length is invalid.");
        }

        if (type == FrameType.Fin && length != 0)
        {
            throw new ProtocolException("FIN payload must be empty.");
        }

        if ((type == FrameType.Error || type == FrameType.Reset) && length > ProtocolConstants.MaxErrorPayloadLength)
        {
            throw new ProtocolException("Error payload length is invalid.");
        }
    }

    private async ValueTask<int> ReadAtLeastAsync(Memory<byte> buffer, bool allowEndOfStream, CancellationToken cancellationToken)
    {
        var total = 0;
        CancellationTokenSource? partialTimeout = null;
        try
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[total..], partialTimeout?.Token ?? cancellationToken);
                if (read == 0)
                {
                    if (total == 0 && allowEndOfStream)
                    {
                        return 0;
                    }

                    return total;
                }

                total += read;
                if (total < buffer.Length && partialTimeout is null)
                {
                    partialTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    partialTimeout.CancelAfter(partialFrameTimeout);
                }
            }

            return total;
        }
        finally
        {
            partialTimeout?.Dispose();
        }
    }
}
