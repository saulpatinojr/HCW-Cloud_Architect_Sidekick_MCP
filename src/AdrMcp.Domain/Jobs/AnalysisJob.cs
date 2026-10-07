namespace AdrMcp.Domain.Jobs;

public enum JobState { Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned }

public enum JobType { EvidenceProcessing, TechnologyRefresh, DriftScan, ContradictionScan, RepositoryImport, DependencyMap, C4Generation, BulkContradictionScan }

public sealed record AnalysisJob
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required JobType Type { get; init; }
    public required string CorrelationId { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public JobState State { get; init; } = JobState.Queued;
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; } = 3;
    public string? DedupeKey { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public int ProgressPercent { get; init; }
    public string? StatusMessage { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool CancellationRequested { get; init; }
    /// <summary>Compact result summary; large results are written to Blob and referenced via ResultBlobPath.</summary>
    public string? ResultJson { get; init; }
    public string? ResultBlobPath { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public int? Ttl { get; init; }
    public string? ETag { get; init; }
    public string PartitionKey => $"{TenantId}|{WorkspaceId}";

    public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Expired or JobState.Poisoned;
    public bool IsCancellable => State is JobState.Queued or JobState.Running;
}
