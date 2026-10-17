using AdrMcp.Analysis;
using AdrMcp.Application.Services;
using AdrMcp.Application.Tools;
using AdrMcp.Functions.Jobs;
using AdrMcp.Functions.Telemetry;
using AdrMcp.Infrastructure;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights for the isolated worker; the host also emits its own telemetry.
// Registered only when a connection string is configured so local runs without telemetry stay quiet.
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services
        .AddApplicationInsightsTelemetryWorkerService()
        .ConfigureFunctionsApplicationInsights();

    // Local authentication is disabled on the Application Insights resource, so the worker SDK must authenticate with Entra ID
    // using the same managed identity the host uses (see ApplicationInsightsAuthentication).
    if (ApplicationInsightsAuthentication.TryCreateCredential(builder.Configuration["APPLICATIONINSIGHTS_AUTHENTICATION_STRING"], out var telemetryCredential))
    {
        builder.Services.Configure<TelemetryConfiguration>(config => config.SetAzureTokenCredential(telemetryCredential));
    }
}

// The default worker rule drops Information logs for the App Insights provider; keep structured Information logs.
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    var toRemove = options.Rules.FirstOrDefault(rule => rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (toRemove is not null) options.Rules.Remove(toRemove);
});

builder.Services.AddAdrApplication(builder.Configuration);
builder.Services.AddAdrPersistence(builder.Configuration);
builder.Services.AddAdrAnalysis(builder.Configuration);
builder.Services.AddSingleton<AdrMcp.Functions.Mcp.McpToolAdapter>();

builder.Services.AddSingleton<IJobHandler, EvidenceProcessingJobHandler>();
builder.Services.AddSingleton<IJobHandler, TechnologyRefreshJobHandler>();
builder.Services.AddSingleton<IJobHandler, DriftScanJobHandler>();
builder.Services.AddSingleton<IJobHandler, ContradictionScanJobHandler>();

// Advertise exact JSON Schemas for every implemented tool (Worker.Extensions.Mcp >= 1.5.0).
// The catalog is the single source of truth shared with contract tests and documentation.
foreach (var tool in ToolCatalog.Implemented)
{
    builder.ConfigureMcpTool(tool.Name)
        .WithInputSchema(tool.InputSchemaJson)
        .WithOutputSchema(tool.OutputSchemaJson);
}

builder.Build().Run();
