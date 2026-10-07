using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Findings;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed record SafetyReviewResult(IReadOnlyList<SafetyFinding> Findings, bool HumanReviewRequired, bool PolicyBlocksAcceptance, IReadOnlyList<string> RulesEvaluated);

/// <summary>Advisory review of a proposed/accepted ADR. Hard blocks come only from IPolicyProvider (organization policy).</summary>
public sealed class SafetyReviewService
{
    private readonly IEnumerable<ISafetyReviewRule> _rules;
    private readonly IPolicyProvider _policy;
    private readonly IAdrRepository _adrRepo;
    private readonly IFindingRepository _findings;
    private readonly AdrService _adrs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly AdrOptions _options;

    public SafetyReviewService(IEnumerable<ISafetyReviewRule> rules, IPolicyProvider policy, IAdrRepository adrRepo, IFindingRepository findings, AdrService adrs, IAuthorizationService authz, IClock clock, IOptions<AdrOptions> options)
    {
        _rules = rules; _policy = policy; _adrRepo = adrRepo; _findings = findings; _adrs = adrs; _authz = authz; _clock = clock; _options = options.Value;
    }

    public async Task<SafetyReviewResult> ReviewAsync(TenantContext ctx, string workspaceId, string? adrId, HumanOwnedContent? inlineContent, bool persist, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AnalysisRun);
        var now = _clock.UtcNow;
        Adr adr;
        if (adrId is not null) adr = await _adrs.GetAsync(ctx, workspaceId, adrId, ct);
        else if (inlineContent is not null)
            adr = new Adr { Id = "inline", TenantId = ctx.TenantId, WorkspaceId = workspaceId, AdrNumber = 0, Human = inlineContent, CreatedAt = now, UpdatedAt = now, Provenance = Provenance.Human(ctx.PrincipalId, now, ctx.CorrelationId) };
        else throw new Domain.Errors.ValidationException("Provide adrId or inline ADR content.");

        var others = (await _adrRepo.ListByStatusesAsync(ctx.TenantId, workspaceId, AdrLifecycle.ActiveStatuses.ToList(), ct)).Where(a => a.Id != adr.Id).ToList();
        var input = new SafetyReviewInput(adr, others, null, now);
        var results = new List<SafetyFinding>();
        var evaluated = new List<string>();
        foreach (var rule in _rules)
        {
            evaluated.Add(rule.RuleId);
            SafetyFinding Create(string summary) => new()
            {
                Id = Ids.New("saf"),
                TenantId = ctx.TenantId, WorkspaceId = workspaceId, AdrId = adr.Id,
                Kind = FindingKind.Safety,
                Severity = FindingSeverity.Medium,
                Confidence = Confidence.Inferred(0.5, "Heuristic rule"),
                Summary = summary,
                Provenance = Provenance.System(rule.RuleId, _options.AnalyzerVersion, now, ctx.CorrelationId),
                FingerprintKey = $"safety:{adr.Id}:{rule.RuleId}:{Hashing.Sha256Hex(summary)[..8]}",
                Category = rule.Category,
                PolicyBlocksAcceptance = _policy.BlocksAcceptance(rule.RuleId),
                PolicyId = _policy.PolicyIdFor(rule.RuleId)
            };
            foreach (var f in rule.Evaluate(input, Create))
            {
                var finding = f with { PolicyBlocksAcceptance = _policy.BlocksAcceptance(rule.RuleId), PolicyId = _policy.PolicyIdFor(rule.RuleId) };
                results.Add(finding);
            }
        }

        if (persist && adrId is not null)
        {
            foreach (var f in results) await _findings.UpsertAsync(f, ct);
            await _adrs.ApplyObservedUpdateAsync(ctx.TenantId, workspaceId, adrId, o => o with { SafetyFindingIds = results.Select(f => f.Id).ToList() },
                Provenance.System("safety_review", _options.AnalyzerVersion, now, ctx.CorrelationId), $"Safety review: {results.Count} findings", ct);
        }
        var ordered = results.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence.Score).ToList();
        return new SafetyReviewResult(ordered, ordered.Any(f => f.Severity >= FindingSeverity.Medium), ordered.Any(f => f.PolicyBlocksAcceptance), evaluated);
    }
}
