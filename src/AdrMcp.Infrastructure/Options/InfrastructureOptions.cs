namespace AdrMcp.Infrastructure.Options;

public sealed class PersistenceOptions
{
    public const string Section = "Persistence";
    /// <summary>"Cosmos" (default) or "InMemory" (local development/tests only; nothing survives a restart).</summary>
    public string Provider { get; set; } = "Cosmos";
}

public sealed class CosmosOptions
{
    public const string Section = "Cosmos";
    public string AccountEndpoint { get; set; } = "";
    /// <summary>Local emulator only. Production uses managed identity; never set this in Azure app settings.</summary>
    public string? ConnectionString { get; set; }
    public string DatabaseName { get; set; } = "adr";
    public string AdrsContainer { get; set; } = "adrs";
    public string HistoryContainer { get; set; } = "adr-history";
    public string ProposalsContainer { get; set; } = "proposals";
    public string EvidenceContainer { get; set; } = "evidence";
    public string FindingsContainer { get; set; } = "findings";
    public string JobsContainer { get; set; } = "jobs";
    public string MemoryContainer { get; set; } = "memory";
    public string AuditContainer { get; set; } = "audit";
    public string IdempotencyContainer { get; set; } = "idempotency";
    public string PartitionKeyPath { get; set; } = "/partitionKey";
}

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>Local Azurite only ("UseDevelopmentStorage=true"). Production uses BlobServiceUri/QueueServiceUri + managed identity.</summary>
    public string? ConnectionString { get; set; }
    public string BlobServiceUri { get; set; } = "";
    public string QueueServiceUri { get; set; } = "";
    public string QuarantineContainer { get; set; } = "uploads-quarantine";
    public string ApprovedContainer { get; set; } = "evidence";
    public string SanitizedContainer { get; set; } = "evidence-sanitized";
    public string AdrSourceContainer { get; set; } = "adr-source";
    public string ReportsContainer { get; set; } = "reports";
    public string SnapshotsContainer { get; set; } = "snapshots";
    public string ManifestsContainer { get; set; } = "manifests";
    public string DiagramsContainer { get; set; } = "diagrams";
    public string JobsQueue { get; set; } = "analysis-jobs";
}
