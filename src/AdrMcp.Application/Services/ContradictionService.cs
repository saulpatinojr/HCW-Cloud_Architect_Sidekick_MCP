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

public sealed record ContradictionCheckResult(IReadOnlyList<ContradictionFinding> Findings, int AdrsCompared, int CandidatesEvaluated, bool SemanticAnalyzerUsed);

/// <summary>
/// Hybrid contradiction detection: normalize → deterministic candidates → scope/date/status filter → optional semantic pass →
/// explained finding with confidence/severity. Never changes ADR status; humans classify results.
/// </summary>
public sealed class ContradictionService
{
    private readonly IEnumerable<IContradictionRule> _rules;
    private readonly IClaimExtractor _claims;
    private readonly ISemanticContradictionAnalyzer _semantic;
    private readonly IAdrRepository _adrRepo;
    private readonly IFindingRepository _findings;
    private readonly AdrService _adrs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly AdrOptions _options;
    private readonly ILogger<ContradictionService> _logger;

    public ContradictionService(IEnumerable<IContradictionRule> rules, IClaimExtractor claims, ISemanticContradictionAnalyzer semantic, IAdrRepository adrRepo, IFindingRepository findings, AdrService adrs, IAuthorizationService authz, IClock clock, IOptions<AdrOptions> options, ILogger<ContradictionService> logger)
    {
        _rules = rules; _claims = claims; _semantic = semantic; _adrRepo = adrRepo; _findings = findings; _adrs = adrs; _authz = authz; _clock = clock; _options = options.Value; _logger = logger;
    }

    public async Task<ContradictionCheckResult> CheckAsync(TenantContext? ctx, string tenantId, string workspaceId, string? adrId, bool includeDrafts, string correlationId, bool persist, CancellationToken ct)
    {
        if (ctx is not null) _authz.Require(ctx, Permission.AnalysisRun);
        var statuses = new List<AdrStatus>(AdrLifecycle.ActiveStatuses);
        if (includeDrafts) statuses.AddRange(new[] { AdrStatus.Draft, AdrStatus.Proposed, AdrStatus.UnderReview });
        var all = await _adrRepo.ListByStatusesAsync(tenantId, workspaceId, statuses, ct);
        var targets = adrId is null ? all : all.Where(a => a.Id == adrId).ToList();
        if (adrId is not null && targets.Count == 0)
        {
            var single = await _adrRepo.GetAsync(tenantId, workspaceId, adrId, ct) ?? throw new NotFoundException(ErrorCodes.AdrNotFound, $"ADR '{adrId}' not found.");
            targets = new List<Adr> { single };
            if (!all.Any(a => a.Id == single.Id)) all = all.Append(single).ToList();
        }

        var claimsByAdr = all.ToDictionary(a => a.Id, a => CollectClaims(a));
        var candidates = new List<ContradictionCandidate>();
        var seenPairs = new HashSet<string>();
        foreach (var a in targets)
        {
            foreach (var b in all)
            {
                if (a.Id == b.Id) continue;
                var pairKey = string.CompareOrdinal(a.Id, b.Id) < 0 ? $"{a.Id}|{b.Id}" : $"{b.Id}|{a.Id}";
                if (!seenPairs.Add(pairKey)) continue;
                // An ADR and the ADR it supersedes are expected to disagree: skip the predecessor relationship.
                if (a.Human.Supersedes == b.Id || b.Human.Supersedes == a.Id || a.Human.SupersededBy == b.Id || b.Human.SupersededBy == a.Id) continue;
                foreach (var rule in _rules)
                    candidates.AddRange(rule.Match(a, claimsByAdr[a.Id], b, claimsByAdr[b.Id]));
            }
        }

        // One logical contradiction → one finding: collapse candidates that differ only by which (declared vs extracted) claim matched.
        candidates = candidates
            .GroupBy(c => (Pair: string.CompareOrdinal(c.A.Id, c.B.Id) < 0 ? $"{c.A.Id}|{c.B.Id}" : $"{c.B.Id}|{c.A.Id}", c.ContradictionType, Key: DecisionClaim.Normalize(c.ClaimA.Object ?? c.ClaimA.Subject)))
            .Select(g => g.OrderByDescending(c => c.ClaimA.Declared && c.ClaimB.Declared).ThenByDescending(c => c.DeterministicScore).First())
            .ToList();

        var now = _clock.UtcNow;
        var findings = new List<ContradictionFinding>();
        foreach (var c in candidates)
        {
            double score = c.DeterministicScore;
            string explanation = c.DeterministicExplanation;
            bool needsSemantic = score < 0.85;
            if (needsSemantic && _semantic.IsEnabled)
            {
                var verdict = await _semantic.AnalyzeAsync(c, ct);
                if (verdict is { } v)
                {
                    if (!v.IsContradiction) { continue; }
                    score = Math.Min(Confidence.InferredCeiling, v.Confidence); explanation += $" Semantic analysis (inferred): {v.Explanation}"; needsSemantic = false;
                }
            }
            var basis = (c.ClaimA.Declared && c.ClaimB.Declared) ? Confidence.Observed(score, "Both claims declared by humans; deterministic rule match.") : Confidence.Inferred(score, "At least one claim was extracted from text.");
            var fingerprint = $"contra:{Fp(c)}";
            var previous = await _findings.GetLatestByFingerprintAsync(tenantId, workspaceId, fingerprint, ct) as ContradictionFinding;
            var finding = new ContradictionFinding
            {
                Id = Ids.New("ctr"),
                TenantId = tenantId, WorkspaceId = workspaceId, AdrId = c.A.Id, OtherAdrId = c.B.Id,
                Kind = FindingKind.Contradiction,
                Severity = Severity(c, basis),
                Confidence = basis,
                Summary = $"{c.ContradictionType}: {c.A.Id} vs {c.B.Id}",
                Explanation = explanation,
                Evidence = new[]
                {
                    new EvidenceItem(c.A.Id, $"adr/{c.A.Id}", Trunc(c.ClaimA.SourceText ?? DriftService.ClaimText(c.ClaimA)), c.A.ContentHash, Kind: "claim"),
                    new EvidenceItem(c.B.Id, $"adr/{c.B.Id}", Trunc(c.ClaimB.SourceText ?? DriftService.ClaimText(c.ClaimB)), c.B.ContentHash, Kind: "claim")
                },
                Provenance = Provenance.System("contradiction_check", _options.AnalyzerVersion, now, correlationId),
                FingerprintKey = fingerprint,
                PreviousFindingId = previous?.Id,
                ClaimA = DriftService.ClaimText(c.ClaimA),
                ClaimB = DriftService.ClaimText(c.ClaimB),
                ContradictionType = c.ContradictionType,
                NeedsSemanticReview = needsSemantic,
                ReviewStatus = previous?.ReviewStatus ?? FindingReviewStatus.Open,
                ReviewedBy = previous?.ReviewedBy,
                ReviewNote = previous?.ReviewNote,
                RequiresHumanReview = true
            };
            // Human decisions stick: a confirmed false positive is not re-raised as Open.
            if (previous?.ReviewStatus is FindingReviewStatus.FalsePositive or FindingReviewStatus.AcceptableException) finding = finding with { RequiresHumanReview = false };
            if (persist) await _findings.UpsertAsync(finding, ct);
            findings.Add(finding);
        }

        if (persist)
        {
            foreach (var a in targets)
            {
                var ids = findings.Where(f => (f.AdrId == a.Id || f.OtherAdrId == a.Id) && f.ReviewStatus is FindingReviewStatus.Open or FindingReviewStatus.Confirmed).Select(f => f.Id).ToList();
                await _adrs.ApplyObservedUpdateAsync(tenantId, workspaceId, a.Id, o => o with { ContradictionFindingIds = ids }, Provenance.System("contradiction_check", _options.AnalyzerVersion, now, correlationId), $"Contradiction check: {ids.Count} open", ct);
            }
        }
        return new ContradictionCheckResult(findings, all.Count, candidates.Count, _semantic.IsEnabled);
    }

