using System.Text.Json;
using AdrMcp.Application.Common;
using AdrMcp.Application.Services;
using AdrMcp.Infrastructure.Queue;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AdrMcp.Functions.Jobs;

/// <summary>
/// Queue-triggered worker. The host retries up to queues.maxDequeueCount (host.json) and then moves the message to
/// analysis-jobs-poison; AnalysisJobService records Poisoned state before that so the job record never goes stale.
/// </summary>
public sealed class AnalysisJobWorker
{
    private readonly AnalysisJobService _jobs;
    private readonly ILogger<AnalysisJobWorker> _logger;
    public AnalysisJobWorker(AnalysisJobService jobs, ILogger<AnalysisJobWorker> logger) { _jobs = jobs; _logger = logger; }

    [Function(nameof(AnalysisJobWorker))]
    public async Task Run([QueueTrigger("%Storage:JobsQueue%", Connection = "AzureWebJobsStorage")] QueueMessage message, CancellationToken ct)
    {
        JobMessage? job;
        try { job = JsonSerializer.Deserialize<JobMessage>(message.Body.ToString(), AppJson.Options); }
        catch (JsonException ex) { _logger.LogError(ex, "Malformed job message {MessageId}; dropping", message.MessageId); return; }
        if (job is null || string.IsNullOrEmpty(job.JobId)) { _logger.LogError("Empty job message {MessageId}; dropping", message.MessageId); return; }

        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = job.CorrelationId, ["JobId"] = job.JobId });
        _logger.LogInformation("Processing job {JobId} (dequeue {Count})", job.JobId, message.DequeueCount);
        await _jobs.ProcessAsync(job.TenantId, job.WorkspaceId, job.JobId, (int)message.DequeueCount, ct);
    }
}
