using System.Globalization;
using System.Text.Json;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using AdrMcp.Infrastructure.Auth;
using AdrMcp.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Functions.Http;

/// <summary>
/// <c>GET /api/auth/check</c>: runs the same token validation and role mapping as a tool call and answers with the HTTP status and
/// <c>WWW-Authenticate</c> challenge the MCP authorization spec asks for (401 invalid or missing, 403 insufficient scope or role,
/// 503 with Retry-After when signing keys cannot be fetched). It exists so an operator can prove the sign-in policy with curl before any
/// MCP client is involved (docs/11-entra-sign-in.md). It returns only what the caller's own token already says: the mapped ADR roles,
/// the scopes and the seconds left. Answers 404 while <c>Auth:Entra:ValidateBearerToken</c> is off. Logs a fixed reason code only.
/// </summary>
public sealed class AuthCheckFunction
{
    private readonly IBearerTokenValidator _validator;
    private readonly AuthOptions _auth;
    private readonly IServiceProvider _services;
    private readonly IClock _clock;
    private readonly ILogger<AuthCheckFunction> _logger;

    public AuthCheckFunction(IBearerTokenValidator validator, IOptions<AuthOptions> auth, IServiceProvider services, IClock clock, ILogger<AuthCheckFunction> logger)
    {
        _validator = validator; _auth = auth.Value; _services = services; _clock = clock; _logger = logger;
    }

    [Function("auth-check")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/auth/check")] HttpRequest req, CancellationToken ct)
    {
        if (!_validator.Enabled) return new NotFoundResult();
        var policy = _services.GetRequiredService<EntraTokenPolicy>();
        var metadataUrl = ProtectedResourceMetadata.MetadataUrl(policy.ResourceUri);
        var response = req.HttpContext.Response;
        response.Headers.CacheControl = "no-store";

        var result = await _validator.ValidateAsync(req.Headers.Authorization.ToString(), ct);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Sign-in check refused ({Reason}, HTTP {Status})", result.ReasonCode, result.HttpStatus);
            return Challenge(response, McpChallenge.For(result.Failure, metadataUrl, policy.ClientScope));
        }

        var principal = result.Principal!;
        var roles = EntraRoleMapper.Map(_auth, principal.Roles);
        if (roles.Count == 0)
        {
            _logger.LogWarning("Sign-in check refused (no_role, HTTP 403)");
            return Challenge(response, McpChallenge.NoRole(metadataUrl, policy.ClientScope));
        }

        var body = new
        {
            status = "ok",
            roles = roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            scopes = principal.Scopes,
            expiresInSeconds = Math.Max(0, (long)(principal.ExpiresAt - _clock.UtcNow).TotalSeconds),
        };
        return Json(StatusCodes.Status200OK, body);
    }

    private static IActionResult Challenge(HttpResponse response, McpChallengeResponse challenge)
    {
        if (challenge.WwwAuthenticate is not null) response.Headers.WWWAuthenticate = challenge.WwwAuthenticate;
        if (challenge.RetryAfterSeconds is { } seconds) response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Json(challenge.StatusCode, new { error = challenge.Error });
    }

    private static ContentResult Json(int status, object body)
        => new() { StatusCode = status, ContentType = "application/json", Content = JsonSerializer.Serialize(body) };
}
