using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayLink.Server.Automation;

/// <summary>
/// 设备密钥证明（X-RelayLink-Proof）。
/// 形状参照 RFC 9449 的 JWT 头/载荷结构，但不是该规范的实现，因此不宣称规范符合性。
/// 头为 {"alg":"ES256","jwk":{...}}，载荷含 htm/htu/ath/iat/exp/jti，配对阶段另加
/// requestId/instanceId/userCodeDigest。
/// </summary>
public static class DeviceProof
{
    public const string HeaderName = "X-RelayLink-Proof";
    public const int MaxTokenBytes = 8 * 1024;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    public sealed record Proof(string? PublicKeyJwk, string? KeyThumbprint, string? Method, string? Uri, string? TokenHash,
        DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string? Jti, string? InstanceId, string? RequestId, string? UserCodeDigest);

    public sealed record ValidationError(string Code, string Message);

    /// <summary>计算 EC 公钥的 JWK thumbprint（RFC 7638 形状，SHA-256）。</summary>
    public static string ThumbprintOf(ECDsa publicKey)
    {
        var parameters = publicKey.ExportParameters(false);
        var canonical = Canonicalize(parameters);
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Canonicalize(ECParameters parameters) =>
        "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + Base64UrlEncode(parameters.Q.X!) +
        "\",\"y\":\"" + Base64UrlEncode(parameters.Q.Y!) + "\"}";

    /// <summary>从 JWK 文本导入 P-256 公钥。</summary>
    public static ECDsa? ImportPublicKey(string jwkJson)
    {
        try
        {
            using var document = JsonDocument.Parse(jwkJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("kty", out var kty) || kty.GetString() != "EC") return null;
            if (!root.TryGetProperty("crv", out var crv) || crv.GetString() != "P-256") return null;
            if (!root.TryGetProperty("x", out var x) || !root.TryGetProperty("y", out var y)) return null;
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Base64UrlDecode(x.GetString()!), Y = Base64UrlDecode(y.GetString()!) }
            });
            return key;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析并校验签名与时间窗。重放（jti）由调用方在缓存层判断，因为它需要按主体分桶。
    /// </summary>
    public static bool TryParse(string? header, ECDsa publicKey, DateTimeOffset nowUtc, string? expectedThumbprint,
        out Proof? proof, out ValidationError? error)
    {
        proof = null;
        error = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            error = new ValidationError("proof_invalid", "缺少 X-RelayLink-Proof 请求头。");
            return false;
        }

        if (Encoding.UTF8.GetByteCount(header) > MaxTokenBytes)
        {
            error = new ValidationError("proof_invalid", "证明过大。");
            return false;
        }

        var parts = header.Split('.');
        if (parts.Length != 3)
        {
            error = new ValidationError("proof_invalid", "证明不是三段式。");
            return false;
        }

        byte[] signingInput;
        try
        {
            signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        }
        catch (ArgumentException)
        {
            error = new ValidationError("proof_invalid", "证明编码无效。");
            return false;
        }

        byte[] signature;
        try { signature = Base64UrlDecode(parts[2]); }
        catch (FormatException) { error = new ValidationError("proof_invalid", "证明签名编码无效。"); return false; }

        if (!publicKey.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            error = new ValidationError("proof_invalid", "证明签名校验失败。");
            return false;
        }

        JsonDocument headerDocument;
        JsonDocument payloadDocument;
        try
        {
            headerDocument = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            payloadDocument = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            error = new ValidationError("proof_invalid", "证明头或载荷无法解析。");
            return false;
        }

        using (headerDocument)
        using (payloadDocument)
        {
            var headerRoot = headerDocument.RootElement;
            if (!headerRoot.TryGetProperty("alg", out var algorithm) || algorithm.GetString() != "ES256")
            {
                error = new ValidationError("proof_invalid", "证明必须使用 ES256。");
                return false;
            }

            // 头里的 jwk 必须与用来验签的公钥一致，否则等于没绑定设备。
            if (!headerRoot.TryGetProperty("jwk", out var jwkElement))
            {
                error = new ValidationError("proof_invalid", "证明头缺少 jwk。");
                return false;
            }

            using var jwkKey = ImportPublicKey(jwkElement.GetRawText());
            if (jwkKey is null)
            {
                error = new ValidationError("proof_invalid", "证明头中的 jwk 不是有效的 P-256 公钥。");
                return false;
            }

            var thumbprint = ThumbprintOf(jwkKey);
            if (expectedThumbprint is not null && !string.Equals(thumbprint, expectedThumbprint, StringComparison.Ordinal))
            {
                error = new ValidationError("proof_invalid", "证明所用公钥与该令牌绑定的设备不一致。");
                return false;
            }

            var payload = payloadDocument.RootElement;
            var issuedAt = ReadTime(payload, "iat");
            var expiresAt = ReadTime(payload, "exp");
            if (issuedAt is null || expiresAt is null)
            {
                error = new ValidationError("proof_invalid", "证明缺少 iat 或 exp。");
                return false;
            }

            // exp-iat 严格 ≤ 60s，与错误消息和设计一致。过期容忍由下方的 skew 检查承担，
            // 不在这里放宽上限；jti 重放缓存保留期已按 60s + skew 计算覆盖。
            if (expiresAt.Value <= issuedAt.Value || expiresAt.Value - issuedAt.Value > TimeSpan.FromSeconds(60))
            {
                error = new ValidationError("proof_invalid", "证明有效期必须为正且不超过 60 秒。");
                return false;
            }

            if (issuedAt.Value > nowUtc + ClockSkew || expiresAt.Value < nowUtc - ClockSkew)
            {
                error = new ValidationError("proof_invalid",
                    $"证明不在有效窗口内；服务端当前时间为 {nowUtc:O}，请校正设备时钟。");
                return false;
            }

            var jti = ReadString(payload, "jti");
            if (string.IsNullOrWhiteSpace(jti) || jti.Length > 128)
            {
                error = new ValidationError("proof_invalid", "证明缺少有效的 jti。");
                return false;
            }

            proof = new Proof(jwkElement.GetRawText(), thumbprint, ReadString(payload, "htm"), ReadString(payload, "htu"),
                ReadString(payload, "ath"), issuedAt.Value, expiresAt.Value, jti, ReadString(payload, "instanceId"),
                ReadString(payload, "requestId"), ReadString(payload, "userCodeDigest"));
            return true;
        }
    }

    /// <summary>证明中的 htu 必须与方法+路径（含查询串）一致。</summary>
    public static bool UriMatches(Proof proof, string method, string path, string? queryString)
    {
        if (!string.Equals(proof.Method, method, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = string.IsNullOrEmpty(path) ? "/" : path;
        if (!string.IsNullOrEmpty(queryString)) expected += "?" + queryString;
        return string.Equals(proof.Uri, expected, StringComparison.Ordinal);
    }

    public static string HashToken(ReadOnlySpan<byte> tokenBytes) => Base64UrlEncode(SHA256.HashData(tokenBytes));

    public static string Base64UrlEncode(byte[] bytes) => Base64Url.EncodeToString(bytes);

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("Invalid base64url length.") };
        return Convert.FromBase64String(padded);
    }

    private static DateTimeOffset? ReadTime(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element) || !element.TryGetInt64(out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
