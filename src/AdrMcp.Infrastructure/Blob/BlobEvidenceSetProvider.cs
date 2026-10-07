using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Services;
using AdrMcp.Domain.Files;
using Microsoft.Extensions.Logging;

namespace AdrMcp.Infrastructure.Blob;

/// <summary>Loads the SANITIZED text of approved evidence for analyzers. Original content is never handed to analysis.</summary>
public sealed class BlobEvidenceSetProvider : IEvidenceSetProvider
{
    private readonly IUploadRepository _uploads;
    private readonly IEvidenceBlobStore _blobs;
    private readonly ILogger<BlobEvidenceSetProvider> _logger;

    public BlobEvidenceSetProvider(IUploadRepository uploads, IEvidenceBlobStore blobs, ILogger<BlobEvidenceSetProvider> logger)
    {
        _uploads = uploads; _blobs = blobs; _logger = logger;
    }

    public async Task<EvidenceSet> LoadForWorkspaceAsync(string tenantId, string workspaceId, string? adrId, int maxFiles, int maxBytesPerFile, CancellationToken ct)
    {
        var records = await _uploads.ListApprovedAsync(tenantId, workspaceId, adrId, ct);
        var files = new List<EvidenceFile>();
        foreach (var r in records.Where(r => r.State == UploadState.Approved && r.SanitizedBlobPath is not null).Take(maxFiles))
        {
            try
            {
                var text = await _blobs.ReadTextAsync(EvidenceContainer.Sanitized, r.SanitizedBlobPath!, maxBytesPerFile, ct);
                files.Add(new EvidenceFile(r.Id, r.OriginalFileName.Replace('\\', '/'), r.VerifiedMediaType ?? r.DeclaredMediaType, text, r.ActualSha256 ?? "", r.AdrId));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not load sanitized evidence {EvidenceId}", r.Id); }
        }
        return new EvidenceSet(files);
    }
}
