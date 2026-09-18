using System.Text.Json;
using RelayLink.Server.Configuration;

namespace RelayLink.Server.Runtime;

public static class AgentConfigurationFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static byte[] Create(ServerConfiguration server, ClientConfiguration client)
    {
        var tunnel = server.Tunnel;
        var trustedCaPemBase64 = tunnel.TlsEnabled
            ? Convert.ToBase64String(File.ReadAllBytes(tunnel.TrustedCaPemPath!))
            : null;
        var agent = new
        {
            serverHost = client.AgentServerHost ?? tunnel.DefaultAgentServerHost,
            serverPort = tunnel.Port,
            dataPort = tunnel.EffectiveDataPort,
            useTls = tunnel.TlsEnabled,
            clientId = client.ClientId,
            trustedCaPemBase64,
            secret = client.Secret,
            reconnect = new { initialDelaySeconds = 1, maxDelaySeconds = 30, permanentErrorDelaySeconds = 60 }
        };
        return JsonSerializer.SerializeToUtf8Bytes(agent, JsonOptions);
    }
}
