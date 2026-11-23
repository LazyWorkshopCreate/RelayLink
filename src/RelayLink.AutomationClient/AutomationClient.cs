using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayLink.AutomationClient;

/// <summary>
/// 设备密钥：EC P-256，私钥只落本机受保护存储。
/// 每次调用用它签名生成 X-RelayLink-Proof，使令牌无法被复制到另一台机器使用。
/// </summary>
public sealed class DeviceKey : IDisposable
{
    private readonly ECDsa key;

    private DeviceKey(ECDsa key, string privateKeyPkcs8Base64) => (this.key, PrivateKeyPkcs8Base64) = (key, privateKeyPkcs8Base64);

    /// <summary>PKCS#8 私钥，Base64 编码。由调用方存入凭据库或受保护文件。</summary>
    public string PrivateKeyPkcs8Base64 { get; }

    public static DeviceKey Generate()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new DeviceKey(key, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
    }

    public static DeviceKey FromPrivateKey(string privateKeyPkcs8Base64)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        return new DeviceKey(key, privateKeyPkcs8Base64);
    }

    /// <summary>RFC 7638 形状的 JWK thumbprint（P-256，SHA-256）。</summary>
    public string Thumbprint
    {
        get
        {
            var parameters = key.ExportParameters(false);
            var canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + Base64UrlEncode(parameters.Q.X!) +
                            "\",\"y\":\"" + Base64UrlEncode(parameters.Q.Y!) + "\"}";
            return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    /// <summary>公钥 JWK（JSON）。服务端只用它验签。</summary>
    public string PublicKeyJwk
    {
        get
        {
            var parameters = key.ExportParameters(false);
            return "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + Base64UrlEncode(parameters.Q.X!) +
                   "\",\"y\":\"" + Base64UrlEncode(parameters.Q.Y!) + "\"}";
        }
    }

    /// <summary>
    /// 生成证明。头 {"alg":"ES256","jwk":...}，载荷按需带 htm/htu/ath/iat/exp/jti。
    /// 这是 RFC 9449 形状的自定协议，不是该规范的实现。
    /// </summary>
    public string CreateProof(IReadOnlyDictionary<string, object?> claims)
    {
        var header = "{\"alg\":\"ES256\",\"typ\":\"relaylink-proof+jwt\",\"jwk\":" + PublicKeyJwk + "}";
        var jti = claims.TryGetValue("jti", out var provided) && provided is string text ? text : Guid.NewGuid().ToString();
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var payloadBuilder = new StringBuilder();
        payloadBuilder.Append('{');
        payloadBuilder.Append("\"iat\":").Append(issuedAt);
        payloadBuilder.Append(",\"exp\":").Append(issuedAt + 60);
        payloadBuilder.Append(",\"jti\":").Append(JsonSerializer.Serialize(jti));
        foreach (var (name, value) in claims)
        {
            if (name == "jti") continue;
            payloadBuilder.Append(',').Append(JsonSerializer.Serialize(name)).Append(':');
            payloadBuilder.Append(JsonSerializer.Serialize(value));
        }

        payloadBuilder.Append('}');

        var signingInput = Base64UrlEncode(Encoding.UTF8.GetBytes(header)) + "." +
                           Base64UrlEncode(Encoding.UTF8.GetBytes(payloadBuilder.ToString()));
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64UrlEncode(signature);
    }

    public void Dispose() => key.Dispose();

    internal static string Base64UrlEncode(byte[] bytes) => Base64Url.EncodeToString(bytes);
}

/// <summary>
/// 自动化 API 客户端。CLI 与 .NET SDK 管理面共用同一份实现，避免两套证明与固定逻辑。
/// 证书固定：配对前可通过 --accept-spki 固定，之后每次连接校验。
/// </summary>
public sealed class AutomationClient : IDisposable
{
    public const string ApiVersionHeader = "X-RelayLink-Api-Version";
    public const string ProofHeader = "X-RelayLink-Proof";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const int SupportedApiVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient http;
    private readonly Uri baseUri;

    /// <summary>
    /// 服务端持久化实例标识。证明里必须带上它，且与服务端一致；
    /// 它来自 /info（匿名可达），配对时也会写进 profile。
    /// </summary>
    public string? AutomationInstanceId { get; set; }

    public AutomationClient(string baseUrl, string? pinnedSpkiSha256 = null, bool acceptAny = false)
    {
        baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        var handler = new HttpClientHandler();
        if (acceptAny)
        {
            // 仅用于首次配对前核对指纹；配对后必须使用固定值。
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is not null && pinnedSpkiSha256 is not null)
                {
                    var spki = Base64UrlEncode(certificate.GetPublicKey());
                    return string.Equals(spki, pinnedSpkiSha256, StringComparison.Ordinal);
                }

                return pinnedSpkiSha256 is null;
            };
        }
        else if (pinnedSpkiSha256 is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && string.Equals(Base64UrlEncode(certificate.GetPublicKey()), pinnedSpkiSha256, StringComparison.Ordinal);
        }

        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add(ApiVersionHeader, SupportedApiVersion.ToString());
    }

    public sealed record ApiError(string Code, string Message, bool Retryable, string? RequestId);

    public sealed record ApiResult(bool Ok, JsonElement? Data, ApiError? Error, HttpStatusCode Status);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, DeviceKey? device, string? token, object? body,
        string? idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path.TrimStart('/')));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (idempotencyKey is not null)
            request.Headers.Add(IdempotencyHeader, idempotencyKey);

        if (device is not null)
        {
            // htu 用未转义路径：服务端从 request.Path（已解码）比对。
            // 若用 PathAndQuery（转义形态），含非 URL 安全字符的 clientId 会恒 proof_invalid。
            var unescapedPath = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var htu = string.IsNullOrEmpty(request.RequestUri.Query)
                ? unescapedPath
                : unescapedPath + request.RequestUri.Query;
            var claims = new Dictionary<string, object?>
            {
                ["htm"] = method.Method,
                ["htu"] = htu
            };
            if (token is not null)
                claims["ath"] = Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            // instanceId 必须有值：服务端会拒绝缺失或不匹配的证明。
            if (!string.IsNullOrEmpty(AutomationInstanceId))
                claims["instanceId"] = AutomationInstanceId;
            request.Headers.Add(ProofHeader, device.CreateProof(claims));
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return new ApiResult(response.IsSuccessStatusCode, null, null, response.StatusCode);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();
        if (response.IsSuccessStatusCode)
        {
            return root.TryGetProperty("data", out var data)
                ? new ApiResult(true, data, null, response.StatusCode)
                : new ApiResult(true, root, null, response.StatusCode);
        }

        if (root.TryGetProperty("error", out var error))
        {
            return new ApiResult(false, null, new ApiError(
                error.TryGetProperty("code", out var code) ? code.GetString() ?? "unknown" : "unknown",
                error.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                error.TryGetProperty("retryable", out var retryable) && retryable.ValueKind == JsonValueKind.True,
                error.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null), response.StatusCode);
        }

        return new ApiResult(false, null, new ApiError("unknown", text, false, null), response.StatusCode);
    }

    // ---------------- 匿名 ----------------

    public Task<ApiResult> GetInfoAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/automation/info", null, null, null, null, cancellationToken);

    public Task<ApiResult> CreatePairingAsync(string subject, string platform, string cliVersion, string[] scopes,
        string devicePublicKeyJwk, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/v1/automation/pairing/requests", null, null,
            new { subject, platform, cliVersion, scopes, devicePublicKey = devicePublicKeyJwk }, null, cancellationToken);

    // ---------------- 配对 ----------------

    public async Task<ApiResult> QueryPairingAsync(Guid requestId, string userCode, DeviceKey device, CancellationToken cancellationToken)
    {
        var digest = UserCodeDigest(userCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"api/v1/automation/pairing/requests/{requestId}/status"))
        {
            // 服务端 status 端点要求 body.userCode 与 proof.userCodeDigest 双重一致；
            // 只发 proof 不带 body 会被判 pairing_denied（用户码不正确）。
            Content = new StringContent(JsonSerializer.Serialize(new { userCode }, Json), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(ProofHeader, device.CreateProof(new Dictionary<string, object?>
        {
            ["requestId"] = requestId.ToString(),
            ["userCodeDigest"] = digest
        }));
        return await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiResult> ExchangeTokenAsync(Guid requestId, string userCode, DeviceKey device, CancellationToken cancellationToken)
    {
        var digest = UserCodeDigest(userCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"api/v1/automation/pairing/requests/{requestId}/token"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { userCode }, Json), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(ProofHeader, device.CreateProof(new Dictionary<string, object?>
        {
            ["requestId"] = requestId.ToString(),
            ["userCodeDigest"] = digest
        }));
        return await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiResult> CancelPairingAsync(Guid requestId, DeviceKey device, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(baseUri, $"api/v1/automation/pairing/requests/{requestId}"));
        request.Headers.Add(ProofHeader, device.CreateProof(new Dictionary<string, object?>
        {
            ["requestId"] = requestId.ToString()
        }));
        return await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiResult> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(ApiVersionHeader, SupportedApiVersion.ToString());
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new ApiResult(response.IsSuccessStatusCode, null, null, response.StatusCode);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();
        if (response.IsSuccessStatusCode)
            return root.TryGetProperty("data", out var data)
                ? new ApiResult(true, data, null, response.StatusCode)
                : new ApiResult(true, root, null, response.StatusCode);

        return root.TryGetProperty("error", out var error)
            ? new ApiResult(false, null, new ApiError(
                error.TryGetProperty("code", out var code) ? code.GetString() ?? "unknown" : "unknown",
                error.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                error.TryGetProperty("retryable", out var retryable) && retryable.ValueKind == JsonValueKind.True,
                error.TryGetProperty("requestId", out var id) ? id.GetString() : null), response.StatusCode)
            : new ApiResult(false, null, new ApiError("unknown", text, false, null), response.StatusCode);
    }

    public static string UserCodeDigest(string userCode)
    {
        var normalized = new string(userCode.Where(character => character != '-' && !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    // ---------------- 令牌 ----------------

    public Task<ApiResult> WhoAmIAsync(DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/automation/whoami", device, token, null, null, cancellationToken);

    public Task<ApiResult> GetVersionAsync(DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/automation/version", device, token, null, null, cancellationToken);

    public Task<ApiResult> GetOverviewAsync(DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/automation/overview", device, token, null, null, cancellationToken);

    public Task<ApiResult> ListClientsAsync(DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/automation/clients", device, token, null, null, cancellationToken);

    public Task<ApiResult> ListChannelsAsync(string clientId, DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/v1/automation/clients/{Uri.EscapeDataString(clientId)}/channels", device, token, null, null, cancellationToken);

    public Task<ApiResult> ListMappingsAsync(string clientId, DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/v1/automation/clients/{Uri.EscapeDataString(clientId)}/mappings", device, token, null, null, cancellationToken);

    public Task<ApiResult> PatchChannelAsync(string clientId, string channelId, DeviceKey device, string token,
        object patch, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"/api/v1/automation/clients/{Uri.EscapeDataString(clientId)}/channels/{Uri.EscapeDataString(channelId)}",
            device, token, patch, idempotencyKey, cancellationToken);

    public Task<ApiResult> SubmitChangeAsync(DeviceKey device, string token, object body, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "/api/v1/automation/change-requests", device, token, body, idempotencyKey, cancellationToken);

    public Task<ApiResult> ListChangesAsync(DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/automation/change-requests", device, token, null, null, cancellationToken);

    public Task<ApiResult> GetChangeAsync(Guid changeRequestId, DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"/api/v1/automation/change-requests/{changeRequestId}", device, token, null, null, cancellationToken);

    public Task<ApiResult> CancelChangeAsync(Guid changeRequestId, DeviceKey device, string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"/api/v1/automation/change-requests/{changeRequestId}", device, token, null, null, cancellationToken);

    public void Dispose() => http.Dispose();

    private static string Base64UrlEncode(byte[] bytes) => Base64Url.EncodeToString(bytes);
}
