using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AdrMcp.Infrastructure.Auth;

/// <summary>Stands in while <c>Auth:Entra:ValidateBearerToken</c> is off, so callers need no null checks.</summary>
public sealed class DisabledBearerTokenValidator : IBearerTokenValidator
{
    public bool Enabled => false;
    public Task<BearerResult> ValidateAsync(string? authorizationHeader, CancellationToken ct) => Task.FromResult(BearerResult.Fail(BearerFailure.MissingToken));
}

/// <summary>
/// Checks one Entra v2 access token the way the MCP authorization spec and Microsoft's guidance require (ADR-0008).
/// Signature and standard claims are verified by <see cref="JsonWebTokenHandler"/>; this class supplies the policy around it:
/// RS256 only, a key chosen by <c>kid</c> from the tenant's cached key set, exact issuer, the server app as audience, the tenant,
/// token version 2.0, an allowed client application, both <c>exp</c> and <c>nbf</c>, and a required scope. Anything that cannot be
/// decided because keys are missing is <see cref="BearerFailure.KeysUnavailable"/>, never acceptance.
/// Nothing here logs a token, a claim value or a library exception message.
/// </summary>
public sealed class BearerTokenValidator : IBearerTokenValidator
{
    public const int MaxTokenLength = 16 * 1024;
    private const string RequiredAlgorithm = SecurityAlgorithms.RsaSha256;

    private readonly EntraTokenPolicy _policy;
    private readonly ISigningKeyProvider _keys;
    private readonly IClock _clock;
    private readonly ILogger<BearerTokenValidator> _logger;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public BearerTokenValidator(EntraTokenPolicy policy, ISigningKeyProvider keys, IClock clock, ILogger<BearerTokenValidator> logger)
    {
        _policy = policy; _keys = keys; _clock = clock; _logger = logger;
    }

    public bool Enabled => true;

    public async Task<BearerResult> ValidateAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (!TryExtractToken(authorizationHeader, out var token, out var failure)) return BearerResult.Fail(failure);

        JsonWebToken parsed;
        try { parsed = new JsonWebToken(token); }
        catch (Exception) { return BearerResult.Fail(BearerFailure.Malformed); }

        // Refuse before any key is touched: "none", HMAC (a public key used as the secret) and every algorithm we do not issue.
        if (!string.Equals(parsed.Alg, RequiredAlgorithm, StringComparison.Ordinal)) return BearerResult.Fail(BearerFailure.UnsupportedAlgorithm);
        if (string.IsNullOrEmpty(parsed.Kid)) return BearerResult.Fail(BearerFailure.UnknownKey);

        var lookup = await _keys.GetKeyAsync(parsed.Kid, ct).ConfigureAwait(false);
        if (lookup.Status == KeyLookupStatus.Unavailable) return BearerResult.Fail(BearerFailure.KeysUnavailable);
        if (lookup.Status != KeyLookupStatus.Found || lookup.Key is null) return BearerResult.Fail(BearerFailure.UnknownKey);

