using AdrMcp.Application.Tools;
using AdrMcp.Functions.Mcp;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace AdrMcp.Functions.Tools;

public sealed class JobAndMemoryTools
{
    private readonly McpToolAdapter _mcp;
    private readonly ToolHandlers _h;
    public JobAndMemoryTools(McpToolAdapter mcp, ToolHandlers h) { _mcp = mcp; _h = h; }

    [Function("analysis_job_get")]
    public Task<string> JobGet([McpToolTrigger("analysis_job_get", "Get the state and progress of an asynchronous analysis job.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AnalysisJobGetRequest>(context, _h.AnalysisJobGet, ct);

    [Function("analysis_job_cancel")]
    public Task<string> JobCancel([McpToolTrigger("analysis_job_cancel", "Request cancellation of a queued or running job.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AnalysisJobCancelRequest>(context, _h.AnalysisJobCancel, ct);

    [Function("analysis_job_results")]
    public Task<string> JobResults([McpToolTrigger("analysis_job_results", "Fetch the compact result of a completed job.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<AnalysisJobResultsRequest>(context, _h.AnalysisJobResults, ct);

    [Function("decision_memory_search")]
    public Task<string> MemorySearch([McpToolTrigger("decision_memory_search", "Search tenant-isolated decision memory (prior tool calls, ADR references, findings, feedback) with explainable keyword scoring.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<DecisionMemorySearchRequest>(context, _h.DecisionMemorySearch, ct);
}
