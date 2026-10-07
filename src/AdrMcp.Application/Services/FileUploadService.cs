using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Files;
using AdrMcp.Domain.Jobs;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed record UploadRequestCommand(string FileName, string MediaType, long ExpectedSizeBytes, string? Sha256, string? AdrId, string? Note);

public sealed record UploadRequestResult(UploadRecord Record, UploadGrant Grant);

public sealed record RegisterUploadResult(UploadRecord Record, SafetyScanResult? Scan, AnalysisJob? Job, bool Replayed);

/// <summary>
/// Direct-to-Blob upload workflow: request (grant) → client PUT → register (verify, quarantine scan, sanitize, approve, queue analysis).
/// </summary>
public sealed class FileUploadService
{
    private readonly IUploadRepository _uploads;
    private readonly IEvidenceBlobStore _blobs;
    private readonly IContentSafetyService _safety;
    private readonly EvidenceService _evidence;
    private readonly AnalysisJobService _jobs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly UploadOptions _options;
    private readonly ILogger<FileUploadService> _logger;

    public FileUploadService(IUploadRepository uploads, IEvidenceBlobStore blobs, IContentSafetyService safety, EvidenceService evidence, AnalysisJobService jobs, IAuthorizationService authz, IClock clock, IOptions<UploadOptions> options, ILogger<FileUploadService> logger)
    {
        _uploads = uploads; _blobs = blobs; _safety = safety; _evidence = evidence; _jobs = jobs; _authz = authz; _clock = clock; _options = options.Value; _logger = logger;
    }

    public async Task<UploadRequestResult> RequestAsync(TenantContext ctx, string workspaceId, UploadRequestCommand cmd, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.EvidenceUpload);
        var safeName = FileValidation.SanitizeFileName(cmd.FileName);
        FileValidation.ValidateRequest(safeName, cmd.MediaType, cmd.ExpectedSizeBytes, _options);
        if (cmd.Sha256 is not null && !(cmd.Sha256.Length == 64 && cmd.Sha256.All(Uri.IsHexDigit))) throw new ValidationException("sha256 must be 64 hex characters.");
        if (cmd.AdrId is not null && !Ids.IsSafeId(cmd.AdrId)) throw new ValidationException("adrId is invalid.");

        // Duplicate short-circuit when the client already knows the hash.
        if (cmd.Sha256 is not null)
        {
            var dup = await _uploads.FindApprovedBySha256Async(ctx.TenantId, workspaceId, cmd.Sha256.ToLowerInvariant(), ct);
            if (dup is not null)
                throw new DomainException(ErrorCodes.Conflict, $"Identical content already exists as evidence '{dup.Id}'. Use evidence_attach to link it.",
                    details: new Dictionary<string, string> { ["duplicateOfEvidenceId"] = dup.Id });
        }

        var now = _clock.UtcNow;
        var uploadId = Ids.New("upl");
        var blobPath = $"{ctx.TenantId}/{workspaceId}/{uploadId}/{safeName}";
        var ttl = TimeSpan.FromMinutes(_options.UploadUrlTtlMinutes);
        var grant = await _blobs.CreateSingleBlobWriteGrantAsync(blobPath, cmd.MediaType, cmd.ExpectedSizeBytes, ttl, ct);

