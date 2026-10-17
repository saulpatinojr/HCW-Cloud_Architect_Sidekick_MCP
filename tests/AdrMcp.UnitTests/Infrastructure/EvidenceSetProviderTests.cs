using System.Text;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Files;
using AdrMcp.Infrastructure.Blob;
using AdrMcp.Infrastructure.InMemory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdrMcp.UnitTests.Infrastructure;

public class EvidenceSetProviderTests
{
    [Fact]
    public async Task Unreadable_file_is_skipped_but_a_cancelled_caller_gets_cancellation_not_a_partial_set()
    {
        var uploads = new InMemoryUploadRepository(new InMemoryDocumentStore());
        var blobs = new CancellableBlobStore();
        foreach (var (id, stored) in new[] { ("e1", true), ("e2", false) })
        {
            var path = $"t/w/{id}/package.json";
            await uploads.CreateAsync(new UploadRecord
            {
                Id = id, TenantId = "t", WorkspaceId = "w", OriginalFileName = "package.json", SafeFileName = "package.json",
                DeclaredMediaType = "application/json", DeclaredSizeBytes = 2, QuarantineBlobPath = path, SanitizedBlobPath = path,
                State = UploadState.Approved, RequestedBy = "u", RequestedAt = DateTimeOffset.UtcNow, UploadUrlExpiresAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            if (stored) blobs.Inner.Put(EvidenceContainer.Sanitized, path, Encoding.UTF8.GetBytes("{}"));
        }
        var provider = new BlobEvidenceSetProvider(uploads, blobs, NullLogger<BlobEvidenceSetProvider>.Instance);

        var set = await provider.LoadForWorkspaceAsync("t", "w", null, 10, 1024, CancellationToken.None);
        Assert.Equal("e1", Assert.Single(set.Files).EvidenceId);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.LoadForWorkspaceAsync("t", "w", null, 10, 1024, cancelled.Token));
    }

    // The in-memory store ignores tokens; the Azure SDK honours them, which is what this wrapper reproduces.
    private sealed class CancellableBlobStore : IEvidenceBlobStore
    {
        public InMemoryEvidenceBlobStore Inner { get; } = new();
        public Task<string> ReadTextAsync(EvidenceContainer container, string blobPath, int maxBytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Inner.ReadTextAsync(container, blobPath, maxBytes, ct);
        }
        public Task<UploadGrant> CreateSingleBlobWriteGrantAsync(string quarantineBlobPath, string contentType, long maxBytes, TimeSpan ttl, CancellationToken ct) => Inner.CreateSingleBlobWriteGrantAsync(quarantineBlobPath, contentType, maxBytes, ttl, ct);
        public Task<BlobProperties> GetPropertiesAsync(EvidenceContainer container, string blobPath, CancellationToken ct) => Inner.GetPropertiesAsync(container, blobPath, ct);
        public Task<(string Sha256, long Bytes)> ComputeSha256Async(EvidenceContainer container, string blobPath, long maxBytes, CancellationToken ct) => Inner.ComputeSha256Async(container, blobPath, maxBytes, ct);
        public Task<byte[]> ReadBytesAsync(EvidenceContainer container, string blobPath, int maxBytes, CancellationToken ct) => Inner.ReadBytesAsync(container, blobPath, maxBytes, ct);
        public Task WriteTextAsync(EvidenceContainer container, string blobPath, string text, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct) => Inner.WriteTextAsync(container, blobPath, text, contentType, metadata, ct);
        public Task CopyAsync(EvidenceContainer sourceContainer, string sourcePath, EvidenceContainer targetContainer, string targetPath, CancellationToken ct) => Inner.CopyAsync(sourceContainer, sourcePath, targetContainer, targetPath, ct);
        public Task DeleteIfExistsAsync(EvidenceContainer container, string blobPath, CancellationToken ct) => Inner.DeleteIfExistsAsync(container, blobPath, ct);
    }
}
