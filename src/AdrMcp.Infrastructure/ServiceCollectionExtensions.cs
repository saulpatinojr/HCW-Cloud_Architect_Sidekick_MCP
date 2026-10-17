using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using AdrMcp.Application.Services;
using AdrMcp.Application.Tools;
using AdrMcp.Infrastructure.Blob;
using AdrMcp.Infrastructure.Cosmos;
using AdrMcp.Infrastructure.InMemory;
using AdrMcp.Infrastructure.Options;
using AdrMcp.Infrastructure.Queue;
using AdrMcp.Infrastructure.Tenancy;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Application services + persistence. Persistence:Provider selects Cosmos (default) or InMemory.</summary>
    public static IServiceCollection AddAdrApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AdrOptions>(configuration.GetSection(AdrOptions.Section));
        services.Configure<UploadOptions>(configuration.GetSection(UploadOptions.Section));
        services.Configure<JobOptions>(configuration.GetSection(JobOptions.Section));
        services.Configure<MemoryOptions>(configuration.GetSection(MemoryOptions.Section));
        services.Configure<AuditOptions>(configuration.GetSection(AuditOptions.Section));
        services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.Section));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddOptions<AuthOptions>().ValidateOnStart();
        services.AddSingleton(sp => new TenantDirectory(sp.GetRequiredService<IOptions<AuthOptions>>().Value));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(PlatformAuthentication.FromConfiguration(configuration));
        services.AddSingleton<ITenantResolver, HeaderTenantResolver>();
        services.AddSingleton<IAuthorizationService, RoleAuthorizationService>();

        services.AddSingleton<AdrService>();
        services.AddSingleton<ChangeProposalService>();
        services.AddSingleton<EvidenceService>();
        services.AddSingleton<AnalysisJobService>();
        services.AddSingleton<FileUploadService>();
        services.AddSingleton<TechnologyService>();
        services.AddSingleton<DriftService>();
        services.AddSingleton<ContradictionService>();
        services.AddSingleton<SafetyReviewService>();
        services.AddSingleton<AuditService>();
        services.AddSingleton<DecisionMemoryService>();
        services.AddSingleton<ToolInvocationPipeline>();
        services.AddSingleton<ToolHandlers>();
        services.AddSingleton<IEvidenceSetProvider, BlobEvidenceSetProvider>();
        return services;
    }

    public static IServiceCollection AddAdrPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.Section));
        services.Configure<CosmosOptions>(configuration.GetSection(CosmosOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));

        var provider = configuration[$"{PersistenceOptions.Section}:Provider"] ?? "Cosmos";
        var inMemory = string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase);

        // Durable persistence must never hash principals with a placeholder or unresolved salt: fail at host start.
        services.AddSingleton<IValidateOptions<AdrOptions>>(new PrincipalHashSaltValidator(requireConfiguredSalt: !inMemory));
        services.AddOptions<AdrOptions>().ValidateOnStart();

        if (inMemory)
        {
            services.AddSingleton<InMemoryDocumentStore>();
            services.AddSingleton<IAdrRepository, InMemoryAdrRepository>();
            services.AddSingleton<IAdrHistoryRepository, InMemoryAdrHistoryRepository>();
            services.AddSingleton<IProposalRepository, InMemoryProposalRepository>();
            services.AddSingleton<IUploadRepository, InMemoryUploadRepository>();
            services.AddSingleton<IFindingRepository, InMemoryFindingRepository>();
            services.AddSingleton<IJobRepository, InMemoryJobRepository>();
            services.AddSingleton<IAuditRepository, InMemoryAuditRepository>();
            services.AddSingleton<IMemoryRepository, InMemoryMemoryRepository>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        }
        else
        {
            services.AddSingleton(sp =>
            {
                var o = configuration.GetSection(CosmosOptions.Section).Get<CosmosOptions>() ?? new CosmosOptions();
                var clientOptions = new CosmosClientOptions
                {
                    ApplicationName = "adr-mcp",
                    UseSystemTextJsonSerializerWithOptions = Application.Common.AppJson.Options,
                    ConnectionMode = ConnectionMode.Gateway,
                    MaxRetryAttemptsOnRateLimitedRequests = 5,
                    MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(10)
                };
                if (!string.IsNullOrEmpty(o.ConnectionString)) return new CosmosClient(o.ConnectionString, clientOptions); // emulator only
                if (string.IsNullOrEmpty(o.AccountEndpoint)) throw new InvalidOperationException("Cosmos:AccountEndpoint is not configured.");
                return new CosmosClient(o.AccountEndpoint, CreateCredential(configuration), clientOptions);
            });
            services.AddSingleton<CosmosContext>();
            services.AddSingleton<IAdrRepository, CosmosAdrRepository>();
            services.AddSingleton<IAdrHistoryRepository, CosmosAdrHistoryRepository>();
            services.AddSingleton<IProposalRepository, CosmosProposalRepository>();
            services.AddSingleton<IUploadRepository, CosmosUploadRepository>();
            services.AddSingleton<IFindingRepository, CosmosFindingRepository>();
            services.AddSingleton<IJobRepository, CosmosJobRepository>();
            services.AddSingleton<IAuditRepository, CosmosAuditRepository>();
            services.AddSingleton<IMemoryRepository, CosmosMemoryRepository>();
            services.AddSingleton<IIdempotencyStore, CosmosIdempotencyStore>();
        }

        // Storage (Blob + Queue): Azurite connection string locally, managed identity in Azure.
        services.AddSingleton(sp =>
        {
            var o = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
            if (!string.IsNullOrEmpty(o.ConnectionString)) return new BlobServiceClient(o.ConnectionString);
            if (string.IsNullOrEmpty(o.BlobServiceUri)) throw new InvalidOperationException("Storage:BlobServiceUri is not configured.");
            return new BlobServiceClient(new Uri(o.BlobServiceUri), CreateCredential(configuration));
        });
        services.AddSingleton(sp =>
        {
            var o = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
            var qo = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
            if (!string.IsNullOrEmpty(o.ConnectionString)) return new QueueClient(o.ConnectionString, o.JobsQueue, qo);
            if (string.IsNullOrEmpty(o.QueueServiceUri)) throw new InvalidOperationException("Storage:QueueServiceUri is not configured.");
            return new QueueClient(new Uri(new Uri(o.QueueServiceUri), o.JobsQueue), CreateCredential(configuration), qo);
        });
        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(configuration[$"{StorageOptions.Section}:ConnectionString"]) && string.IsNullOrEmpty(configuration[$"{StorageOptions.Section}:BlobServiceUri"]))
        {
            services.AddSingleton<InMemoryEvidenceBlobStore>();
            services.AddSingleton<IEvidenceBlobStore>(sp => sp.GetRequiredService<InMemoryEvidenceBlobStore>());
            services.AddSingleton<InMemoryJobQueue>();
            services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<InMemoryJobQueue>());
        }
        else
        {
            services.AddSingleton<BlobEvidenceStore>();
            services.AddSingleton<IEvidenceBlobStore>(sp => sp.GetRequiredService<BlobEvidenceStore>());
            services.AddSingleton<StorageQueueJobQueue>();
            services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<StorageQueueJobQueue>());
        }
        return services;
    }

    /// <summary>Managed identity in Azure (user-assigned when AZURE_CLIENT_ID is set), developer credentials locally. No secrets.</summary>
    public static TokenCredential CreateCredential(IConfiguration configuration)
    {
        var clientId = configuration["AZURE_CLIENT_ID"];
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = string.IsNullOrEmpty(clientId) ? null : clientId,
            ExcludeInteractiveBrowserCredential = true,
        });
    }
}
