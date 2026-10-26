using System.Text.Json.Serialization;
using AdrMcp.Application.Abstractions;

namespace AdrMcp.Infrastructure.Auth;

/// <summary>The HTTP answer for a refused bearer token: status, optional challenge header, optional Retry-After.</summary>
public sealed record McpChallengeResponse(int StatusCode, string? WwwAuthenticate, int? RetryAfterSeconds, string Error);

/// <summary>
/// Builds the HTTP challenge the MCP authorization spec asks for (revision 2026-07-28; RFC 6750 section 3 and RFC 9728 section 5.1):
///  - no credentials: 401, <c>Bearer resource_metadata="..." scope="..."</c> and no <c>error</c>;
///  - a token that is not valid: 401, <c>error="invalid_token"</c> with a generic description (the reason goes to the log, not to the caller);
///  - not enough scope or role: 403, <c>error="insufficient_scope"</c>, <c>scope</c> and <c>resource_metadata</c>;
///  - keys cannot be fetched: 503 with <c>Retry-After</c> and no <c>WWW-Authenticate</c>, because the credentials may be fine.
/// The MCP webhook itself is challenged by App Service Authentication; this is used where the app owns the HTTP response.
/// </summary>
public static class McpChallenge
{
    public const int RetryAfterSecondsOnUnavailable = 30;

    public static McpChallengeResponse For(BearerFailure failure, string resourceMetadataUrl, string scope)
    {
        Require(resourceMetadataUrl); Require(scope);
        switch (failure)
        {
            case BearerFailure.None:
                return new McpChallengeResponse(200, null, null, "");
            case BearerFailure.MissingToken:
                return new McpChallengeResponse(401, $"Bearer resource_metadata=\"{resourceMetadataUrl}\", scope=\"{scope}\"", null, "unauthorized");
            case BearerFailure.InsufficientScope:
            case BearerFailure.ApplicationTokenNotAllowed:
                return new McpChallengeResponse(403,
                    $"Bearer error=\"insufficient_scope\", error_description=\"The access token does not carry the required scope.\", scope=\"{scope}\", resource_metadata=\"{resourceMetadataUrl}\"",
                    null, "insufficient_scope");
            case BearerFailure.KeysUnavailable:
                return new McpChallengeResponse(503, null, RetryAfterSecondsOnUnavailable, "temporarily_unavailable");
            default:
                return new McpChallengeResponse(401,
                    $"Bearer error=\"invalid_token\", error_description=\"The access token is not valid for this server.\", resource_metadata=\"{resourceMetadataUrl}\", scope=\"{scope}\"",
                    null, "invalid_token");
        }
    }

    /// <summary>403 for a caller whose token is fine but who holds no ADR role (an app role is an assignment, not a scope).</summary>
    public static McpChallengeResponse NoRole(string resourceMetadataUrl, string scope)
    {
        Require(resourceMetadataUrl); Require(scope);
        return new McpChallengeResponse(403,
            $"Bearer error=\"insufficient_scope\", error_description=\"An ADR app role is required.\", scope=\"{scope}\", resource_metadata=\"{resourceMetadataUrl}\"",
            null, "insufficient_scope");
    }

    // The values are ours, but a quote or control character in one would break the header, so refuse rather than escape.
    private static void Require(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => c is '"' or '\\' || char.IsControl(c)))
            throw new ArgumentException("Challenge values must be non-empty and free of quotes, backslashes and control characters.");
    }
}

/// <summary>OAuth 2.0 Protected Resource Metadata (RFC 9728) for the MCP endpoint, as MCP clients read it to find Entra.</summary>
public static class ProtectedResourceMetadata
{
    public const string WellKnownPrefix = "/.well-known/oauth-protected-resource";

    /// <summary>The path-inserted metadata URL for a resource identifier (RFC 9728 section 3.1): the well-known segment goes between host and path.</summary>
    public static string MetadataUrl(string resourceUri)
    {
        var uri = new Uri(resourceUri);
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.GetLeftPart(UriPartial.Authority)}{WellKnownPrefix}{path}";
    }

    /// <summary>The path after the well-known segment that names this resource ("" for a resource at the root), without a leading slash.</summary>
    public static string ResourcePath(string resourceUri) => new Uri(resourceUri).AbsolutePath.Trim('/');

    public static ProtectedResourceMetadataDocument Build(EntraTokenPolicy policy) => new()
    {
        Resource = policy.ResourceUri,
        AuthorizationServers = new[] { policy.Issuer },
        ScopesSupported = new[] { policy.ClientScope },
        BearerMethodsSupported = new[] { "header" },
        ResourceName = "ADR Intelligence MCP server",
    };
}

public sealed class ProtectedResourceMetadataDocument
{
    [JsonPropertyName("resource")] public string Resource { get; init; } = "";
    [JsonPropertyName("authorization_servers")] public string[] AuthorizationServers { get; init; } = Array.Empty<string>();
    [JsonPropertyName("scopes_supported")] public string[] ScopesSupported { get; init; } = Array.Empty<string>();
    [JsonPropertyName("bearer_methods_supported")] public string[] BearerMethodsSupported { get; init; } = Array.Empty<string>();
    [JsonPropertyName("resource_name")] public string ResourceName { get; init; } = "";
}
