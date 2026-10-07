using AdrMcp.Application.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace AdrMcp.Functions.Http;

/// <summary>Liveness probe for smoke tests and load balancers. Returns no configuration or secrets.</summary>
public sealed class HealthFunction
{
    [Function("health")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
        => new OkObjectResult(new { status = "ok", server = "adr-intelligence", tools = ToolCatalog.Implemented.Count(), utc = DateTimeOffset.UtcNow });
}
