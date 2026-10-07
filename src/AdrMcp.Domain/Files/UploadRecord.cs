namespace AdrMcp.Domain.Files;

public enum UploadState { Requested, Uploaded, Quarantined, Scanning, Approved, Rejected, Expired, Removed, Duplicate }

/// <summary>Tracks a single-object upload from request to approval. Also serves as the evidence-file metadata record.</summary>
public sealed record UploadRecord
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? AdrId { get; init; }
    public required string OriginalFileName { get; init; }
    public required string SafeFileName { get; init; }
    public required string DeclaredMediaType { get; init; }
    public required long DeclaredSizeBytes { get; init; }
    public string? DeclaredSha256 { get; init; }
    public required string QuarantineBlobPath { get; init; }
    public string? ApprovedBlobPath { get; init; }
    public string? SanitizedBlobPath { get; init; }
    public long? ActualSizeBytes { get; init; }
    public string? ActualSha256 { get; init; }
    public string? VerifiedMediaType { get; init; }
    public UploadState State { get; init; } = UploadState.Requested;
    public required string RequestedBy { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset UploadUrlExpiresAt { get; init; }
    public DateTimeOffset? RegisteredAt { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<SafetyFindingSummary> SafetyFindings { get; init; } = Array.Empty<SafetyFindingSummary>();
    public string? DuplicateOfEvidenceId { get; init; }
    public string? ScanJobId { get; init; }
    public string? CorrelationId { get; init; }
    public bool Immutable { get; init; }
    public int? Ttl { get; init; }
    public string? ETag { get; init; }
    public string PartitionKey => $"{TenantId}|{WorkspaceId}";
}

/// <summary>Finding summary that is safe to persist: never includes the matched secret.</summary>
public sealed record SafetyFindingSummary(string Type, string Severity, string Location, string Action, string? Detector);
