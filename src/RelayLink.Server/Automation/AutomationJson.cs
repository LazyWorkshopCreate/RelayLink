using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayLink.Server.Automation;

/// <summary>
/// 稳定错误码。CLI 与 SDK 依赖这些字符串，不得随实现细节变动。
/// </summary>
public static class AutomationErrors
{
    public const string Unauthenticated = "unauthenticated";
    public const string ProofInvalid = "proof_invalid";
    public const string ProofReplayed = "proof_replayed";
    public const string TokenExpired = "token_expired";
    public const string TokenRevoked = "token_revoked";
    public const string ScopeInsufficient = "scope_insufficient";
    public const string PairingNotFound = "pairing_not_found";
    public const string PairingDenied = "pairing_denied";
    public const string PairingExpired = "pairing_expired";
    public const string PairingLocked = "pairing_locked";
    public const string PairingAlreadyCompleted = "pairing_already_completed";
    public const string PendingCapacityExceeded = "pending_capacity_exceeded";
    public const string RateLimited = "rate_limited";
    public const string ValidationFailed = "validation_failed";
    public const string UnknownField = "unknown_field";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string RequiresChangeRequest = "requires_change_request";
    public const string ChangeRequestNotApproved = "change_request_not_approved";
    public const string AuditUnavailable = "audit_unavailable";
    public const string StorageUnavailable = "storage_unavailable";
    public const string UnsupportedApiVersion = "unsupported_api_version";
    public const string AutomationDisabled = "automation_disabled";
}

/// <summary>
/// 统一响应契约。成功响应恒含 requestId；写操作另含 auditStatus 与 operationId。
/// 失败响应统一为 {"error":{"code","message","retryable","requestId","details"}}。
/// </summary>
public static class AutomationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public const string ApiVersionHeader = "X-RelayLink-Api-Version";
    public const string OperationIdHeader = "X-RelayLink-Operation-Id";
    public const string AuditStatusHeader = "X-RelayLink-Audit-Status";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const int SupportedApiVersion = 1;

    public static IResult Ok(object data, string? requestId = null, string? auditStatus = null, string? operationId = null) =>
        Results.Json(new
        {
            requestId = requestId ?? Guid.NewGuid().ToString(),
            auditStatus,
            operationId,
            data
        }, Options);

    public static IResult Error(string code, string message, int statusCode, bool retryable = false,
        string? requestId = null, object? details = null) =>
        Results.Json(new
        {
            error = new
            {
                code,
                message,
                retryable,
                requestId = requestId ?? Guid.NewGuid().ToString(),
                details
            }
        }, Options, statusCode: statusCode);

    public static int StatusFor(string code) => code switch
    {
        AutomationErrors.Unauthenticated => StatusCodes.Status401Unauthorized,
        AutomationErrors.ProofInvalid => StatusCodes.Status401Unauthorized,
        AutomationErrors.ProofReplayed => StatusCodes.Status401Unauthorized,
        AutomationErrors.TokenExpired => StatusCodes.Status401Unauthorized,
        AutomationErrors.TokenRevoked => StatusCodes.Status401Unauthorized,
        AutomationErrors.ScopeInsufficient => StatusCodes.Status403Forbidden,
        AutomationErrors.PairingNotFound => StatusCodes.Status404NotFound,
        AutomationErrors.NotFound => StatusCodes.Status404NotFound,
        AutomationErrors.PairingDenied => StatusCodes.Status403Forbidden,
        AutomationErrors.PairingExpired => StatusCodes.Status410Gone,
        AutomationErrors.PairingLocked => StatusCodes.Status403Forbidden,
        AutomationErrors.PairingAlreadyCompleted => StatusCodes.Status409Conflict,
        AutomationErrors.Conflict => StatusCodes.Status409Conflict,
        AutomationErrors.RequiresChangeRequest => StatusCodes.Status409Conflict,
        AutomationErrors.ChangeRequestNotApproved => StatusCodes.Status409Conflict,
        AutomationErrors.PendingCapacityExceeded => StatusCodes.Status429TooManyRequests,
        AutomationErrors.RateLimited => StatusCodes.Status429TooManyRequests,
        AutomationErrors.ValidationFailed => StatusCodes.Status400BadRequest,
        AutomationErrors.UnknownField => StatusCodes.Status400BadRequest,
        AutomationErrors.AuditUnavailable => StatusCodes.Status503ServiceUnavailable,
        AutomationErrors.StorageUnavailable => StatusCodes.Status503ServiceUnavailable,
        AutomationErrors.UnsupportedApiVersion => StatusCodes.Status400BadRequest,
        AutomationErrors.AutomationDisabled => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest
    };

    public static bool IsRetryable(string code) => code is
        AutomationErrors.RateLimited or AutomationErrors.PendingCapacityExceeded or
        AutomationErrors.AuditUnavailable or AutomationErrors.StorageUnavailable;
}
