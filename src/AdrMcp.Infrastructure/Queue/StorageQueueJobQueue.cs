using System.Text.Json;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using Azure.Storage.Queues;

namespace AdrMcp.Infrastructure.Queue;

public sealed record JobMessage(string JobId, string TenantId, string WorkspaceId, string CorrelationId)
{
    public static JobMessage FromPartitionKey(string jobId, string partitionKey, string correlationId)
    {
        var parts = partitionKey.Split('|', 2);
        return new JobMessage(jobId, parts[0], parts.Length > 1 ? parts[1] : "", correlationId);
    }
}

/// <summary>Azure Storage Queue producer. Messages are Base64-encoded to match the Functions queue trigger default.</summary>
public sealed class StorageQueueJobQueue : IJobQueue
{
    private readonly QueueClient _queue;
    public StorageQueueJobQueue(QueueClient queue) => _queue = queue;

    public async Task EnqueueAsync(string jobId, string partitionKey, string correlationId, TimeSpan? visibilityDelay, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(JobMessage.FromPartitionKey(jobId, partitionKey, correlationId), AppJson.Options);
        try
        {
            await _queue.SendMessageAsync(body, visibilityTimeout: visibilityDelay, timeToLive: TimeSpan.FromDays(2), cancellationToken: ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.ErrorCode == "QueueNotFound")
        {
            await _queue.CreateIfNotExistsAsync(cancellationToken: ct); // local emulator; Bicep creates the queue in Azure
            await _queue.SendMessageAsync(body, visibilityTimeout: visibilityDelay, timeToLive: TimeSpan.FromDays(2), cancellationToken: ct);
        }
    }

    public Task EnsureQueueAsync(CancellationToken ct) => _queue.CreateIfNotExistsAsync(cancellationToken: ct);
}
