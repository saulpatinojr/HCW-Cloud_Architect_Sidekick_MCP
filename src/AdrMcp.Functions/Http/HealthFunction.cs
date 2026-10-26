using AdrMcp.Application.Options;
using AdrMcp.Application.Tools;
using AdrMcp.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Options;

namespace AdrMcp.Functions.Http;

/// <summary>Liveness probe for smoke tests and load balancers. Returns no configuration or secrets. The route is spelled out because host.json sets the route prefix to "" (ADR-0008).</summary>
public sealed class HealthFunction
{
    private readonly bool _ready;

    public HealthFunction(IOptions<AuthOptions> auth, PlatformAuthentication platform)
        // Entra mode rejects every call without App Service Authentication in front, so report unavailable instead of
        // letting a deployment go green that cannot serve a single request (the deploy job polls this probe).
        => _ready = !string.Equals(auth.Value.Mode, "EntraBuiltIn", StringComparison.OrdinalIgnoreCase) || platform.Enabled;

    [Function("health")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/health")] HttpRequest req)
        => _ready
            ? new OkObjectResult(new { status = "ok", server = "adr-intelligence", tools = ToolCatalog.Implemented.Count(), utc = DateTimeOffset.UtcNow })
            : new ObjectResult(new { status = "unavailable" }) { StatusCode = StatusCodes.Status503ServiceUnavailable };
}
