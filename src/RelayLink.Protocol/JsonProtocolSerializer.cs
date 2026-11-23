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
        if (protocolVersion is not (3 or 4)) throw new ProtocolException("Unsupported protocol version.");
        var node = JsonSerializer.SerializeToNode(value, Options);
        RemoveNewFields(node, protocolVersion);
        return JsonSerializer.SerializeToUtf8Bytes(node, Options);
    }

    private static void RemoveNewFields(JsonNode? node, byte version)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("applicationProtocol");
            obj.Remove("http");
            obj.Remove("httpIngress");
            if (version == 3) { obj.Remove("transportProtocol"); obj.Remove("udpIdleTimeoutSeconds"); }
            foreach (var property in obj) RemoveNewFields(property.Value, version);
        }
        else if (node is JsonArray array)
            foreach (var item in array) RemoveNewFields(item, version);
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
