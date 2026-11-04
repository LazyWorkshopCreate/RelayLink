using System.Security.Cryptography;
using System.Text;

namespace RelayLink.Protocol;

public static class InspectionProof
{
    public static byte[] Compute(string base64Secret, string role, string callerId, string channelId, Guid connectionId, ReadOnlySpan<byte> challenge)
    {
        var context = Encoding.UTF8.GetBytes($"RelayLink inspection proof v1\0server-peer\0{role}\0{callerId}\0{channelId}\0{connectionId:N}\0");
        var payload = new byte[context.Length + challenge.Length];
        context.CopyTo(payload, 0);
        challenge.CopyTo(payload.AsSpan(context.Length));
        return HMACSHA256.HashData(Convert.FromBase64String(base64Secret), payload);
    }
}
