namespace AdrMcp.Application.Abstractions;

public sealed record BlobProperties(bool Exists, long Length, string? ContentType, string? ETag, DateTimeOffset? LastModified, IReadOnlyDictionary<string, string> Tags);

/// <summary>Write-only, single-object, short-lived upload grant. Never carries account keys.</summary>
public sealed record UploadGrant(Uri UploadUri, string HttpMethod, IReadOnlyDictionary<string, string> RequiredHeaders, DateTimeOffset ExpiresAt);

public enum EvidenceContainer { Quarantine, Approved, Sanitized, AdrSource, Reports, Snapshots, Manifests, Diagrams }

/// <summary>Blob operations used by the application. Paths are always tenant-scoped by the caller and validated by the implementation.</summary>
public interface IEvidenceBlobStore
{
    Task<UploadGrant> CreateSingleBlobWriteGrantAsync(string quarantineBlobPath, string contentType, long maxBytes, TimeSpan ttl, CancellationToken ct);
    Task<BlobProperties> GetPropertiesAsync(EvidenceContainer container, string blobPath, CancellationToken ct);
    /// <summary>Streams the blob, hashing it, with a hard byte cap. Returns (sha256, bytesRead, textPreviewIfText).</summary>
    Task<(string Sha256, long Bytes)> ComputeSha256Async(EvidenceContainer container, string blobPath, long maxBytes, CancellationToken ct);
    Task<string> ReadTextAsync(EvidenceContainer container, string blobPath, int maxBytes, CancellationToken ct);
    Task<byte[]> ReadBytesAsync(EvidenceContainer container, string blobPath, int maxBytes, CancellationToken ct);
    Task WriteTextAsync(EvidenceContainer container, string blobPath, string text, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct);
    Task CopyAsync(EvidenceContainer sourceContainer, string sourcePath, EvidenceContainer targetContainer, string targetPath, CancellationToken ct);
    Task DeleteIfExistsAsync(EvidenceContainer container, string blobPath, CancellationToken ct);
}

public interface IJobQueue
{
    Task EnqueueAsync(string jobId, string partitionKey, string correlationId, TimeSpan? visibilityDelay, CancellationToken ct);
}