    public async Task<Finding> ReviewAsync(TenantContext ctx, string workspaceId, string findingId, FindingReviewStatus status, string? note, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.FindingReview);
        if (status == FindingReviewStatus.Open) throw new ValidationException("Use Confirmed, AcceptableException, FalsePositive or Resolved.");
        var finding = await _findings.GetAsync(ctx.TenantId, workspaceId, findingId, ct) ?? throw new NotFoundException(ErrorCodes.NotFound, $"Finding '{findingId}' not found.");
        Finding updated = finding switch
        {
            ContradictionFinding c => c with { ReviewStatus = status, ReviewedBy = ctx.PrincipalId, ReviewedAt = _clock.UtcNow, ReviewNote = note, RequiresHumanReview = false },
            DriftFinding d => d with { ReviewStatus = status, ReviewedBy = ctx.PrincipalId, ReviewedAt = _clock.UtcNow, ReviewNote = note, RequiresHumanReview = false },
            SafetyFinding s => s with { ReviewStatus = status, ReviewedBy = ctx.PrincipalId, ReviewedAt = _clock.UtcNow, ReviewNote = note, RequiresHumanReview = false },
            GovernanceFinding g => g with { ReviewStatus = status, ReviewedBy = ctx.PrincipalId, ReviewedAt = _clock.UtcNow, ReviewNote = note, RequiresHumanReview = false },
            _ => throw new InvalidOperationException("Unknown finding type")
        };
        await _findings.UpsertAsync(updated, ct);
        return updated;
    }

    private IReadOnlyList<DecisionClaim> CollectClaims(Adr adr)
    {
        var declared = adr.Human.Claims.ToList();
        var extracted = _claims.Extract(adr).Where(e => !declared.Any(d => d.Kind == e.Kind && d.NormalizedSubject == e.NormalizedSubject)).ToList();
        return declared.Concat(extracted).ToList();
    }

    private static string Fp(ContradictionCandidate c)
    {
        var ids = new[] { $"{c.A.Id}:{c.ClaimA.ClaimId}", $"{c.B.Id}:{c.ClaimB.ClaimId}" }.OrderBy(x => x, StringComparer.Ordinal);
        return Hashing.Sha256Hex($"{c.ContradictionType}|{string.Join("|", ids)}")[..24];
    }

    private static FindingSeverity Severity(ContradictionCandidate c, Confidence conf)
    {
        var bothActive = AdrLifecycle.ActiveStatuses.Contains(c.A.Status) && AdrLifecycle.ActiveStatuses.Contains(c.B.Status);
        if (bothActive && conf.Score >= 0.8) return FindingSeverity.High;
        if (bothActive) return FindingSeverity.Medium;
        return conf.Score >= 0.8 ? FindingSeverity.Medium : FindingSeverity.Low;
    }

    private static string Trunc(string s) => s.Length <= 240 ? s : s[..240] + "…";
}
