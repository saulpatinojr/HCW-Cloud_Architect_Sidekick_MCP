using System.Text.Json;
using AdrMcp.Application.Common;
using AdrMcp.Application.Services;
using AdrMcp.Domain.Jobs;

namespace AdrMcp.Functions.Jobs;

/// <summary>After an upload is approved: refresh technologies + drift for the linked ADR, then run a contradiction check.</summary>
public sealed class EvidenceProcessingJobHandler : IJobHandler
{
    private readonly TechnologyService _technology;
    private readonly DriftService _drift;
    private readonly ContradictionService _contradictions;
    public EvidenceProcessingJobHandler(TechnologyService technology, DriftService drift, ContradictionService contradictions) { _technology = technology; _drift = drift; _contradictions = contradictions; }
    public JobType Type => JobType.EvidenceProcessing;

    public async Task<JobOutcome> ExecuteAsync(AnalysisJob job, Func<int, string, Task> progress, CancellationToken ct)
    {
        var adrId = job.Parameters.GetValueOrDefault("adrId");
        object? tech = null, drift = null;
        if (!string.IsNullOrEmpty(adrId))
        {
            await progress(10, "Refreshing technologies");
            var t = await _technology.RefreshAsync(null, job.TenantId, job.WorkspaceId, adrId, job.CorrelationId, ct);
            tech = new { t.Added, t.Updated, t.Removed, total = t.Technologies.Count };
            await progress(50, "Evaluating drift");
            var d = await _drift.ScanAsync(null, job.TenantId, job.WorkspaceId, adrId, job.CorrelationId, ct);
            drift = new { d.OverallStatus, findings = d.Findings.Count, d.StatusChanges };
        }
        await progress(80, "Checking contradictions");
        var c = await _contradictions.CheckAsync(null, job.TenantId, job.WorkspaceId, adrId, includeDrafts: true, job.CorrelationId, persist: true, ct);
        return new JobOutcome(true, JsonSerializer.Serialize(new { uploadId = job.Parameters.GetValueOrDefault("uploadId"), adrId, technologies = tech, drift, contradictions = new { findings = c.Findings.Count, c.CandidatesEvaluated } }, AppJson.Options));
    }
}

public sealed class TechnologyRefreshJobHandler : IJobHandler
{
    private readonly TechnologyService _technology;
    private readonly DriftService _drift;
    public TechnologyRefreshJobHandler(TechnologyService technology, DriftService drift) { _technology = technology; _drift = drift; }
    public JobType Type => JobType.TechnologyRefresh;
    public async Task<JobOutcome> ExecuteAsync(AnalysisJob job, Func<int, string, Task> progress, CancellationToken ct)
    {
        var adrId = job.Parameters["adrId"];
        await progress(10, "Refreshing technologies");
        var t = await _technology.RefreshAsync(null, job.TenantId, job.WorkspaceId, adrId, job.CorrelationId, ct);
        await progress(60, "Evaluating drift");
        var d = await _drift.ScanAsync(null, job.TenantId, job.WorkspaceId, adrId, job.CorrelationId, ct);
        return new JobOutcome(true, JsonSerializer.Serialize(new { adrId, t.Added, t.Updated, t.Removed, technologies = t.Technologies.Count, drift = new { d.OverallStatus, findings = d.Findings.Count, d.StatusChanges } }, AppJson.Options));
    }
}

public sealed class DriftScanJobHandler : IJobHandler
{
    private readonly DriftService _drift;
    public DriftScanJobHandler(DriftService drift) => _drift = drift;
    public JobType Type => JobType.DriftScan;
    public async Task<JobOutcome> ExecuteAsync(AnalysisJob job, Func<int, string, Task> progress, CancellationToken ct)
    {
        await progress(10, "Loading evidence");
        var d = await _drift.ScanAsync(null, job.TenantId, job.WorkspaceId, job.Parameters["adrId"], job.CorrelationId, ct);
        return new JobOutcome(true, JsonSerializer.Serialize(new { adrId = job.Parameters["adrId"], d.OverallStatus, d.StatusChanges, d.EvidenceFiles, findings = d.Findings.Select(f => new { f.Id, f.ClaimText, result = f.Result.ToString(), severity = f.Severity.ToString() }) }, AppJson.Options));
    }
}

public sealed class ContradictionScanJobHandler : IJobHandler
{
    private readonly ContradictionService _contradictions;
    public ContradictionScanJobHandler(ContradictionService contradictions) => _contradictions = contradictions;
    public JobType Type => JobType.ContradictionScan;
    public async Task<JobOutcome> ExecuteAsync(AnalysisJob job, Func<int, string, Task> progress, CancellationToken ct)
    {
        await progress(10, "Comparing ADRs");
        var c = await _contradictions.CheckAsync(null, job.TenantId, job.WorkspaceId, job.Parameters.GetValueOrDefault("adrId"), includeDrafts: true, job.CorrelationId, persist: true, ct);
        return new JobOutcome(true, JsonSerializer.Serialize(new { c.AdrsCompared, c.CandidatesEvaluated, findings = c.Findings.Select(f => new { f.Id, f.AdrId, f.OtherAdrId, f.ContradictionType, severity = f.Severity.ToString(), f.NeedsSemanticReview }) }, AppJson.Options));
    }
}
