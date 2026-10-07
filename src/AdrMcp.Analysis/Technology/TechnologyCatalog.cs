using AdrMcp.Domain.Adrs;

namespace AdrMcp.Analysis.Technology;

/// <summary>Normalisation and categorisation of well-known package/image/resource names. Unknown names default to Library.</summary>
public static class TechnologyCatalog
{
    private static readonly Dictionary<string, (string Name, TechnologyCategory Category)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // .NET
        ["Microsoft.Azure.Cosmos"] = ("Azure Cosmos DB SDK", TechnologyCategory.Database),
        ["Microsoft.EntityFrameworkCore"] = ("Entity Framework Core", TechnologyCategory.Framework),
        ["Microsoft.EntityFrameworkCore.SqlServer"] = ("SQL Server (EF Core provider)", TechnologyCategory.Database),
        ["Npgsql"] = ("PostgreSQL (Npgsql)", TechnologyCategory.Database),
        ["Npgsql.EntityFrameworkCore.PostgreSQL"] = ("PostgreSQL (EF Core provider)", TechnologyCategory.Database),
        ["MongoDB.Driver"] = ("MongoDB", TechnologyCategory.Database),
        ["StackExchange.Redis"] = ("Redis", TechnologyCategory.Database),
        ["Microsoft.Data.SqlClient"] = ("SQL Server", TechnologyCategory.Database),
        ["System.Data.SqlClient"] = ("SQL Server (legacy client)", TechnologyCategory.Database),
        ["MySql.Data"] = ("MySQL", TechnologyCategory.Database),
        ["Dapper"] = ("Dapper", TechnologyCategory.Library),
        ["Newtonsoft.Json"] = ("Newtonsoft.Json", TechnologyCategory.Library),
        ["Azure.Messaging.ServiceBus"] = ("Azure Service Bus", TechnologyCategory.Messaging),
        ["Azure.Messaging.EventHubs"] = ("Azure Event Hubs", TechnologyCategory.Messaging),
        ["RabbitMQ.Client"] = ("RabbitMQ", TechnologyCategory.Messaging),
        ["Confluent.Kafka"] = ("Apache Kafka", TechnologyCategory.Messaging),
        ["MassTransit"] = ("MassTransit", TechnologyCategory.Messaging),
        ["Azure.Storage.Blobs"] = ("Azure Blob Storage SDK", TechnologyCategory.CloudService),
        ["Azure.Storage.Queues"] = ("Azure Queue Storage SDK", TechnologyCategory.CloudService),
        ["Azure.Identity"] = ("Azure Identity (Managed Identity/Entra)", TechnologyCategory.Identity),
        ["Microsoft.Identity.Web"] = ("Microsoft Identity Web (Entra ID)", TechnologyCategory.Identity),
        ["Microsoft.Azure.Functions.Worker"] = ("Azure Functions (isolated worker)", TechnologyCategory.Runtime),
        ["Microsoft.Azure.Functions.Worker.Extensions.Mcp"] = ("Azure Functions MCP extension", TechnologyCategory.Framework),
        ["Microsoft.ApplicationInsights"] = ("Application Insights", TechnologyCategory.Observability),
        ["Microsoft.Azure.Functions.Worker.ApplicationInsights"] = ("Application Insights", TechnologyCategory.Observability),
        ["OpenTelemetry"] = ("OpenTelemetry", TechnologyCategory.Observability),
        ["Serilog"] = ("Serilog", TechnologyCategory.Observability),
        ["Polly"] = ("Polly", TechnologyCategory.Library),
        ["MediatR"] = ("MediatR", TechnologyCategory.Library),
        ["AutoMapper"] = ("AutoMapper", TechnologyCategory.Library),
        ["Swashbuckle.AspNetCore"] = ("Swagger/OpenAPI (Swashbuckle)", TechnologyCategory.Tooling),
        ["xunit"] = ("xUnit", TechnologyCategory.Tooling),
        ["NUnit"] = ("NUnit", TechnologyCategory.Tooling),
        ["Moq"] = ("Moq", TechnologyCategory.Tooling),
        // Node
        ["express"] = ("Express", TechnologyCategory.Framework),
        ["react"] = ("React", TechnologyCategory.Framework),
        ["next"] = ("Next.js", TechnologyCategory.Framework),
        ["vue"] = ("Vue.js", TechnologyCategory.Framework),
        ["@angular/core"] = ("Angular", TechnologyCategory.Framework),
        ["fastify"] = ("Fastify", TechnologyCategory.Framework),
        ["@nestjs/core"] = ("NestJS", TechnologyCategory.Framework),
        ["mongoose"] = ("MongoDB (Mongoose)", TechnologyCategory.Database),
        ["pg"] = ("PostgreSQL (pg)", TechnologyCategory.Database),
        ["mysql2"] = ("MySQL", TechnologyCategory.Database),
        ["redis"] = ("Redis", TechnologyCategory.Database),
        ["ioredis"] = ("Redis (ioredis)", TechnologyCategory.Database),
        ["@azure/cosmos"] = ("Azure Cosmos DB SDK", TechnologyCategory.Database),
        ["@azure/storage-blob"] = ("Azure Blob Storage SDK", TechnologyCategory.CloudService),
        ["@azure/service-bus"] = ("Azure Service Bus", TechnologyCategory.Messaging),
        ["@azure/identity"] = ("Azure Identity", TechnologyCategory.Identity),
        ["amqplib"] = ("RabbitMQ (amqplib)", TechnologyCategory.Messaging),
        ["kafkajs"] = ("Apache Kafka", TechnologyCategory.Messaging),
        ["typescript"] = ("TypeScript", TechnologyCategory.Language),
        ["jest"] = ("Jest", TechnologyCategory.Tooling),
        ["vitest"] = ("Vitest", TechnologyCategory.Tooling),
        ["@modelcontextprotocol/sdk"] = ("MCP TypeScript SDK", TechnologyCategory.Framework),
        ["aws-sdk"] = ("AWS SDK", TechnologyCategory.CloudService),
        ["@aws-sdk/client-s3"] = ("AWS S3 SDK", TechnologyCategory.CloudService),
        // Python
        ["django"] = ("Django", TechnologyCategory.Framework),
        ["flask"] = ("Flask", TechnologyCategory.Framework),
        ["fastapi"] = ("FastAPI", TechnologyCategory.Framework),
        ["sqlalchemy"] = ("SQLAlchemy", TechnologyCategory.Library),
        ["psycopg2"] = ("PostgreSQL (psycopg2)", TechnologyCategory.Database),
        ["psycopg2-binary"] = ("PostgreSQL (psycopg2)", TechnologyCategory.Database),
        ["pymongo"] = ("MongoDB", TechnologyCategory.Database),
        ["azure-cosmos"] = ("Azure Cosmos DB SDK", TechnologyCategory.Database),
        ["azure-storage-blob"] = ("Azure Blob Storage SDK", TechnologyCategory.CloudService),
        ["azure-identity"] = ("Azure Identity", TechnologyCategory.Identity),
        ["boto3"] = ("AWS SDK (boto3)", TechnologyCategory.CloudService),
        ["celery"] = ("Celery", TechnologyCategory.Messaging),
        ["pika"] = ("RabbitMQ (pika)", TechnologyCategory.Messaging),
        ["pytest"] = ("pytest", TechnologyCategory.Tooling),
        ["openai"] = ("OpenAI SDK", TechnologyCategory.Library),
        ["anthropic"] = ("Anthropic SDK", TechnologyCategory.Library),
        // Images
        ["postgres"] = ("PostgreSQL", TechnologyCategory.Database),
        ["mysql"] = ("MySQL", TechnologyCategory.Database),
        ["mongo"] = ("MongoDB", TechnologyCategory.Database),
        ["rabbitmq"] = ("RabbitMQ", TechnologyCategory.Messaging),
        ["nginx"] = ("NGINX", TechnologyCategory.Runtime),
        ["node"] = ("Node.js", TechnologyCategory.Runtime),
        ["python"] = ("Python", TechnologyCategory.Runtime),
        ["mcr.microsoft.com/dotnet/aspnet"] = ("ASP.NET Core runtime image", TechnologyCategory.Runtime),
        ["mcr.microsoft.com/dotnet/sdk"] = (".NET SDK image", TechnologyCategory.Runtime),
        ["mcr.microsoft.com/dotnet/runtime"] = (".NET runtime image", TechnologyCategory.Runtime),
        ["mcr.microsoft.com/azure-functions/dotnet-isolated"] = ("Azure Functions dotnet-isolated image", TechnologyCategory.Runtime),
    };

    public static (string Name, TechnologyCategory Category) Normalize(string raw, TechnologyCategory fallback = TechnologyCategory.Library)
    {
        var key = raw.Trim();
        if (Known.TryGetValue(key, out var k)) return k;
        // Image names: strip registry/tag; try the last path segment.
        var noTag = key.Split('@')[0];
        var lastSeg = noTag.Split('/').Last();
        if (Known.TryGetValue(lastSeg, out var k2)) return k2;
        return (key, fallback);
    }

    public static TechnologyCategory CategoryForAzureResource(string type) => type.ToLowerInvariant() switch
    {
        var t when t.StartsWith("microsoft.documentdb") => TechnologyCategory.Database,
        var t when t.StartsWith("microsoft.sql") || t.StartsWith("microsoft.dbfor") => TechnologyCategory.Database,
        var t when t.StartsWith("microsoft.cache") => TechnologyCategory.Database,
        var t when t.StartsWith("microsoft.servicebus") || t.StartsWith("microsoft.eventhub") || t.StartsWith("microsoft.eventgrid") => TechnologyCategory.Messaging,
        var t when t.StartsWith("microsoft.containerservice") || t.StartsWith("microsoft.app/") => TechnologyCategory.Orchestration,
        var t when t.StartsWith("microsoft.web") => TechnologyCategory.Runtime,
        var t when t.StartsWith("microsoft.keyvault") || t.StartsWith("microsoft.managedidentity") => TechnologyCategory.Identity,
        var t when t.StartsWith("microsoft.insights") || t.StartsWith("microsoft.operationalinsights") => TechnologyCategory.Observability,
        _ => TechnologyCategory.CloudService
    };
}
