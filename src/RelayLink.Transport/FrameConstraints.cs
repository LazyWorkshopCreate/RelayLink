using RelayLink.Protocol;

namespace RelayLink.Transport;

public static class FrameConstraints
{
    public static void ValidateForWrite(Frame frame)
    {
        if (frame.Flags != 0)
        {
            throw new ProtocolException("Frame flags must be zero.");
        }

        if (frame.Payload.Length > ProtocolConstants.MaxControlPayloadLength)
        {
            throw new ProtocolException("Frame payload exceeds its allowed limit.");
        }

        ValidateLength(frame.Type, frame.Payload.Length);
    }

    public static void ValidateLength(FrameType type, int length)
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
}
