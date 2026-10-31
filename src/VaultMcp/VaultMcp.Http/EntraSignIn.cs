using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace VaultMcp.Http;

/// <summary>Why a call to the MCP endpoint was refused. Logged as a fixed word; never a token, a claim value or a library message.</summary>
public enum TokenRefusal
{
    /// <summary>No <c>Authorization: Bearer</c> credential at all.</summary>
    MissingToken,
    /// <summary>Signature, issuer, audience, lifetime, algorithm, key, token version, tenant, client or shape: anything a new sign-in may fix.</summary>
    InvalidToken,
    /// <summary>A delegated token without the vault scope, or an application token (no <c>scp</c>), which this server never accepts.</summary>
    InsufficientScope,
    /// <summary>A valid token whose user holds no vault app role. An assignment in Entra fixes it; signing in again does not.</summary>
    NoRole,
    /// <summary>The tenant's signing keys could not be fetched, so the token could not be decided. Never acceptance.</summary>
    KeysUnavailable,
}

/// <summary>Carries a <see cref="TokenRefusal"/> from the token checks to the challenge, through the authentication result.</summary>
public sealed class TokenRefusedException : Exception
{
    public TokenRefusedException(TokenRefusal reason) : base(reason.ToString()) => Reason = reason;
    public TokenRefusal Reason { get; }
}

/// <summary>
/// Entra sign-in for the vault server (ADR-0010 phase 2 step 3): the ADR-0008 pattern applied to a second server with its own app
/// registration. ASP.NET Core's bearer handler verifies the signature (RS256 only, a key by <c>kid</c> from the tenant's cached key set),
/// the exact v2.0 issuer, the audience (the vault server's client ID) and the lifetime; the checks here add the token version, the tenant,
/// an allowed client application (<c>azp</c>), the <c>oid</c>, the delegated scope, and the authorization policy adds the app role.
/// Refusals follow the MCP authorization spec (revision 2026-07-28; RFC 6750 section 3 and RFC 9728 section 5.1), the same table as
/// the ADR server's <c>McpChallenge</c>: 401 with the resource metadata for a missing token, 401 <c>invalid_token</c>, 403
/// <c>insufficient_scope</c>, 403 without a challenge for a missing role, and 503 with <c>Retry-After</c> when the keys cannot be fetched.
/// </summary>
public static class EntraSignIn
{
    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;
    public const string Policy = "VaultReader";
    public const string WellKnownPrefix = "/.well-known/oauth-protected-resource";
    public const int RetryAfterSecondsOnUnavailable = 30;
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    private const string RefusalItem = "VaultMcp.TokenRefusal";

