using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Findings;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed record DriftScanResult(Adr Adr, IReadOnlyList<DriftFinding> Findings, string OverallStatus, int StatusChanges, int EvidenceFiles);

/// <summary>
/// Drift detection: parses the decision into claims, evaluates each with detector plugins, persists findings with history,
/// and updates the ADR's system-managed drift status. The decision text is never modified.
/// </summary>
public sealed class DriftService
{
    private readonly IEnumerable<IDriftDetector> _detectors;
    private readonly IClaimExtractor _claims;
    private readonly IEvidenceSetProvider _evidence;
    private readonly IFindingRepository _findings;
    private readonly IAdrRepository _adrRepo;
    private readonly AdrService _adrs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly AdrOptions _options;
    private readonly JobOptions _jobOptions;
    private readonly ILogger<DriftService> _logger;

    public DriftService(IEnumerable<IDriftDetector> detectors, IClaimExtractor claims, IEvidenceSetProvider evidence, IFindingRepository findings, IAdrRepository adrRepo, AdrService adrs, IAuthorizationService authz, IClock clock, IOptions<AdrOptions> options, IOptions<JobOptions> jobOptions, ILogger<DriftService> logger)
    {
        _detectors = detectors; _claims = claims; _evidence = evidence; _findings = findings; _adrRepo = adrRepo; _adrs = adrs; _authz = authz; _clock = clock; _options = options.Value; _jobOptions = jobOptions.Value; _logger = logger;
    }

    public IReadOnlyList<string> DetectorNames => _detectors.Select(d => $"{d.Name}@{d.Version}").ToList();

    public async Task<DriftScanResult> ScanAsync(TenantContext? ctx, string tenantId, string workspaceId, string adrId, string correlationId, CancellationToken ct)
    {
        if (ctx is not null) _authz.Require(ctx, Permission.AnalysisRun);
        var adr = await LoadAsync(tenantId, workspaceId, adrId, ct);
        var now = _clock.UtcNow;
        var set = await _evidence.LoadForWorkspaceAsync(tenantId, workspaceId, null, _jobOptions.MaxEvidenceFilesPerScan, _jobOptions.MaxBytesPerEvidenceFile, ct);
        var claims = CollectClaims(adr);
        var findings = new List<DriftFinding>();

        foreach (var claim in claims)
        {
            var detectors = _detectors.Where(d => d.Supports(claim)).ToList();
            DriftEvaluation eval;
            string detectorName = "none", detectorVersion = "-";
            if (detectors.Count == 0)
                eval = new DriftEvaluation(DriftClaimResult.Unsupported, Confidence.Uncertain("No detector supports this claim kind yet."), $"No drift detector supports claim kind {claim.Kind}.", Array.Empty<EvidenceItem>());
            else if (set.Files.Count == 0)
                eval = new DriftEvaluation(DriftClaimResult.Uncertain, Confidence.Uncertain("No evidence files in workspace."), "No approved evidence is available in this workspace; upload code or infrastructure files.", Array.Empty<EvidenceItem>());
            else
            {
                // Most specific detector first; if several apply, take the most severe result.
                var evals = detectors.Select(d => (d, e: Safe(d, claim, set))).ToList();
                var pick = evals.OrderByDescending(x => Rank(x.e.Result)).ThenByDescending(x => x.e.Confidence.Score).First();
                eval = pick.e; detectorName = pick.d.Name; detectorVersion = pick.d.Version;
            }
            if (!claim.Declared) eval = eval with { Confidence = Confidence.Inferred(eval.Confidence.Score, "Claim was extracted from decision text, not declared.") };

            var fingerprint = $"drift:{adrId}:{claim.ClaimId}";
            var previous = await _findings.GetLatestByFingerprintAsync(tenantId, workspaceId, fingerprint, ct) as DriftFinding;
            var finding = new DriftFinding
            {
                Id = Ids.New("drf"),
                TenantId = tenantId, WorkspaceId = workspaceId, AdrId = adrId,
                Kind = FindingKind.Drift,
                Severity = Severity(eval.Result, claim),
                Confidence = eval.Confidence,
                Summary = $"{eval.Result}: {ClaimText(claim)}",
                Explanation = eval.Explanation,
                Evidence = eval.Evidence.Take(10).ToList(),
                Provenance = Provenance.System(detectorName, detectorVersion, now, correlationId, $"{set.Files.Count} evidence files"),
                FingerprintKey = fingerprint,
                PreviousFindingId = previous?.Id,
                RequiresHumanReview = eval.Result is DriftClaimResult.Drifted or DriftClaimResult.Uncertain,
                ClaimId = claim.ClaimId,
                ClaimText = ClaimText(claim),
                Result = eval.Result,
                PreviousResult = previous?.Result,
                DetectorName = detectorName,
                DetectorVersion = detectorVersion,
                // Human classification carries over while the result is unchanged.
                ReviewStatus = previous is not null && previous.Result == eval.Result ? previous.ReviewStatus : FindingReviewStatus.Open,
                ReviewedBy = previous is not null && previous.Result == eval.Result ? previous.ReviewedBy : null,
                ReviewNote = previous is not null && previous.Result == eval.Result ? previous.ReviewNote : null,
            };
            await _findings.UpsertAsync(finding, ct);
            findings.Add(finding);
            if (finding.StatusChanged) _logger.LogWarning("Drift status changed for {AdrId} claim {ClaimId}: {Prev} -> {Now}", adrId, claim.ClaimId, previous!.Result, eval.Result);
        }

        var overall = findings.Count == 0 ? "NoClaims"
            : findings.Any(f => f.Result == DriftClaimResult.Drifted) ? "Drifted"
            : findings.Any(f => f.Result == DriftClaimResult.Uncertain) ? "Uncertain"
            : findings.All(f => f.Result is DriftClaimResult.Unsupported or DriftClaimResult.NotApplicable) ? "Unsupported" : "Compliant";

        var provenance = Provenance.System("drift_scan", _options.AnalyzerVersion, now, correlationId);
        var updated = await _adrs.ApplyObservedUpdateAsync(tenantId, workspaceId, adrId,
            o => o with { DriftStatus = overall, DriftFindingIds = findings.Where(f => f.Result != DriftClaimResult.Compliant).Select(f => f.Id).ToList() },
            provenance, $"Drift scan: {overall} ({findings.Count} claims)", ct);
        return new DriftScanResult(updated, findings, overall, findings.Count(f => f.StatusChanged), set.Files.Count);
    }

