using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayLink.Protocol;

public static class JsonProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Options)
                ?? throw new ProtocolException("Protocol JSON payload was null.");
        }
        catch (JsonException exception)
        {
            throw new ProtocolException($"Invalid protocol JSON: {exception.Message}");
        }
    }
}
