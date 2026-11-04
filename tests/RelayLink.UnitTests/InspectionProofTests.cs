using System.Text;
using RelayLink.Protocol;

namespace RelayLink.UnitTests;

public sealed class InspectionProofTests
{
    [Fact]
    public void Proof_binds_role_connection_and_mode_and_stays_off_old_peer_messages()
    {
        var secret = Convert.ToBase64String(new byte[32]);
        var connection = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var challenge = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var caller = InspectionProof.Compute(secret, "caller", "caller-a", "ssh", connection, challenge);
        var target = InspectionProof.Compute(secret, "target", "caller-a", "ssh", connection, challenge);
        Assert.Equal(32, caller.Length);
        Assert.NotEqual(caller, target);
        Assert.NotEqual(caller, InspectionProof.Compute(secret, "caller", "caller-a", "ssh", Guid.NewGuid(), challenge));

        var message = new PeerOpenMessage(connection, connection, "caller-a", "ssh", "token", true, "tcp") { InspectionMode = "server-peer" };
        var current = Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(message, 6));
        var legacy = Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(message, 5));
        Assert.Contains("\"inspectionMode\":\"server-peer\"", current);
        Assert.DoesNotContain("inspectionMode", legacy);
        var start = Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(new StartMessage(connection) { LegTls = true }, 5));
        Assert.DoesNotContain("legTls", start);
        Assert.Contains("\"legTls\":true", Encoding.UTF8.GetString(JsonProtocolSerializer.Serialize(new StartMessage(connection) { LegTls = true }, 6)));
    }
}
