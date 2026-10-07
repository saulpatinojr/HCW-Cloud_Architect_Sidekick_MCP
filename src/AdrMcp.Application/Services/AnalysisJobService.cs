using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Jobs;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed record JobOutcome(bool Succeeded, string? ResultJson, string? ErrorCode = null, string? ErrorMessage = null, bool Retryable = false);

/// <summary>Handler for one job type. Must be idempotent: a job may be re-delivered after a crash.</summary>
public interface IJobHandler
{
    JobType Type { get; }
    Task<JobOutcome> ExecuteAsync(AnalysisJob job, Func<int, string, Task> progress, CancellationToken ct);
}

/// <summary>Queue-backed asynchronous processing with dedupe, cancellation, retry and poison handling.</summary>
public sealed class AnalysisJobService
{
    private readonly IJobRepository _jobs;
    private readonly IJobQueue _queue;
    private readonly IEnumerable<IJobHandler> _handlers;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly JobOptions _options;
    private readonly ILogger<AnalysisJobService> _logger;

    public AnalysisJobService(IJobRepository jobs, IJobQueue queue, IEnumerable<IJobHandler> handlers, IAuthorizationService authz, IClock clock, IOptions<JobOptions> options, ILogger<AnalysisJobService> logger)
    {
        _jobs = jobs; _queue = queue; _handlers = handlers; _authz = authz; _clock = clock; _options = options.Value; _logger = logger;
    }

