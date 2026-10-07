using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Application.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.Infrastructure.Options;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure.Cosmos;

/// <summary>Shared Cosmos helpers: container access, System.Text.Json round-tripping, ETag mapping and error translation.</summary>
public sealed class CosmosContext
{
    private readonly CosmosClient _client;
    private readonly CosmosOptions _options;

    public CosmosContext(CosmosClient client, IOptions<CosmosOptions> options) { _client = client; _options = options.Value; }

    public CosmosOptions Options => _options;
    public Container Adrs => Get(_options.AdrsContainer);
    public Container History => Get(_options.HistoryContainer);
    public Container Proposals => Get(_options.ProposalsContainer);
    public Container Evidence => Get(_options.EvidenceContainer);
    public Container Findings => Get(_options.FindingsContainer);
    public Container Jobs => Get(_options.JobsContainer);
    public Container Memory => Get(_options.MemoryContainer);
    public Container Audit => Get(_options.AuditContainer);
    public Container Idempotency => Get(_options.IdempotencyContainer);

    private Container Get(string name) => _client.GetContainer(_options.DatabaseName, name);

    /// <summary>Creates a document with the id/partitionKey conventions (JSON camelCase: "id", "partitionKey").</summary>
    public static async Task<(JsonObject Document, string ETag)> CreateAsync<T>(Container container, T entity, string partitionKey, CancellationToken ct)
    {
        var doc = ToDocument(entity);
        try
        {
            var response = await container.CreateItemStreamAsync(ToStream(doc), new PartitionKey(partitionKey), cancellationToken: ct);
            EnsureSuccess(response, doc["id"]?.ToString());
            return (doc, response.Headers.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DomainException(ErrorCodes.Conflict, $"An item with id '{doc["id"]}' already exists.", inner: ex);
        }
    }

    public static async Task<string> ReplaceAsync<T>(Container container, T entity, string id, string partitionKey, string? etag, CancellationToken ct)
    {
        var doc = ToDocument(entity);
        var options = new ItemRequestOptions { IfMatchEtag = etag };
        try
        {
            var response = await container.ReplaceItemStreamAsync(ToStream(doc), id, new PartitionKey(partitionKey), options, ct);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new ConcurrencyException($"Item '{id}' was modified concurrently (etag mismatch).");
            EnsureSuccess(response, id);
            return response.Headers.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ConcurrencyException($"Item '{id}' was modified concurrently (etag mismatch).");
        }
    }

    public static async Task<string> UpsertAsync<T>(Container container, T entity, string partitionKey, CancellationToken ct)
    {
        var doc = ToDocument(entity);
        var response = await container.UpsertItemStreamAsync(ToStream(doc), new PartitionKey(partitionKey), cancellationToken: ct);
        EnsureSuccess(response, doc["id"]?.ToString());
        return response.Headers.ETag;
    }

    public static async Task<(T? Entity, string? ETag)> ReadAsync<T>(Container container, string id, string partitionKey, CancellationToken ct)
    {
        using var response = await container.ReadItemStreamAsync(id, new PartitionKey(partitionKey), cancellationToken: ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return (default, null);
        EnsureSuccess(response, id);
        var entity = await JsonSerializer.DeserializeAsync<T>(response.Content, AppJson.Options, ct);
        return (entity, response.Headers.ETag);
    }

    public static async Task<(JsonObject? Document, string? ETag)> ReadDocumentAsync(Container container, string id, string partitionKey, CancellationToken ct)
    {
        using var response = await container.ReadItemStreamAsync(id, new PartitionKey(partitionKey), cancellationToken: ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        EnsureSuccess(response, id);
        var node = await JsonNode.ParseAsync(response.Content, cancellationToken: ct);
        return (node as JsonObject, response.Headers.ETag);
    }

    /// <summary>Runs a SQL query within one partition and materialises results with System.Text.Json. Returns documents + etags.</summary>
    public static async Task<(List<(T Entity, string? ETag)> Items, string? Continuation)> QueryAsync<T>(Container container, QueryDefinition query, string partitionKey, int maxItems, string? continuation, CancellationToken ct)
    {
        var items = new List<(T, string?)>();
        string? token = null;
        using var iterator = container.GetItemQueryStreamIterator(query, continuation, new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey), MaxItemCount = maxItems });
        while (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(ct);
            EnsureSuccess(response, "query");
            var node = await JsonNode.ParseAsync(response.Content, cancellationToken: ct) as JsonObject;
            if (node?["Documents"] is JsonArray docs)
                foreach (var d in docs.OfType<JsonObject>())
                {
                    var etag = d["_etag"]?.ToString();
                    var entity = d.Deserialize<T>(AppJson.Options);
                    if (entity is not null) items.Add((entity, etag));
                }
            token = response.ContinuationToken;
            if (maxItems > 0 && items.Count >= maxItems) break;
            if (token is null) break;
        }
        return (items, token);
    }

    public static async Task<List<JsonObject>> QueryDocumentsAsync(Container container, QueryDefinition query, string partitionKey, int maxItems, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        using var iterator = container.GetItemQueryStreamIterator(query, null, new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey), MaxItemCount = maxItems });
        while (iterator.HasMoreResults && items.Count < maxItems)
        {
            using var response = await iterator.ReadNextAsync(ct);
            EnsureSuccess(response, "query");
            var node = await JsonNode.ParseAsync(response.Content, cancellationToken: ct) as JsonObject;
            if (node?["Documents"] is JsonArray docs) items.AddRange(docs.OfType<JsonObject>().Select(d => (JsonObject)d.DeepClone()));
        }
        return items;
    }

    public static async Task<T?> ScalarAsync<T>(Container container, QueryDefinition query, string partitionKey, CancellationToken ct)
    {
        using var iterator = container.GetItemQueryStreamIterator(query, null, new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey), MaxItemCount = 1 });
        if (!iterator.HasMoreResults) return default;
        using var response = await iterator.ReadNextAsync(ct);
        EnsureSuccess(response, "scalar");
        var node = await JsonNode.ParseAsync(response.Content, cancellationToken: ct) as JsonObject;
        var first = (node?["Documents"] as JsonArray)?.FirstOrDefault();
        return first is null ? default : first.Deserialize<T>(AppJson.Options);
    }

    public static JsonObject ToDocument<T>(T entity)
    {
        var node = JsonSerializer.SerializeToNode(entity, AppJson.Options) as JsonObject ?? throw new InvalidOperationException("Entity must serialize to an object.");
        if (node["id"] is null) throw new InvalidOperationException("Entity must have an Id property.");
        if (node["partitionKey"] is null) throw new InvalidOperationException("Entity must expose PartitionKey.");
        node.Remove("etag"); // ETag is a Cosmos system property (_etag); never store the stale copy.
        return node;
    }

    private static MemoryStream ToStream(JsonObject doc) => new(JsonSerializer.SerializeToUtf8Bytes(doc));

    private static void EnsureSuccess(ResponseMessage response, string? id)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new DomainException(ErrorCodes.RateLimited, "Cosmos DB throttled the request. Retry shortly.", retryable: true);
        throw new DomainException(ErrorCodes.Internal, $"Cosmos DB request for '{id}' failed with {(int)response.StatusCode}.", retryable: (int)response.StatusCode >= 500);
    }
}
