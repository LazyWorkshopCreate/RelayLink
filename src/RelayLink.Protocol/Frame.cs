namespace RelayLink.Protocol;

public sealed record Frame(FrameType Type, ReadOnlyMemory<byte> Payload, ushort Flags = 0)
{
    public bool IsData => Type == FrameType.Data;
}
