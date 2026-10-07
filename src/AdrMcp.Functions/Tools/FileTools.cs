using AdrMcp.Application.Tools;
using AdrMcp.Functions.Mcp;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace AdrMcp.Functions.Tools;

public sealed class FileTools
{
    private readonly McpToolAdapter _mcp;
    private readonly ToolHandlers _h;
    public FileTools(McpToolAdapter mcp, ToolHandlers h) { _mcp = mcp; _h = h; }

    [Function("file_upload_request")]
    public Task<string> UploadRequest([McpToolTrigger("file_upload_request", "Start a fast direct-to-Blob upload: validates name/type/size/policy and returns a short-lived, write-only, single-object upload URL. Follow with file_register_upload.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<FileUploadRequestRequest>(context, _h.FileUploadRequest, ct);

    [Function("file_register_upload")]
    public Task<string> RegisterUpload([McpToolTrigger("file_register_upload", "Finish an upload: verify size/SHA-256/MIME, quarantine, scan for secrets/PII/prompt-injection, sanitize, promote to immutable evidence, attach to ADR, queue analysis. Safe to repeat.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<FileRegisterUploadRequest>(context, _h.FileRegisterUpload, ct);

    [Function("evidence_attach")]
    public Task<string> Attach([McpToolTrigger("evidence_attach", "Attach approved evidence to an ADR with provenance.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<EvidenceAttachRequest>(context, _h.EvidenceAttach, ct);

    [Function("evidence_list")]
    public Task<string> List([McpToolTrigger("evidence_list", "List approved evidence metadata in the workspace or for one ADR. Never returns file contents.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<EvidenceListRequest>(context, _h.EvidenceList, ct);

    [Function("evidence_remove")]
    public Task<string> Remove([McpToolTrigger("evidence_remove", "Detach evidence from an ADR with a reason (Approver). The immutable blob is retained for audit.")] ToolInvocationContext context, CancellationToken ct)
        => _mcp.RunAsync<EvidenceRemoveRequest>(context, _h.EvidenceRemove, ct);
}
