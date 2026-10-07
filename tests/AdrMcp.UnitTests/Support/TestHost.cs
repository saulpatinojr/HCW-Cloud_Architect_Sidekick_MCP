using System.Text.Json;
using AdrMcp.Analysis;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Services;
using AdrMcp.Application.Tools;
using AdrMcp.Domain.Tenancy;
using AdrMcp.Functions.Jobs;
using AdrMcp.Infrastructure;
using AdrMcp.Infrastructure.InMemory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdrMcp.UnitTests.Support;

/// <summary>Full application composition over in-memory persistence. Mirrors Program.cs minus the Functions host.</summary>
public sealed class TestHost
{
    public ServiceProvider Services { get; }
    public ToolInvocationPipeline Pipeline => Services.GetRequiredService<ToolInvocationPipeline>();
    public ToolHandlers Handlers => Services.GetRequiredService<ToolHandlers>();
    public InMemoryEvidenceBlobStore Blobs => Services.GetRequiredService<InMemoryEvidenceBlobStore>();
    public InMemoryJobQueue Queue => Services.GetRequiredService<InMemoryJobQueue>();
    public InMemoryAuditRepository Audit => (InMemoryAuditRepository)Services.GetRequiredService<IAuditRepository>();
    public InMemoryMemoryRepository Memory => (InMemoryMemoryRepository)Services.GetRequiredService<IMemoryRepository>();
    public InMemoryFindingRepository Findings => (InMemoryFindingRepository)Services.GetRequiredService<IFindingRepository>();
    public InMemoryDocumentStore Store => Services.GetRequiredService<InMemoryDocumentStore>();

