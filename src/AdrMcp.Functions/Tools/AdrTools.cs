using AdrMcp.Application.Tools;
using AdrMcp.Functions.Mcp;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace AdrMcp.Functions.Tools;

/// <summary>ADR management tools. Each function binds the trigger to ToolInvocationContext and delegates to the pipeline.</summary>
public sealed class AdrTools
{
    private readonly McpToolAdapter _mcp;
    private readonly ToolHandlers _h;
    public AdrTools(McpToolAdapter mcp, ToolHandlers h) { _mcp = mcp; _h = h; }

    [Function("adr_create")]
    public Task<string> Create([McpToolTrigger("adr_create", "Create a new Architectural Decision Record (Draft/Proposed) from Markdown and/or structured fields. Human-owned content is stored verbatim.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrCreateRequest>(context, _h.AdrCreate, ct);

    [Function("adr_get")]
    public Task<string> Get([McpToolTrigger("adr_get", "Get one ADR with human-owned content, claims and compact system-managed observations; optional Markdown.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrGetRequest>(context, _h.AdrGet, ct);

    [Function("adr_list")]
    public Task<string> List([McpToolTrigger("adr_list", "List ADRs in a workspace with status/tag/owner filters; compact summaries with continuation token.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrListRequest>(context, _h.AdrList, ct);

    [Function("adr_search")]
    public Task<string> Search([McpToolTrigger("adr_search", "Deterministic keyword search over ADR title, tags, context, decision and consequences; ranked summaries.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrSearchRequest>(context, _h.AdrSearch, ct);

    [Function("adr_update")]
    public Task<string> Update([McpToolTrigger("adr_update", "Human edit of human-owned fields with ETag concurrency. Accepted decision content is immutable; status/supersession need Approver.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrUpdateRequest>(context, _h.AdrUpdate, ct);

    [Function("adr_propose_change")]
    public Task<string> Propose([McpToolTrigger("adr_propose_change", "Propose changes to human-owned ADR fields. Nothing is applied until a human approves with adr_approve_change. Use for all assistant/analyzer suggestions.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrProposeChangeRequest>(context, _h.AdrProposeChange, ct);

    [Function("adr_approve_change")]
    public Task<string> Approve([McpToolTrigger("adr_approve_change", "Approve and apply a pending proposal (Approver). Four-eyes, stale-field protection, lifecycle and ETag checks; full provenance.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrApproveChangeRequest>(context, _h.AdrApproveChange, ct);

    [Function("adr_reject_change")]
    public Task<string> Reject([McpToolTrigger("adr_reject_change", "Reject a pending proposal with a note (Approver).")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrRejectChangeRequest>(context, _h.AdrRejectChange, ct);

    [Function("adr_supersede")]
    public Task<string> Supersede([McpToolTrigger("adr_supersede", "Mark an ADR Superseded by an Accepted ADR (human choice, Approver). Links both ADRs.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrSupersedeRequest>(context, _h.AdrSupersede, ct);

    [Function("adr_archive")]
    public Task<string> Archive([McpToolTrigger("adr_archive", "Archive an ADR with a reason (Approver). Archived ADRs are read-only and excluded from analysis.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrArchiveRequest>(context, _h.AdrArchive, ct);

    [Function("adr_history")]
    public Task<string> History([McpToolTrigger("adr_history", "List version snapshots (human and observed revisions) with provenance and content hashes.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrHistoryRequest>(context, _h.AdrHistory, ct);

    [Function("adr_export")]
    public Task<string> Export([McpToolTrigger("adr_export", "Export an ADR as Markdown or JSON.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AdrExportRequest>(context, _h.AdrExport, ct);
}
