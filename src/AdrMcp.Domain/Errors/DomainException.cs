namespace AdrMcp.Domain.Errors;

/// <summary>Stable, machine-readable error codes returned to MCP clients.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string AdrNotFound = "ADR_NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string EtagMismatch = "ETAG_MISMATCH";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string InvalidTransition = "INVALID_STATUS_TRANSITION";
    public const string HumanOwnedFieldProtected = "HUMAN_OWNED_FIELD_PROTECTED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string TenantMismatch = "TENANT_MISMATCH";
    public const string PolicyBlocked = "POLICY_BLOCKED";
    public const string UploadRejected = "UPLOAD_REJECTED";
    public const string UploadExpired = "UPLOAD_EXPIRED";
    public const string UploadNotFound = "UPLOAD_NOT_FOUND";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string UnsupportedFileType = "UNSUPPORTED_FILE_TYPE";
    public const string ChecksumMismatch = "CHECKSUM_MISMATCH";
    public const string ContentSafetyBlocked = "CONTENT_SAFETY_BLOCKED";
    public const string RateLimited = "RATE_LIMITED";
    public const string JobNotFound = "JOB_NOT_FOUND";
    public const string JobNotCancellable = "JOB_NOT_CANCELLABLE";
    public const string Internal = "INTERNAL_ERROR";
    public const string NotImplemented = "NOT_IMPLEMENTED";
}

public class DomainException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }
    public IReadOnlyDictionary<string, string>? Details { get; }

    public DomainException(string code, string message, bool retryable = false, IReadOnlyDictionary<string, string>? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
        Details = details;
    }
}

public sealed class ValidationException : DomainException
{
    public IReadOnlyList<string> Errors { get; }
    public ValidationException(IReadOnlyList<string> errors)
        : base(ErrorCodes.ValidationFailed, "Request validation failed: " + string.Join("; ", errors)) => Errors = errors;
    public ValidationException(string error) : this(new[] { error }) { }
}

public sealed class NotFoundException : DomainException
{
    public NotFoundException(string code, string message) : base(code, message) { }
}

public sealed class ConcurrencyException : DomainException
{
    public ConcurrencyException(string message) : base(ErrorCodes.EtagMismatch, message, retryable: true) { }
}

public sealed class ForbiddenException : DomainException
{
    public ForbiddenException(string message, string code = ErrorCodes.Forbidden) : base(code, message) { }
}

public sealed class InvalidTransitionException : DomainException
{
    public InvalidTransitionException(string from, string to)
        : base(ErrorCodes.InvalidTransition, $"ADR status transition {from} -> {to} is not allowed.") { }
}

public sealed class HumanOwnedFieldException : DomainException
{
    public HumanOwnedFieldException(string field)
        : base(ErrorCodes.HumanOwnedFieldProtected, $"Field '{field}' is human-owned and cannot be modified automatically. Use adr_propose_change.") { }
}

public sealed class PolicyBlockedException : DomainException
{
    public PolicyBlockedException(string message) : base(ErrorCodes.PolicyBlocked, message) { }
}