    public TestHost(Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "InMemory",
            ["Auth:Mode"] = "FunctionKey",
            ["Auth:StaticTenantId"] = "tenant-a",
            ["Auth:StaticWorkspaceId"] = "ws-1",
            ["Auth:StaticPrincipalId"] = "dev-user",
            ["Auth:StaticRoles:0"] = "Author",
            ["Auth:StaticRoles:1"] = "Approver",
            ["Auth:TenantWorkspaces:tenant-a:0"] = "ws-1",
            ["Auth:TenantWorkspaces:tenant-a:1"] = "ws-2",
            ["Adr:PrincipalHashSalt"] = "test-salt",
            ["Uploads:BlockOnCriticalSafetyFinding"] = "true",
        };
        configure?.Invoke(settings);
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Warning));
        services.AddAdrApplication(config);
        services.AddAdrPersistence(config);
        services.AddAdrAnalysis(config);
        services.AddSingleton<IJobHandler, EvidenceProcessingJobHandler>();
        services.AddSingleton<IJobHandler, TechnologyRefreshJobHandler>();
        services.AddSingleton<IJobHandler, DriftScanJobHandler>();
        services.AddSingleton<IJobHandler, ContradictionScanJobHandler>();
        Services = services.BuildServiceProvider();
    }

    public static TenantContext Tenant(string tenantId = "tenant-a", string workspace = "ws-1", string principal = "dev-user", params Role[] roles)
        => new(tenantId, principal, principal, (roles.Length == 0 ? new[] { Role.Author, Role.Approver } : roles).ToHashSet(), new HashSet<string> { workspace, "ws-2" }, workspace, "Test", Guid.NewGuid().ToString("N"));

    /// <summary>Invokes a tool through the full pipeline the way the Functions adapter does, returning the parsed envelope.</summary>
    public async Task<JsonElement> CallAsync(string tool, object args, IReadOnlyDictionary<string, string>? headers = null)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args, AppJson.Options), AppJson.Options)!;
        var json = await Dispatch(tool, dict, headers ?? new Dictionary<string, string>());
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private Task<string> Dispatch(string tool, IReadOnlyDictionary<string, object?> args, IReadOnlyDictionary<string, string> headers) => tool switch
    {
        "adr_create" => Pipeline.ExecuteAsync<AdrCreateRequest>(tool, args, headers, null, Handlers.AdrCreate, default),
        "adr_get" => Pipeline.ExecuteAsync<AdrGetRequest>(tool, args, headers, null, Handlers.AdrGet, default),
        "adr_list" => Pipeline.ExecuteAsync<AdrListRequest>(tool, args, headers, null, Handlers.AdrList, default),
        "adr_search" => Pipeline.ExecuteAsync<AdrSearchRequest>(tool, args, headers, null, Handlers.AdrSearch, default),
        "adr_update" => Pipeline.ExecuteAsync<AdrUpdateRequest>(tool, args, headers, null, Handlers.AdrUpdate, default),
        "adr_propose_change" => Pipeline.ExecuteAsync<AdrProposeChangeRequest>(tool, args, headers, null, Handlers.AdrProposeChange, default),
        "adr_approve_change" => Pipeline.ExecuteAsync<AdrApproveChangeRequest>(tool, args, headers, null, Handlers.AdrApproveChange, default),
        "adr_reject_change" => Pipeline.ExecuteAsync<AdrRejectChangeRequest>(tool, args, headers, null, Handlers.AdrRejectChange, default),
        "adr_supersede" => Pipeline.ExecuteAsync<AdrSupersedeRequest>(tool, args, headers, null, Handlers.AdrSupersede, default),
        "adr_archive" => Pipeline.ExecuteAsync<AdrArchiveRequest>(tool, args, headers, null, Handlers.AdrArchive, default),
        "adr_history" => Pipeline.ExecuteAsync<AdrHistoryRequest>(tool, args, headers, null, Handlers.AdrHistory, default),
        "adr_export" => Pipeline.ExecuteAsync<AdrExportRequest>(tool, args, headers, null, Handlers.AdrExport, default),
        "file_upload_request" => Pipeline.ExecuteAsync<FileUploadRequestRequest>(tool, args, headers, null, Handlers.FileUploadRequest, default),
        "file_register_upload" => Pipeline.ExecuteAsync<FileRegisterUploadRequest>(tool, args, headers, null, Handlers.FileRegisterUpload, default),
        "evidence_attach" => Pipeline.ExecuteAsync<EvidenceAttachRequest>(tool, args, headers, null, Handlers.EvidenceAttach, default),
        "evidence_list" => Pipeline.ExecuteAsync<EvidenceListRequest>(tool, args, headers, null, Handlers.EvidenceList, default),
        "evidence_remove" => Pipeline.ExecuteAsync<EvidenceRemoveRequest>(tool, args, headers, null, Handlers.EvidenceRemove, default),
        "technology_detect" => Pipeline.ExecuteAsync<TechnologyDetectRequest>(tool, args, headers, null, Handlers.TechnologyDetect, default),
        "technology_refresh" => Pipeline.ExecuteAsync<TechnologyRefreshRequest>(tool, args, headers, null, Handlers.TechnologyRefresh, default),
        "adr_detect_drift" => Pipeline.ExecuteAsync<AdrDetectDriftRequest>(tool, args, headers, null, Handlers.AdrDetectDrift, default),
        "adr_check_contradictions" => Pipeline.ExecuteAsync<AdrCheckContradictionsRequest>(tool, args, headers, null, Handlers.AdrCheckContradictions, default),
        "adr_safety_review" => Pipeline.ExecuteAsync<AdrSafetyReviewRequest>(tool, args, headers, null, Handlers.AdrSafetyReview, default),
        "finding_review" => Pipeline.ExecuteAsync<FindingReviewRequest>(tool, args, headers, null, Handlers.FindingReview, default),
        "analysis_job_get" => Pipeline.ExecuteAsync<AnalysisJobGetRequest>(tool, args, headers, null, Handlers.AnalysisJobGet, default),
        "analysis_job_cancel" => Pipeline.ExecuteAsync<AnalysisJobCancelRequest>(tool, args, headers, null, Handlers.AnalysisJobCancel, default),
        "analysis_job_results" => Pipeline.ExecuteAsync<AnalysisJobResultsRequest>(tool, args, headers, null, Handlers.AnalysisJobResults, default),
        "decision_memory_search" => Pipeline.ExecuteAsync<DecisionMemorySearchRequest>(tool, args, headers, null, Handlers.DecisionMemorySearch, default),
        _ => throw new ArgumentException($"Unknown tool {tool}")
    };

    public async Task<string> CreateAdrAsync(string title, string decision, string status = "Draft", object? claims = null, string? workspaceId = null, string? context = null, string? rationale = null, string[]? alternatives = null, string? consequences = null)
    {
        var r = await CallAsync("adr_create", new { workspaceId, fields = new { title, decision, status, claims, context = context ?? "Context for " + title, rationale = rationale ?? "Because it is the best trade-off given the constraints we documented in detail.", alternatives = alternatives ?? new[] { "Alternative A", "Alternative B" }, consequences = consequences ?? "Consequences. Rollback: revert the configuration. Monitoring via Application Insights alerts." } });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        return r.GetProperty("data").GetProperty("adr").GetProperty("summary").GetProperty("id").GetString()!;
    }

    public async Task<string> AcceptAsync(string adrId)
    {
        var p = await CallAsync("adr_update", new { adrId, fields = new { status = "Proposed" } });
        Assert.True(p.GetProperty("ok").GetBoolean(), p.ToString());
        var u = await CallAsync("adr_update", new { adrId, fields = new { status = "UnderReview" } });
        Assert.True(u.GetProperty("ok").GetBoolean(), u.ToString());
        var a = await CallAsync("adr_update", new { adrId, fields = new { status = "Accepted" } });
        Assert.True(a.GetProperty("ok").GetBoolean(), a.ToString());
        return a.GetProperty("data").GetProperty("adr").GetProperty("summary").GetProperty("etag").GetString()!;
    }

    /// <summary>Simulates the client PUT + register for a text file.</summary>
    public async Task<string> UploadAsync(string fileName, string content, string? adrId = null, string mediaType = "text/plain")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var req = await CallAsync("file_upload_request", new { fileName, mediaType, expectedSizeBytes = bytes.Length, adrId });
        Assert.True(req.GetProperty("ok").GetBoolean(), req.ToString());
        var uploadId = req.GetProperty("data").GetProperty("uploadId").GetString()!;
        var url = new Uri(req.GetProperty("data").GetProperty("uploadUrl").GetString()!);
        var path = url.AbsolutePath["/quarantine/".Length..];
        Blobs.Put(EvidenceContainer.Quarantine, path, bytes, mediaType);
        var reg = await CallAsync("file_register_upload", new { uploadId });
        Assert.True(reg.GetProperty("ok").GetBoolean(), reg.ToString());
        return reg.GetProperty("data").GetProperty("evidenceId").GetString()!;
    }

    public async Task DrainJobsAsync()
    {
        var jobs = Services.GetRequiredService<AnalysisJobService>();
        while (Queue.Messages.TryDequeue(out var m))
        {
            var parts = m.PartitionKey.Split('|');
            await jobs.ProcessAsync(parts[0], parts[1], m.JobId, 1, default);
        }
    }
}