    public IReadOnlyList<DecisionClaim> CollectClaims(Adr adr)
    {
        var declared = adr.Human.Claims.ToList();
        var extracted = _claims.Extract(adr).Where(e => !declared.Any(d => d.Kind == e.Kind && d.NormalizedSubject == e.NormalizedSubject)).ToList();
        return declared.Concat(extracted).ToList();
    }

    public static string ClaimText(DecisionClaim c) => c.Kind switch
    {
        ClaimKind.ProhibitedTechnology => $"must not use {c.Subject}",
        ClaimKind.RequiredTechnology => $"must use {c.Subject}",
        ClaimKind.InfrastructureRequirement => $"infrastructure {c.Subject} {c.Predicate} {c.Object}".Trim(),
        ClaimKind.DatabaseOwnership => $"{c.Subject} exclusively owns database {c.Object}",
        _ => $"{c.Subject} {c.Predicate} {c.Object}".Trim()
    };

    private async Task<Adr> LoadAsync(string tenantId, string workspaceId, string adrId, CancellationToken ct)
    {
        // Analysis may run without a TenantContext (queue worker); the repository enforces the partition.
        var adr = await _adrRepo.GetAsync(tenantId, workspaceId, adrId, ct);
        return adr is null || adr.IsDeleted ? throw new NotFoundException(ErrorCodes.AdrNotFound, $"ADR '{adrId}' not found.") : adr;
    }

    private DriftEvaluation Safe(IDriftDetector d, DecisionClaim claim, EvidenceSet set)
    {
        try { return d.Evaluate(claim, set); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drift detector {Detector} failed for claim {Claim}", d.Name, claim.ClaimId);
            return new DriftEvaluation(DriftClaimResult.Uncertain, Confidence.Uncertain("Detector error"), $"Detector {d.Name} failed: {SensitiveDataRedactor.Redact(ex.Message)}", Array.Empty<EvidenceItem>());
        }
    }

    private static int Rank(DriftClaimResult r) => r switch { DriftClaimResult.Drifted => 4, DriftClaimResult.Uncertain => 3, DriftClaimResult.Compliant => 2, DriftClaimResult.NotApplicable => 1, _ => 0 };

    private static FindingSeverity Severity(DriftClaimResult r, DecisionClaim c) => r switch
    {
        DriftClaimResult.Drifted => c.Kind is ClaimKind.ProhibitedTechnology or ClaimKind.DatabaseOwnership or ClaimKind.AuthStrategy or ClaimKind.DataResidency ? FindingSeverity.High : FindingSeverity.Medium,
        DriftClaimResult.Uncertain => FindingSeverity.Low,
        _ => FindingSeverity.Info
    };
}
