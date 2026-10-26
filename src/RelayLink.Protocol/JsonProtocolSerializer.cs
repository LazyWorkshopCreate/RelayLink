using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

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

    public static byte[] Serialize<T>(T value, byte protocolVersion)
    {
        if (protocolVersion == ProtocolConstants.Version) return Serialize(value);
        if (protocolVersion != 3) throw new ProtocolException("Unsupported protocol version.");
        var node = JsonSerializer.SerializeToNode(value, Options);
        RemoveUdpFields(node);
        return JsonSerializer.SerializeToUtf8Bytes(node, Options);
    }

    private static void RemoveUdpFields(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("transportProtocol");
            obj.Remove("udpIdleTimeoutSeconds");
            foreach (var property in obj) RemoveUdpFields(property.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array) RemoveUdpFields(item);
    }

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