    public static IServiceCollection AddVaultEntraSignIn(this IServiceCollection services, VaultEntraOptions entra)
    {
        services.AddAuthentication(Scheme).AddJwtBearer(Scheme, options =>
        {
            options.Authority = entra.Issuer;              // the OpenID Connect metadata and signing keys of this tenant, cached by the handler
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;              // keep the raw claim names: tid, oid, azp, scp, roles, ver
            options.IncludeErrorDetails = false;           // the reason goes to the log, not to the caller
            options.SaveToken = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
                ValidateIssuer = true,
                ValidIssuer = entra.Issuer,
                ValidateAudience = true,
                ValidAudience = entra.ClientId,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = ClockSkew,
                NameClaimType = "preferred_username",
                RoleClaimType = "roles",
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (CheckClaims(context.Principal, entra) is { } refusal) context.Fail(new TokenRefusedException(refusal));
                    return Task.CompletedTask;
                },
                // A token the library refused keeps its own exception and becomes invalid_token below. Anything else that stopped the
                // check (the key set could not be fetched, the request was aborted) is "could not decide", answered 503, never 500 or accept.
                OnAuthenticationFailed = async context =>
                {
                    if (!IsTokenProblem(context.Exception)) { context.Fail(new TokenRefusedException(TokenRefusal.KeysUnavailable)); return; }
                    // With Entra's real configuration manager the library swallows a failed metadata fetch and reports "no signing key" (seen on
                    // the running host). Ask the manager whether it holds a configuration at all: if it cannot produce one, the token could not
                    // be decided, which is 503 with Retry-After, not a 401 that sends the client back to sign in during an outage.
                    if (NamesNoKey(context.Exception) && context.Options.ConfigurationManager is { } manager)
                    {
                        try { await manager.GetConfigurationAsync(context.HttpContext.RequestAborted).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { context.Fail(new TokenRefusedException(TokenRefusal.KeysUnavailable)); }
                    }
                },
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    var refusal = context.AuthenticateFailure switch
                    {
                        null => TokenRefusal.MissingToken,
                        TokenRefusedException refused => refused.Reason,
                        _ => TokenRefusal.InvalidToken,
                    };
                    return WriteRefusalAsync(context.HttpContext, refusal, entra);
                },
                OnForbidden = context => WriteRefusalAsync(context.HttpContext, TokenRefusal.NoRole, entra),
            };
        });

        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.HasClaim(c => c.Type == "roles" && string.Equals(c.Value, entra.RequiredRole, StringComparison.Ordinal))));

        return services;
    }

    /// <summary>The claim rules on top of the library's checks. Null means accepted. Mirrors the ADR server's <c>BearerTokenValidator</c>.</summary>
    public static TokenRefusal? CheckClaims(ClaimsPrincipal? principal, VaultEntraOptions entra)
    {
        if (principal is null) return TokenRefusal.InvalidToken;
        if (Value(principal, "ver") != "2.0") return TokenRefusal.InvalidToken;
        var tid = Value(principal, "tid");
        if (tid is null || !Guid.TryParse(tid, out _) || VaultEntraOptions.Canonical(tid) != entra.TenantId) return TokenRefusal.InvalidToken;
        var azp = Value(principal, "azp");
        if (azp is null || !entra.AllowedClientIds.Contains(VaultEntraOptions.Canonical(azp))) return TokenRefusal.InvalidToken;
        var oid = Value(principal, "oid");
        if (oid is null || !Guid.TryParse(oid, out _)) return TokenRefusal.InvalidToken;

        // Delegated tokens only: an application token (no scp) cannot stand for a person, and the vault is read as a person.
        var scopes = (Value(principal, "scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(entra.Scope, StringComparer.Ordinal)) return TokenRefusal.InsufficientScope;
        return null;
    }

    private static string? Value(ClaimsPrincipal principal, string type)
    {
        var value = principal.FindFirst(c => c.Type == type)?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>"No signing key matched" from the library, which it also reports when the key set could not be fetched at all.</summary>
    private static bool NamesNoKey(Exception exception) => exception switch
    {
        SecurityTokenSignatureKeyNotFoundException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(NamesNoKey),
        _ => false,
    };

    /// <summary>
    /// The library's own refusals (signature, issuer, audience, lifetime, algorithm, key) and our claim refusals, plus the argument and
    /// format errors it raises for a credential that is not a token at all; anything else could not be decided.
    /// </summary>
    private static bool IsTokenProblem(Exception exception) => exception switch
    {
        TokenRefusedException => true,
        SecurityTokenException => true,
        ArgumentException or FormatException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTokenProblem),
        _ => false,
    };

    /// <summary>Writes the spec's answer for a refusal. The log gets the fixed reason word; the body a short plain line, never a claim or a token.</summary>
    public static async Task WriteRefusalAsync(HttpContext http, TokenRefusal refusal, VaultEntraOptions entra)
    {
        var response = http.Response;
        var metadata = ResourceMetadataUrl(entra);
        var scope = entra.ClientScope;
        string body;
        switch (refusal)
        {
            case TokenRefusal.MissingToken:
                response.StatusCode = StatusCodes.Status401Unauthorized;
                response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{metadata}\", scope=\"{scope}\"";
                body = "unauthorized";
                break;
            case TokenRefusal.InsufficientScope:
                response.StatusCode = StatusCodes.Status403Forbidden;
                response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", error_description=\"The access token does not carry the required scope.\", scope=\"{scope}\", resource_metadata=\"{metadata}\"";
                body = "insufficient_scope";
                break;
            case TokenRefusal.NoRole:
                // An app role is an assignment in Entra, not a scope: no challenge, or the client would loop through consent for nothing.
                response.StatusCode = StatusCodes.Status403Forbidden;
                body = "forbidden";
                break;
            case TokenRefusal.KeysUnavailable:
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                response.Headers.RetryAfter = RetryAfterSecondsOnUnavailable.ToString(System.Globalization.CultureInfo.InvariantCulture);
                body = "temporarily_unavailable";
                break;
            default:
                response.StatusCode = StatusCodes.Status401Unauthorized;
                response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"The access token is not valid for this server.\", resource_metadata=\"{metadata}\", scope=\"{scope}\"";
                body = "invalid_token";
                break;
        }
        http.Items[RefusalItem] = refusal;
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("VaultMcp.Http.EntraSignIn")
            .LogInformation("MCP call refused: {Reason} ({Status})", refusal, response.StatusCode);
        response.ContentType = "text/plain; charset=utf-8";
        await response.WriteAsync(body);
    }

    /// <summary>The path-inserted metadata URL for the resource (RFC 9728 section 3.1): the well-known segment goes between host and path.</summary>
    public static string ResourceMetadataUrl(VaultEntraOptions entra)
    {
        var uri = new Uri(entra.ResourceUri);
        return $"{uri.GetLeftPart(UriPartial.Authority)}{WellKnownPrefix}{uri.AbsolutePath.TrimEnd('/')}";
    }

    /// <summary>The path after the well-known segment that names this resource, without a leading slash ("mcp").</summary>
    public static string ResourcePath(VaultEntraOptions entra) => new Uri(entra.ResourceUri).AbsolutePath.Trim('/');

    public static ProtectedResourceMetadataDocument Metadata(VaultEntraOptions entra) => new()
    {
        Resource = entra.ResourceUri,
        AuthorizationServers = new[] { entra.Issuer },
        ScopesSupported = new[] { entra.ClientScope },
        BearerMethodsSupported = new[] { "header" },
        ResourceName = "Obsidian vault MCP server",
    };

    /// <summary>
    /// Serves the protected resource metadata (RFC 9728) at <c>/.well-known/oauth-protected-resource</c> and at the path-inserted form
    /// for the MCP endpoint, so clients can find Entra and the scope. Anonymous and cacheable: it holds nothing but what the challenge already says.
    /// </summary>
    public static void MapProtectedResourceMetadata(this WebApplication app, VaultEntraOptions entra)
    {
        var document = Metadata(entra);
        var resourcePath = ResourcePath(entra);
        app.MapGet(WellKnownPrefix + "/{*path}", (string? path, HttpContext http) =>
        {
            var requested = (path ?? "").Trim('/');
            if (requested.Length > 0 && !string.Equals(requested, resourcePath, StringComparison.Ordinal)) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=300";
            http.Response.Headers.AccessControlAllowOrigin = "*";
            return Results.Json(document);
        }).AllowAnonymous();
        app.MapGet(WellKnownPrefix, (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            http.Response.Headers.AccessControlAllowOrigin = "*";
            return Results.Json(document);
        }).AllowAnonymous();
    }
}

/// <summary>OAuth 2.0 Protected Resource Metadata (RFC 9728), the fields MCP clients read.</summary>
public sealed class ProtectedResourceMetadataDocument
{
    [JsonPropertyName("resource")] public string Resource { get; init; } = "";
    [JsonPropertyName("authorization_servers")] public string[] AuthorizationServers { get; init; } = Array.Empty<string>();
    [JsonPropertyName("scopes_supported")] public string[] ScopesSupported { get; init; } = Array.Empty<string>();
    [JsonPropertyName("bearer_methods_supported")] public string[] BearerMethodsSupported { get; init; } = Array.Empty<string>();
    [JsonPropertyName("resource_name")] public string ResourceName { get; init; } = "";
}
