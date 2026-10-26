using System.Text.Json;
using AdrMcp.Application.Abstractions;
using AdrMcp.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace AdrMcp.Functions.Http;

/// <summary>
/// OAuth 2.0 Protected Resource Metadata (RFC 9728) for the MCP endpoint, which the MCP authorization spec (revision 2026-07-28)
/// requires every HTTP MCP server to publish. Served at the root well-known URI and at the path-inserted one
/// (<c>/.well-known/oauth-protected-resource/runtime/webhooks/mcp</c>); any other path is 404. Public metadata, no secrets, so it is anonymous.
/// Answers 404 while <c>Auth:Entra:ValidateBearerToken</c> is off. With App Service Authentication in front, the platform answers the
/// root URI itself (preview feature); this is the app's own copy, so discovery does not depend on that preview (ADR-0008).
/// </summary>
public sealed class ProtectedResourceMetadataFunction
{
    private readonly IBearerTokenValidator _validator;
    private readonly IServiceProvider _services;

    public ProtectedResourceMetadataFunction(IBearerTokenValidator validator, IServiceProvider services)
    {
        _validator = validator; _services = services;
    }

    [Function("protected-resource-metadata")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ".well-known/oauth-protected-resource/{*path}")] HttpRequest req, string? path)
    {
        if (!_validator.Enabled) return new NotFoundResult();
        var policy = _services.GetRequiredService<EntraTokenPolicy>();
        var requested = (path ?? "").Trim('/');
        if (requested.Length != 0 && !string.Equals(requested, ProtectedResourceMetadata.ResourcePath(policy.ResourceUri), StringComparison.Ordinal)) return new NotFoundResult();

        req.HttpContext.Response.Headers.CacheControl = "public, max-age=300";
        req.HttpContext.Response.Headers.AccessControlAllowOrigin = "*";   // public metadata; lets browser-based MCP clients read it
        return new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "application/json",
            Content = JsonSerializer.Serialize(ProtectedResourceMetadata.Build(policy)),
        };
    }
}