        var record = new UploadRecord
        {
            Id = uploadId,
            TenantId = ctx.TenantId,
            WorkspaceId = workspaceId,
            AdrId = cmd.AdrId,
            OriginalFileName = cmd.FileName.Length > 255 ? cmd.FileName[..255] : cmd.FileName,
            SafeFileName = safeName,
            DeclaredMediaType = cmd.MediaType,
            DeclaredSizeBytes = cmd.ExpectedSizeBytes,
            DeclaredSha256 = cmd.Sha256?.ToLowerInvariant(),
            QuarantineBlobPath = blobPath,
            RequestedBy = ctx.PrincipalId,
            RequestedAt = now,
            UploadUrlExpiresAt = grant.ExpiresAt,
            CorrelationId = ctx.CorrelationId,
            Ttl = (int)TimeSpan.FromHours(_options.QuarantineRetentionHours).TotalSeconds * 30
        };
        var created = await _uploads.CreateAsync(record, ct);
        _logger.LogInformation("Upload {UploadId} requested for {Workspace} ({Bytes} bytes, {MediaType})", uploadId, workspaceId, cmd.ExpectedSizeBytes, cmd.MediaType);
        return new UploadRequestResult(created, grant);
    }

    public async Task<RegisterUploadResult> RegisterAsync(TenantContext ctx, string workspaceId, string uploadId, string? sha256, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.EvidenceUpload);
        var record = await _uploads.GetAsync(ctx.TenantId, workspaceId, uploadId, ct)
            ?? throw new NotFoundException(ErrorCodes.UploadNotFound, $"Upload '{uploadId}' not found.");

        // Replay protection: registration is single-use; later calls return the recorded outcome.
        if (record.State is not UploadState.Requested)
            return new RegisterUploadResult(record, null, null, Replayed: true);

        var now = _clock.UtcNow;
        var props = await _blobs.GetPropertiesAsync(EvidenceContainer.Quarantine, record.QuarantineBlobPath, ct);
        if (!props.Exists)
        {
            if (record.UploadUrlExpiresAt < now)
            {
                await _uploads.ReplaceAsync(record with { State = UploadState.Expired, RejectionReason = "Upload URL expired before any content was uploaded." }, ct);
                throw new DomainException(ErrorCodes.UploadExpired, "The upload grant expired and no blob was uploaded. Request a new upload.");
            }
            throw new DomainException(ErrorCodes.UploadNotFound, "The blob has not been uploaded yet. Complete the PUT and retry.", retryable: true);
        }

        record = record with { State = UploadState.Uploaded, RegisteredAt = now };

        // 1. Size checks (declared vs actual vs policy).
        if (props.Length > _options.MaxFileBytes || props.Length > record.DeclaredSizeBytes * 1.05 + 1024)
            return await RejectAsync(record, ErrorCodes.PayloadTooLarge, $"Uploaded size {props.Length} exceeds declared/allowed size.", ct);

        // 2. Checksum (always computed server-side; client-declared value is compared if present).
        var (actualSha, bytes) = await _blobs.ComputeSha256Async(EvidenceContainer.Quarantine, record.QuarantineBlobPath, _options.MaxFileBytes, ct);
        var declared = (sha256 ?? record.DeclaredSha256)?.ToLowerInvariant();
        if (declared is not null && declared != actualSha)
            return await RejectAsync(record, ErrorCodes.ChecksumMismatch, "SHA-256 of the uploaded blob does not match the declared checksum.", ct);
        record = record with { ActualSha256 = actualSha, ActualSizeBytes = bytes, State = UploadState.Quarantined };

        // 3. Duplicate detection by content hash.
        var duplicate = await _uploads.FindApprovedBySha256Async(ctx.TenantId, workspaceId, actualSha, ct);
        if (duplicate is not null && duplicate.Id != record.Id)
        {
            await _blobs.DeleteIfExistsAsync(EvidenceContainer.Quarantine, record.QuarantineBlobPath, ct);
            var dupRecord = await _uploads.ReplaceAsync(record with { State = UploadState.Duplicate, DuplicateOfEvidenceId = duplicate.Id, RejectionReason = "Duplicate content." }, ct);
            if (record.AdrId is not null) await _evidence.AttachAsync(ctx, workspaceId, record.AdrId, duplicate.Id, "Attached existing identical evidence (duplicate upload).", ct);
            return new RegisterUploadResult(dupRecord, null, null, false);
        }

        // 4. MIME verification against the first bytes.
        var head = await _blobs.ReadBytesAsync(EvidenceContainer.Quarantine, record.QuarantineBlobPath, 512, ct);
        var (plausible, detected) = FileValidation.SniffMediaType(head, record.DeclaredMediaType);
        if (!plausible) return await RejectAsync(record, ErrorCodes.UnsupportedFileType, $"Content does not match declared media type (detected {detected}).", ct);
        record = record with { VerifiedMediaType = detected == "text/*" ? record.DeclaredMediaType : detected };

        // 5. Optional Microsoft Defender for Storage malware-scan result (blob index tag written by Defender).
        if (_options.RequireDefenderScanResult)
        {
            if (!props.Tags.TryGetValue("Malware Scanning scan result", out var verdict))
                throw new DomainException(ErrorCodes.Conflict, "Malware scan result not yet available. Retry shortly.", retryable: true);
            if (!string.Equals(verdict, "No threats found", StringComparison.OrdinalIgnoreCase))
                return await RejectAsync(record, ErrorCodes.UploadRejected, "Malware scanner reported a threat.", ct);
        }

        // 6. Content safety scan + sanitized variant for text content. Binary content is stored but marked unscanned.
        SafetyScanResult? scan = null;
        string? sanitizedPath = null;
        var isText = FileValidation.IsTextMediaType(record.DeclaredMediaType, record.SafeFileName) && FileValidation.IsProbablyText(head);
        if (isText && bytes <= _options.MaxTextScanBytes)
        {
            record = record with { State = UploadState.Scanning };
            var text = await _blobs.ReadTextAsync(EvidenceContainer.Quarantine, record.QuarantineBlobPath, (int)Math.Min(bytes, int.MaxValue), ct);
            scan = await _safety.ScanAsync(text, new SafetyScanOptions(record.SafeFileName, _options.BlockOnCriticalSafetyFinding), ct);
            record = record with { SafetyFindings = scan.Detections.Select(d => new SafetyFindingSummary(d.Type, d.Severity.ToString(), $"L{d.Line}:C{d.Column}", d.Action.ToString(), d.Detector)).ToList() };
            if (scan.Blocked)
            {
                return await RejectAsync(record, ErrorCodes.ContentSafetyBlocked, $"Blocked by content-safety policy: {scan.Detections.Count(d => d.Severity == SafetySeverity.Critical)} critical finding(s). The secret values were not retained.", ct, scan);
            }
            sanitizedPath = record.QuarantineBlobPath;
            await _blobs.WriteTextAsync(EvidenceContainer.Sanitized, sanitizedPath, scan.SanitizedText, "text/plain; charset=utf-8",
                new Dictionary<string, string> { ["sourceSha256"] = actualSha, ["detections"] = scan.Detections.Count.ToString(), ["promptInjectionSuspected"] = scan.PromptInjectionSuspected.ToString() }, ct);
        }

        // 7. Promote to the approved container (immutable evidence snapshot) and remove from quarantine.
        var approvedPath = record.QuarantineBlobPath;
        await _blobs.CopyAsync(EvidenceContainer.Quarantine, record.QuarantineBlobPath, EvidenceContainer.Approved, approvedPath, ct);
        await _blobs.DeleteIfExistsAsync(EvidenceContainer.Quarantine, record.QuarantineBlobPath, ct);
        record = record with { State = UploadState.Approved, ApprovedBlobPath = approvedPath, SanitizedBlobPath = sanitizedPath, Immutable = true, Ttl = null };
        var saved = await _uploads.ReplaceAsync(record, ct);

        // 8. Attach to ADR when requested, then queue asynchronous analysis.
        if (saved.AdrId is not null) await _evidence.AttachAsync(ctx, workspaceId, saved.AdrId, saved.Id, "Attached on upload registration.", ct);
        var job = await _jobs.EnqueueAsync(ctx, workspaceId, JobType.EvidenceProcessing,
            new Dictionary<string, string> { ["uploadId"] = saved.Id, ["adrId"] = saved.AdrId ?? "" }, dedupeKey: $"evidence:{saved.Id}", ct);
        _logger.LogInformation("Upload {UploadId} approved ({Bytes} bytes, {Detections} safety detections); job {JobId} queued", saved.Id, bytes, scan?.Detections.Count ?? 0, job.Id);
        return new RegisterUploadResult(saved with { ScanJobId = job.Id }, scan, job, false);
    }

    /// <summary>Removes quarantine blobs whose grant expired without registration. Called by a timer trigger.</summary>
    public async Task<int> CleanupExpiredAsync(string tenantId, string workspaceId, CancellationToken ct)
    {
        var expired = await _uploads.ListExpiredRequestsAsync(tenantId, workspaceId, _clock.UtcNow.AddHours(-_options.QuarantineRetentionHours), ct);
        var count = 0;
        foreach (var r in expired)
        {
            await _blobs.DeleteIfExistsAsync(EvidenceContainer.Quarantine, r.QuarantineBlobPath, ct);
            await _uploads.ReplaceAsync(r with { State = UploadState.Expired, RejectionReason = "Expired without registration." }, ct);
            count++;
        }
        return count;
    }

    private async Task<RegisterUploadResult> RejectAsync(UploadRecord record, string code, string reason, CancellationToken ct, SafetyScanResult? scan = null)
    {
        if (!_options.RetainOriginalOnReject) await _blobs.DeleteIfExistsAsync(EvidenceContainer.Quarantine, record.QuarantineBlobPath, ct);
        var rejected = await _uploads.ReplaceAsync(record with { State = UploadState.Rejected, RejectionReason = reason }, ct);
        _logger.LogWarning("Upload {UploadId} rejected: {Code}", record.Id, code);
        throw new DomainException(code, reason, details: new Dictionary<string, string> { ["uploadId"] = rejected.Id, ["state"] = rejected.State.ToString() });
    }
}
