namespace AdrMcp.Application.Abstractions;

/// <summary>Why a bearer token was refused. The code is logged; the caller only ever sees a generic message (ADR-0008).</summary>
public enum BearerFailure
{
    None = 0,
    /// <summary>No <c>Authorization</c> header, or it is not a Bearer credential.</summary>
    MissingToken,
    Malformed,
    TokenTooLarge,
    /// <summary>Header <c>alg</c> is not RS256 (this includes <c>none</c> and every HMAC algorithm).</summary>
    UnsupportedAlgorithm,
    /// <summary>No <c>kid</c>, or no signing key with that ID after the permitted refresh.</summary>
    UnknownKey,
    InvalidSignature,
    Expired,
    NotYetValid,
    WrongIssuer,
    WrongAudience,
    WrongTenant,
    UnsupportedVersion,
    WrongClient,
    MissingClaim,
    /// <summary>A delegated token without the required scope (403).</summary>
    InsufficientScope,
    /// <summary>A token without <c>scp</c> while application tokens are not allowed (403).</summary>
    ApplicationTokenNotAllowed,
    /// <summary>Signing keys are needed and could not be fetched or parsed (503, never accept).</summary>
    KeysUnavailable,
}

/// <summary>The verified caller. Built only from a token whose signature and claims passed; never from a header the caller can set.</summary>
public sealed record BearerPrincipal(
    string TenantId,
    string ObjectId,
    string ClientAppId,
    string? Display,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt);

public sealed record BearerResult(BearerPrincipal? Principal, BearerFailure Failure)
{
    public bool Succeeded => Failure == BearerFailure.None && Principal is not null;

    public static BearerResult Ok(BearerPrincipal principal) => new(principal, BearerFailure.None);
    public static BearerResult Fail(BearerFailure failure) => new(null, failure);

    /// <summary>HTTP status the failure maps to under the MCP authorization spec: 401 invalid or missing, 403 insufficient scope, 503 cannot decide.</summary>
    public int HttpStatus => Failure switch
    {
        BearerFailure.None => 200,
        BearerFailure.InsufficientScope or BearerFailure.ApplicationTokenNotAllowed => 403,
        BearerFailure.KeysUnavailable => 503,
        _ => 401,
    };

    /// <summary>Fixed, content-free reason for logs.</summary>
    public string ReasonCode => Failure switch
    {
        BearerFailure.None => "ok",
        BearerFailure.MissingToken => "missing_token",
        BearerFailure.Malformed => "malformed",
        BearerFailure.TokenTooLarge => "token_too_large",
        BearerFailure.UnsupportedAlgorithm => "unsupported_algorithm",
        BearerFailure.UnknownKey => "unknown_key",
        BearerFailure.InvalidSignature => "invalid_signature",
        BearerFailure.Expired => "expired",
        BearerFailure.NotYetValid => "not_yet_valid",
        BearerFailure.WrongIssuer => "wrong_issuer",
        BearerFailure.WrongAudience => "wrong_audience",
        BearerFailure.WrongTenant => "wrong_tenant",
        BearerFailure.UnsupportedVersion => "unsupported_token_version",
        BearerFailure.WrongClient => "wrong_client",
        BearerFailure.MissingClaim => "missing_claim",
        BearerFailure.InsufficientScope => "insufficient_scope",
        BearerFailure.ApplicationTokenNotAllowed => "application_token_not_allowed",
        BearerFailure.KeysUnavailable => "keys_unavailable",
        _ => "refused",
    };
}

/// <summary>Validates the <c>Authorization</c> header value of one request. Never throws for a bad token.</summary>
public interface IBearerTokenValidator
{
    /// <summary>False when <c>Auth:Entra:ValidateBearerToken</c> is off; callers then keep using the platform principal header.</summary>
    bool Enabled { get; }

    Task<BearerResult> ValidateAsync(string? authorizationHeader, CancellationToken ct);
}
