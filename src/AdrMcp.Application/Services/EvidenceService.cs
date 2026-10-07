using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Files;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed class EvidenceService
{
    private readonly IUploadRepository _uploads;
    private readonly AdrService _adrs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly AdrOptions _options;

    public EvidenceService(IUploadRepository uploads, AdrService adrs, IAuthorizationService authz, IClock clock, IOptions<AdrOptions> options)
    {
        _uploads = uploads; _adrs = adrs; _authz = authz; _clock = clock; _options = options.Value;
    }

    public async Task<Adr> AttachAsync(TenantContext ctx, string workspaceId, string adrId, string evidenceId, string? note, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.EvidenceUpload);
        var upload = await _uploads.GetAsync(ctx.TenantId, workspaceId, evidenceId, ct) ?? throw new NotFoundException(ErrorCodes.NotFound, $"Evidence '{evidenceId}' not found.");
        if (upload.State != UploadState.Approved || upload.ApprovedBlobPath is null) throw new DomainException(ErrorCodes.Conflict, $"Evidence '{evidenceId}' is in state {upload.State} and cannot be attached.");
        _ = await _adrs.GetAsync(ctx, workspaceId, adrId, ct);
        var reference = new EvidenceReference(upload.Id, upload.SafeFileName, upload.VerifiedMediaType ?? upload.DeclaredMediaType, upload.ActualSizeBytes ?? 0, upload.ActualSha256 ?? "", upload.ApprovedBlobPath, upload.SanitizedBlobPath, _clock.UtcNow, ctx.PrincipalId, note);
        var provenance = Provenance.Human(ctx.PrincipalId, _clock.UtcNow, ctx.CorrelationId, "evidence_attach");
        return await _adrs.ApplyObservedUpdateAsync(ctx.TenantId, workspaceId, adrId,
            o => o.Evidence.Any(e => e.EvidenceId == evidenceId) ? o : o with { Evidence = o.Evidence.Append(reference).ToList() },
            provenance, $"Evidence {evidenceId} attached", ct);
    }

    public async Task<IReadOnlyList<UploadRecord>> ListAsync(TenantContext ctx, string workspaceId, string? adrId, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrRead);
        return await _uploads.ListApprovedAsync(ctx.TenantId, workspaceId, adrId, ct);
    }

    public async Task<Adr> RemoveAsync(TenantContext ctx, string workspaceId, string adrId, string evidenceId, string reason, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.EvidenceRemove);
        var provenance = Provenance.Human(ctx.PrincipalId, _clock.UtcNow, ctx.CorrelationId, "evidence_remove");
        return await _adrs.ApplyObservedUpdateAsync(ctx.TenantId, workspaceId, adrId,
            o => o with { Evidence = o.Evidence.Where(e => e.EvidenceId != evidenceId).ToList() }, provenance, $"Evidence {evidenceId} detached: {reason}", ct);
    }
}