        var parameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = lookup.Key,
            ValidAlgorithms = new[] { RequiredAlgorithm },
            ValidateIssuer = true,
            ValidIssuer = _policy.Issuer,
            ValidateAudience = true,
            ValidAudiences = _policy.Audiences,
            IgnoreTrailingSlashWhenValidatingAudience = false,   // exact match, also for the extra audiences
            // Lifetime is checked below against the injected clock, so both bounds are required and tests can fix the time.
            ValidateLifetime = false,
            RequireExpirationTime = false,
        };

        TokenValidationResult result;
        try { result = await _handler.ValidateTokenAsync(parsed, parameters).ConfigureAwait(false); }
        catch (Exception) { return BearerResult.Fail(BearerFailure.Malformed); }
        if (!result.IsValid) return BearerResult.Fail(Classify(result.Exception));

        // The token is signed by Entra, but a claim of an odd shape must still be a refusal and never an exception.
        try { return CheckClaims(parsed); }
        catch (Exception) { return BearerResult.Fail(BearerFailure.Malformed); }
    }

    private BearerResult CheckClaims(JsonWebToken token)
    {
        var now = _clock.UtcNow;
        if (token.ValidTo == DateTime.MinValue || token.ValidFrom == DateTime.MinValue) return BearerResult.Fail(BearerFailure.MissingClaim);   // exp and nbf are both required
        var expires = new DateTimeOffset(DateTime.SpecifyKind(token.ValidTo, DateTimeKind.Utc));
        var notBefore = new DateTimeOffset(DateTime.SpecifyKind(token.ValidFrom, DateTimeKind.Utc));
        // Arranged so that no date arithmetic can overflow for an exp or nbf at the ends of the calendar.
        if (now - _policy.ClockSkew >= expires) return BearerResult.Fail(BearerFailure.Expired);
        if (notBefore > now + _policy.ClockSkew) return BearerResult.Fail(BearerFailure.NotYetValid);

        if (!TryString(token, "ver", out var version) || version != "2.0") return BearerResult.Fail(BearerFailure.UnsupportedVersion);
        if (!TryString(token, "tid", out var tid)) return BearerResult.Fail(BearerFailure.MissingClaim);
        if (!Guid.TryParse(tid, out _) || !string.Equals(EntraTokenPolicy.Canonical(tid), _policy.TenantId, StringComparison.Ordinal)) return BearerResult.Fail(BearerFailure.WrongTenant);
        if (!TryString(token, "azp", out var azp)) return BearerResult.Fail(BearerFailure.MissingClaim);
        if (!_policy.AllowedClientIds.Contains(EntraTokenPolicy.Canonical(azp))) return BearerResult.Fail(BearerFailure.WrongClient);
        if (!TryString(token, "oid", out var oid) || !Guid.TryParse(oid, out _)) return BearerResult.Fail(BearerFailure.MissingClaim);

        var scopes = TryString(token, "scp", out var scp)
            ? scp.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        if (scopes.Length > 0)
        {
            if (!scopes.Any(_policy.RequiredScopes.Contains)) return BearerResult.Fail(BearerFailure.InsufficientScope);
        }
        else if (!_policy.AllowApplicationTokens)
        {
            return BearerResult.Fail(BearerFailure.ApplicationTokenNotAllowed);
        }

        var roles = token.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        TryString(token, "preferred_username", out var display);
        if (string.IsNullOrEmpty(display)) TryString(token, "name", out display);
        return BearerResult.Ok(new BearerPrincipal(EntraTokenPolicy.Canonical(tid), EntraTokenPolicy.Canonical(oid), EntraTokenPolicy.Canonical(azp), string.IsNullOrEmpty(display) ? null : display, roles, scopes, expires));
    }

    private static bool TryString(JsonWebToken token, string claim, out string value)
    {
        value = "";
        try { return token.TryGetPayloadValue<string>(claim, out value!) && !string.IsNullOrEmpty(value); }
        catch (Exception) { return false; }   // a claim of the wrong JSON type counts as missing
    }

    /// <summary>Maps the library's exception type to our reason. Messages are never read: they can echo claim values.</summary>
    private static BearerFailure Classify(Exception? exception) => exception switch
    {
        SecurityTokenInvalidAudienceException => BearerFailure.WrongAudience,
        SecurityTokenInvalidIssuerException => BearerFailure.WrongIssuer,
        SecurityTokenInvalidAlgorithmException => BearerFailure.UnsupportedAlgorithm,
        SecurityTokenSignatureKeyNotFoundException => BearerFailure.UnknownKey,
        SecurityTokenInvalidSignatureException => BearerFailure.InvalidSignature,
        SecurityTokenMalformedException or ArgumentException => BearerFailure.Malformed,
        _ => BearerFailure.InvalidSignature,
    };

    /// <summary>Extracts the credential from an <c>Authorization</c> header value: one Bearer token, scheme case-insensitive, JWT characters only.</summary>
    public static bool TryExtractToken(string? header, out string token, out BearerFailure failure)
    {
        token = "";
        failure = BearerFailure.MissingToken;
        if (string.IsNullOrWhiteSpace(header)) return false;
        var value = header.Trim();
        const string scheme = "Bearer";
        if (value.Length <= scheme.Length + 1 || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(value[scheme.Length])) return false;
        var candidate = value[(scheme.Length + 1)..].Trim();
        if (candidate.Length == 0) return false;
        if (candidate.Length > MaxTokenLength) { failure = BearerFailure.TokenTooLarge; return false; }
        foreach (var c in candidate)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '+' or '/' or '=')) { failure = BearerFailure.Malformed; return false; }
        token = candidate;
        return true;
    }
}
