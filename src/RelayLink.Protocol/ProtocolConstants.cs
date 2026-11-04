namespace RelayLink.Protocol;

public static class ProtocolConstants
{
    public const string Magic = "NTP1";
    public const byte Version = 7;
    public const byte InspectionVersion = 7;

    public static bool IsSupported(byte version) => version is 3 or 4 or 5 or 6 or 7;
    public const int HeaderLength = 12;
    public const int MaxInitialPayloadLength = 8 * 1024;
    public const int MaxControlPayloadLength = 256 * 1024;
    public const int MaxDataPayloadLength = 32 * 1024;
    public const int MaxErrorPayloadLength = 1024;
}
