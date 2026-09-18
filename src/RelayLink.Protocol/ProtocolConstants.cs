namespace RelayLink.Protocol;

public static class ProtocolConstants
{
    public const string Magic = "NTP1";
    public const byte Version = 1;
    public const int HeaderLength = 12;
    public const int MaxInitialPayloadLength = 8 * 1024;
    public const int MaxControlPayloadLength = 256 * 1024;
    public const int MaxDataPayloadLength = 32 * 1024;
    public const int MaxErrorPayloadLength = 1024;
}
