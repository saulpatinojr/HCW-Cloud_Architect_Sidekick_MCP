using AdrMcp.Application.Tools;
using AdrMcp.Functions.Mcp;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace AdrMcp.Functions.Tools;

public sealed class AnalysisTools
{
    private readonly McpToolAdapter _mcp;
    private readonly ToolHandlers _h;
    public AnalysisTools(McpToolAdapter mcp, ToolHandlers h) { _mcp = mcp; _h = h; }

    [Function("technology_detect")]
    public Task<string> Detect([McpToolTrigger("technology_detect", "Deterministically detect technologies/versions from inline files or workspace evidence. Nothing is persisted; results carry detector, confidence and evidence.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<TechnologyDetectRequest>(context, _h.TechnologyDetect, ct);

    [Function("technology_refresh")]
    public Task<string> Refresh([McpToolTrigger("technology_refresh", "Refresh the ADR's system-managed observed technologies from evidence and run drift detection. Never edits the decision. async=true returns a job.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<TechnologyRefreshRequest>(context, _h.TechnologyRefresh, ct);

    [Function("adr_detect_drift")]
    public Task<string> Drift([McpToolTrigger("adr_detect_drift", "Evaluate an ADR's claims against workspace evidence with detector plugins: Compliant/Drifted/Uncertain/Unsupported/NotApplicable per claim, with evidence, confidence and history. async=true returns a job.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrDetectDriftRequest>(context, _h.AdrDetectDrift, ct);

    [Function("adr_check_contradictions")]
    public Task<string> Contradictions([McpToolTrigger("adr_check_contradictions", "Hybrid contradiction check between ADRs: deterministic claim matching with scope/date/status filtering, explained findings with confidence and severity. Humans classify with finding_review.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrCheckContradictionsRequest>(context, _h.AdrCheckContradictions, ct);

    [Function("adr_safety_review")]
    public Task<string> Safety([McpToolTrigger("adr_safety_review", "Advisory safety review of an ADR (stored or inline): security, availability, data loss, privacy, compliance, lock-in, cost, EOL, scalability, operability, rollback/migration, evidence, conflicts, irreversibility, blast radius, responsible AI. Blocks only by organization policy.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrSafetyReviewRequest>(context, _h.AdrSafetyReview, ct);

    [Function("finding_review")]
    public Task<string> Review([McpToolTrigger("finding_review", "Human classification of a finding: Confirmed, AcceptableException, FalsePositive or Resolved.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<FindingReviewRequest>(context, _h.FindingReview, ct);
}