    public async Task<AnalysisJob> EnqueueAsync(TenantContext ctx, string workspaceId, JobType type, IReadOnlyDictionary<string, string> parameters, string? dedupeKey, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AnalysisRun);
        if (dedupeKey is not null)
        {
            var existing = await _jobs.FindActiveByDedupeKeyAsync(ctx.TenantId, workspaceId, dedupeKey, ct);
            if (existing is not null) return existing;
        }
        var now = _clock.UtcNow;
        var job = new AnalysisJob
        {
            Id = Ids.New("job"),
            TenantId = ctx.TenantId,
            WorkspaceId = workspaceId,
            Type = type,
            CorrelationId = ctx.CorrelationId,
            RequestedBy = ctx.PrincipalId,
            CreatedAt = now,
            DedupeKey = dedupeKey,
            Parameters = parameters,
            MaxAttempts = _options.MaxAttempts,
            ExpiresAt = now.AddDays(_options.ResultRetentionDays),
            Ttl = (int)TimeSpan.FromDays(_options.ResultRetentionDays).TotalSeconds
        };
        var created = await _jobs.CreateAsync(job, ct);
        await _queue.EnqueueAsync(created.Id, created.PartitionKey, ctx.CorrelationId, null, ct);
        return created;
    }

    public async Task<AnalysisJob> GetAsync(TenantContext ctx, string workspaceId, string jobId, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.JobRead);
        var job = await _jobs.GetAsync(ctx.TenantId, workspaceId, jobId, ct) ?? throw new NotFoundException(ErrorCodes.JobNotFound, $"Job '{jobId}' not found.");
        if (!job.IsTerminal && job.ExpiresAt < _clock.UtcNow) job = await _jobs.ReplaceAsync(job with { State = JobState.Expired }, ct);
        return job;
    }

    public async Task<AnalysisJob> CancelAsync(TenantContext ctx, string workspaceId, string jobId, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.JobCancel);
        var job = await GetAsync(ctx, workspaceId, jobId, ct);
        if (!job.IsCancellable) throw new DomainException(ErrorCodes.JobNotCancellable, $"Job is {job.State}.");
        var updated = job.State == JobState.Queued
            ? job with { State = JobState.Cancelled, CompletedAt = _clock.UtcNow, CancellationRequested = true }
            : job with { CancellationRequested = true, StatusMessage = "Cancellation requested" };
        return await _jobs.ReplaceAsync(updated, ct);
    }

    /// <summary>Queue-trigger entry point. Idempotent and safe under at-least-once delivery.</summary>
    public async Task ProcessAsync(string tenantId, string workspaceId, string jobId, int dequeueCount, CancellationToken ct)
    {
        var job = await _jobs.GetAsync(tenantId, workspaceId, jobId, ct);
        if (job is null) { _logger.LogWarning("Job {JobId} not found; dropping message", jobId); return; }
        if (job.IsTerminal) { _logger.LogInformation("Job {JobId} already {State}; skipping duplicate delivery", jobId, job.State); return; }
        if (job.CancellationRequested) { await _jobs.ReplaceAsync(job with { State = JobState.Cancelled, CompletedAt = _clock.UtcNow }, ct); return; }
        if (job.ExpiresAt < _clock.UtcNow) { await _jobs.ReplaceAsync(job with { State = JobState.Expired, CompletedAt = _clock.UtcNow }, ct); return; }

        var handler = _handlers.FirstOrDefault(h => h.Type == job.Type);
        if (handler is null)
        {
            await _jobs.ReplaceAsync(job with { State = JobState.Failed, ErrorCode = ErrorCodes.NotImplemented, ErrorMessage = $"No handler for {job.Type}", CompletedAt = _clock.UtcNow }, ct);
            return;
        }

        job = await _jobs.ReplaceAsync(job with { State = JobState.Running, StartedAt = job.StartedAt ?? _clock.UtcNow, Attempt = dequeueCount }, ct);
        var current = job;
        async Task Progress(int pct, string msg)
        {
            var latest = await _jobs.GetAsync(tenantId, workspaceId, jobId, ct);
            if (latest?.CancellationRequested == true) throw new OperationCanceledException("Job cancelled by user.");
            current = await _jobs.ReplaceAsync((latest ?? current) with { ProgressPercent = Math.Clamp(pct, 0, 100), StatusMessage = msg }, ct);
        }

        try
        {
            var outcome = await handler.ExecuteAsync(job, Progress, ct);
            var latest = await _jobs.GetAsync(tenantId, workspaceId, jobId, ct) ?? current;
            if (outcome.Succeeded)
                await _jobs.ReplaceAsync(latest with { State = JobState.Succeeded, ProgressPercent = 100, ResultJson = outcome.ResultJson, CompletedAt = _clock.UtcNow, StatusMessage = "Completed" }, ct);
            else if (outcome.Retryable && dequeueCount < job.MaxAttempts)
                throw new DomainException(outcome.ErrorCode ?? ErrorCodes.Internal, outcome.ErrorMessage ?? "Retryable failure", retryable: true); // let the queue retry
            else
                await _jobs.ReplaceAsync(latest with { State = JobState.Failed, ErrorCode = outcome.ErrorCode, ErrorMessage = SensitiveDataRedactor.Redact(outcome.ErrorMessage), CompletedAt = _clock.UtcNow }, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var latest = await _jobs.GetAsync(tenantId, workspaceId, jobId, ct) ?? current;
            await _jobs.ReplaceAsync(latest with { State = JobState.Cancelled, CompletedAt = _clock.UtcNow, StatusMessage = "Cancelled" }, ct);
        }
        catch (Exception ex) when (ex is not DomainException { Retryable: true })
        {
            _logger.LogError(ex, "Job {JobId} failed on attempt {Attempt}", jobId, dequeueCount);
            var latest = await _jobs.GetAsync(tenantId, workspaceId, jobId, ct) ?? current;
            if (dequeueCount >= job.MaxAttempts)
            {
                await _jobs.ReplaceAsync(latest with { State = JobState.Poisoned, ErrorCode = ErrorCodes.Internal, ErrorMessage = SensitiveDataRedactor.Redact(ex.Message), CompletedAt = _clock.UtcNow }, ct);
                return; // swallow: message is dropped; the job record carries the failure
            }
            await _jobs.ReplaceAsync(latest with { StatusMessage = $"Attempt {dequeueCount} failed; will retry", ErrorMessage = SensitiveDataRedactor.Redact(ex.Message) }, ct);
            throw; // rethrow so the Functions host re-queues the message
        }
    }
}
