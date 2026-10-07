namespace AdrMcp.Domain.Adrs;

public sealed record LinkedFile(
    string Path,
    string? RepositoryId,
    string? ContentHash,
    string? Reason,
    double Relevance,
    DateTimeOffset LinkedAt,
    string LinkedBy);

/// <summary>Reference from an ADR to an evidence file (quarantined, approved and sanitized variants live in Blob storage).</summary>
public sealed record EvidenceReference(
    string EvidenceId,
    string FileName,
    string MediaType,
    long SizeBytes,
    string Sha256,
    string BlobPath,
    string? SanitizedBlobPath,
    DateTimeOffset AttachedAt,
    string AttachedBy,
    string? Note);
