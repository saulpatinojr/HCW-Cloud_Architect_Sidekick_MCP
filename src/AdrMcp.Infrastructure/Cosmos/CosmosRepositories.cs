using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Audit;
using AdrMcp.Domain.Files;
using AdrMcp.Domain.Findings;
using AdrMcp.Domain.History;
using AdrMcp.Domain.Jobs;
using AdrMcp.Domain.Memory;
using AdrMcp.Domain.Proposals;
using Microsoft.Azure.Cosmos;

namespace AdrMcp.Infrastructure.Cosmos;

/// <summary>
/// ADR container. Partition key = "{tenantId}|{workspaceId}". Query patterns: point read by id, list/filter within a workspace,
/// keyword CONTAINS search within a workspace, MAX(adrNumber) within a workspace. Cross-workspace queries are never issued.
/// </summary>
public sealed class CosmosAdrRepository : IAdrRepository
{
    private readonly CosmosContext _ctx;
    public CosmosAdrRepository(CosmosContext ctx) => _ctx = ctx;

    public async Task<Adr?> GetAsync(string tenantId, string workspaceId, string adrId, CancellationToken ct)
    {
        var (adr, etag) = await CosmosContext.ReadAsync<Adr>(_ctx.Adrs, adrId, Adr.BuildPartitionKey(tenantId, workspaceId), ct);
        if (adr is null) return null;
        if (adr.TenantId != tenantId || adr.WorkspaceId != workspaceId) return null; // defence in depth
        adr.ETag = etag;
        return adr;
    }

    public async Task<Adr> CreateAsync(Adr adr, CancellationToken ct)
    {
        var (_, etag) = await CosmosContext.CreateAsync(_ctx.Adrs, adr, adr.PartitionKey, ct);
        adr.ETag = etag;
        return adr;
    }

    public async Task<Adr> ReplaceAsync(Adr adr, CancellationToken ct)
    {
        adr.ETag = await CosmosContext.ReplaceAsync(_ctx.Adrs, adr, adr.Id, adr.PartitionKey, adr.ETag, ct);
        return adr;
    }

    public async Task<Page<Adr>> ListAsync(AdrQuery q, CancellationToken ct)
    {
        var sql = "SELECT * FROM c WHERE c.partitionKey = @pk AND c.isDeleted = false";
        var def = new QueryDefinition(sql);
        if (q.Status is not null) { sql += " AND c.human.status = @status"; }
        if (q.Tag is not null) { sql += " AND ARRAY_CONTAINS(c.human.tags, @tag, false)"; }
        if (q.Owner is not null) { sql += " AND ARRAY_CONTAINS(c.human.owners, @owner, false)"; }
        if (q.RepositoryId is not null) { sql += " AND c.repositoryId = @repo"; }
        sql += " ORDER BY c.adrNumber DESC";
        def = new QueryDefinition(sql).WithParameter("@pk", Adr.BuildPartitionKey(q.TenantId, q.WorkspaceId));
        if (q.Status is not null) def = def.WithParameter("@status", JsonNamingPolicy.CamelCase.ConvertName(q.Status.Value.ToString()));
        if (q.Tag is not null) def = def.WithParameter("@tag", q.Tag);
        if (q.Owner is not null) def = def.WithParameter("@owner", q.Owner);
        if (q.RepositoryId is not null) def = def.WithParameter("@repo", q.RepositoryId);
        var (items, token) = await CosmosContext.QueryAsync<Adr>(_ctx.Adrs, def, Adr.BuildPartitionKey(q.TenantId, q.WorkspaceId), q.Take, q.ContinuationToken, ct);
        return new Page<Adr>(items.Select(i => { i.Entity.ETag = i.ETag; return i.Entity; }).ToList(), token);
    }

    public async Task<IReadOnlyList<Adr>> SearchAsync(string tenantId, string workspaceId, IReadOnlyList<string> terms, AdrStatus? status, int take, CancellationToken ct)
    {
        var clauses = new List<string>();
        var def = new QueryDefinition("SELECT 1"); // placeholder, rebuilt below
        var sb = new System.Text.StringBuilder("SELECT * FROM c WHERE c.partitionKey = @pk AND c.isDeleted = false");
        if (status is not null) sb.Append(" AND c.human.status = @status");
        for (int i = 0; i < Math.Min(terms.Count, 8); i++)
            clauses.Add($"(CONTAINS(LOWER(c.human.title), @t{i}) OR CONTAINS(LOWER(c.human.decision), @t{i}) OR CONTAINS(LOWER(c.human.context), @t{i}) OR CONTAINS(LOWER(c.human.consequences), @t{i}) OR ARRAY_CONTAINS(c.human.tags, @t{i}, false))");
        if (clauses.Count > 0) sb.Append(" AND (").Append(string.Join(" OR ", clauses)).Append(')');
        def = new QueryDefinition(sb.ToString()).WithParameter("@pk", Adr.BuildPartitionKey(tenantId, workspaceId));
        if (status is not null) def = def.WithParameter("@status", JsonNamingPolicy.CamelCase.ConvertName(status.Value.ToString()));
        for (int i = 0; i < Math.Min(terms.Count, 8); i++) def = def.WithParameter($"@t{i}", terms[i].ToLowerInvariant());
        var (items, _) = await CosmosContext.QueryAsync<Adr>(_ctx.Adrs, def, Adr.BuildPartitionKey(tenantId, workspaceId), take, null, ct);
        return items.Select(i => { i.Entity.ETag = i.ETag; return i.Entity; }).ToList();
    }

    public async Task<IReadOnlyList<Adr>> ListByStatusesAsync(string tenantId, string workspaceId, IReadOnlyCollection<AdrStatus> statuses, CancellationToken ct)
    {
        var names = statuses.Select(s => JsonNamingPolicy.CamelCase.ConvertName(s.ToString())).ToList();
        var def = new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @pk AND c.isDeleted = false AND ARRAY_CONTAINS(@statuses, c.human.status)")
            .WithParameter("@pk", Adr.BuildPartitionKey(tenantId, workspaceId)).WithParameter("@statuses", names);
        var (items, _) = await CosmosContext.QueryAsync<Adr>(_ctx.Adrs, def, Adr.BuildPartitionKey(tenantId, workspaceId), 1000, null, ct);
        return items.Select(i => { i.Entity.ETag = i.ETag; return i.Entity; }).ToList();
    }

    public async Task<int> NextAdrNumberAsync(string tenantId, string workspaceId, CancellationToken ct)
    {
        // Two concurrent creates may compute the same number; the id uniqueness constraint then rejects one (CONFLICT) and the client retries.
        var def = new QueryDefinition("SELECT VALUE MAX(c.adrNumber) FROM c WHERE c.partitionKey = @pk").WithParameter("@pk", Adr.BuildPartitionKey(tenantId, workspaceId));
        var max = await CosmosContext.ScalarAsync<int?>(_ctx.Adrs, def, Adr.BuildPartitionKey(tenantId, workspaceId), ct);
        return (max ?? 0) + 1;
    }
}

public sealed class CosmosAdrHistoryRepository : IAdrHistoryRepository
{
    private readonly CosmosContext _ctx;
    public CosmosAdrHistoryRepository(CosmosContext ctx) => _ctx = ctx;
    public Task AppendAsync(AdrVersionSnapshot s, CancellationToken ct) => CosmosContext.UpsertAsync(_ctx.History, s, s.PartitionKey, ct);
    public async Task<IReadOnlyList<AdrVersionSnapshot>> ListAsync(string tenantId, string workspaceId, string adrId, int take, CancellationToken ct)
    {
        var def = new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @pk AND c.adrId = @adrId ORDER BY c.capturedAt DESC").WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@adrId", adrId);
        var (items, _) = await CosmosContext.QueryAsync<AdrVersionSnapshot>(_ctx.History, def, $"{tenantId}|{workspaceId}", take, null, ct);
        return items.Select(i => i.Entity).ToList();
    }
    public async Task<AdrVersionSnapshot?> GetAsync(string tenantId, string workspaceId, string adrId, int humanRevision, int observedRevision, CancellationToken ct)
    {
        var def = new QueryDefinition("SELECT TOP 1 * FROM c WHERE c.partitionKey = @pk AND c.adrId = @adrId AND c.humanRevision = @h AND c.observedRevision = @o ORDER BY c.capturedAt DESC")
            .WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@adrId", adrId).WithParameter("@h", humanRevision).WithParameter("@o", observedRevision);
        var (items, _) = await CosmosContext.QueryAsync<AdrVersionSnapshot>(_ctx.History, def, $"{tenantId}|{workspaceId}", 1, null, ct);
        return items.FirstOrDefault().Entity;
    }
}

public sealed class CosmosProposalRepository : IProposalRepository
{
    private readonly CosmosContext _ctx;
    public CosmosProposalRepository(CosmosContext ctx) => _ctx = ctx;
    public async Task<ChangeProposal> CreateAsync(ChangeProposal p, CancellationToken ct) { var (_, etag) = await CosmosContext.CreateAsync(_ctx.Proposals, p, p.PartitionKey, ct); return p with { ETag = etag }; }
    public async Task<ChangeProposal?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct) { var (p, etag) = await CosmosContext.ReadAsync<ChangeProposal>(_ctx.Proposals, id, $"{tenantId}|{workspaceId}", ct); return p is null ? null : p with { ETag = etag }; }
    public async Task<ChangeProposal> ReplaceAsync(ChangeProposal p, CancellationToken ct) => p with { ETag = await CosmosContext.ReplaceAsync(_ctx.Proposals, p, p.Id, p.PartitionKey, p.ETag, ct) };
    public async Task<IReadOnlyList<ChangeProposal>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, ProposalStatus? status, CancellationToken ct)
    {
        var sql = "SELECT * FROM c WHERE c.partitionKey = @pk AND c.adrId = @adrId" + (status is null ? "" : " AND c.status = @status") + " ORDER BY c.proposedAt DESC";
        var def = new QueryDefinition(sql).WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@adrId", adrId);
        if (status is not null) def = def.WithParameter("@status", JsonNamingPolicy.CamelCase.ConvertName(status.Value.ToString()));
        var (items, _) = await CosmosContext.QueryAsync<ChangeProposal>(_ctx.Proposals, def, $"{tenantId}|{workspaceId}", 100, null, ct);
        return items.Select(i => i.Entity with { ETag = i.ETag }).ToList();
    }
}

public sealed class CosmosUploadRepository : IUploadRepository
{
    private readonly CosmosContext _ctx;
    public CosmosUploadRepository(CosmosContext ctx) => _ctx = ctx;
    public async Task<UploadRecord> CreateAsync(UploadRecord r, CancellationToken ct) { var (_, etag) = await CosmosContext.CreateAsync(_ctx.Evidence, r, r.PartitionKey, ct); return r with { ETag = etag }; }
    public async Task<UploadRecord?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct) { var (r, etag) = await CosmosContext.ReadAsync<UploadRecord>(_ctx.Evidence, id, $"{tenantId}|{workspaceId}", ct); return r is null ? null : r with { ETag = etag }; }
    public async Task<UploadRecord> ReplaceAsync(UploadRecord r, CancellationToken ct) => r with { ETag = await CosmosContext.ReplaceAsync(_ctx.Evidence, r, r.Id, r.PartitionKey, r.ETag, ct) };
    public async Task<UploadRecord?> FindApprovedBySha256Async(string tenantId, string workspaceId, string sha256, CancellationToken ct)
    {
        var def = new QueryDefinition("SELECT TOP 1 * FROM c WHERE c.partitionKey = @pk AND c.state = 'approved' AND c.actualSha256 = @sha").WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@sha", sha256);
        var (items, _) = await CosmosContext.QueryAsync<UploadRecord>(_ctx.Evidence, def, $"{tenantId}|{workspaceId}", 1, null, ct);
        return items.Count == 0 ? null : items[0].Entity with { ETag = items[0].ETag };
    }
    public async Task<IReadOnlyList<UploadRecord>> ListApprovedAsync(string tenantId, string workspaceId, string? adrId, CancellationToken ct)
    {
        var sql = "SELECT * FROM c WHERE c.partitionKey = @pk AND c.state = 'approved'" + (adrId is null ? "" : " AND c.adrId = @adrId") + " ORDER BY c.registeredAt DESC";
        var def = new QueryDefinition(sql).WithParameter("@pk", $"{tenantId}|{workspaceId}");
        if (adrId is not null) def = def.WithParameter("@adrId", adrId);
        var (items, _) = await CosmosContext.QueryAsync<UploadRecord>(_ctx.Evidence, def, $"{tenantId}|{workspaceId}", 1000, null, ct);
        return items.Select(i => i.Entity with { ETag = i.ETag }).ToList();
    }
    public async Task<IReadOnlyList<UploadRecord>> ListExpiredRequestsAsync(string tenantId, string workspaceId, DateTimeOffset olderThan, CancellationToken ct)
    {
        var def = new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @pk AND c.state = 'requested' AND c.uploadUrlExpiresAt < @t").WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@t", olderThan.ToString("O"));
        var (items, _) = await CosmosContext.QueryAsync<UploadRecord>(_ctx.Evidence, def, $"{tenantId}|{workspaceId}", 500, null, ct);
        return items.Select(i => i.Entity with { ETag = i.ETag }).ToList();
    }
}

/// <summary>Findings are polymorphic; a "findingType" discriminator is written alongside the serialized record.</summary>
public sealed class CosmosFindingRepository : IFindingRepository
{
    private readonly CosmosContext _ctx;
    public CosmosFindingRepository(CosmosContext ctx) => _ctx = ctx;

    public static JsonObject ToDoc(Finding f)
    {
        var doc = CosmosContext.ToDocument<object>(f);
        doc["findingType"] = f.GetType().Name;
        return doc;
    }

    public static Finding? FromDoc(JsonObject doc)
    {
        var type = doc["findingType"]?.ToString();
        doc.Remove("_etag"); doc.Remove("_rid"); doc.Remove("_self"); doc.Remove("_attachments"); doc.Remove("_ts"); doc.Remove("findingType");
        return type switch
        {
            nameof(ContradictionFinding) => doc.Deserialize<ContradictionFinding>(AppJson.Options),
            nameof(DriftFinding) => doc.Deserialize<DriftFinding>(AppJson.Options),
            nameof(SafetyFinding) => doc.Deserialize<SafetyFinding>(AppJson.Options),
            nameof(GovernanceFinding) => doc.Deserialize<GovernanceFinding>(AppJson.Options),
            _ => null
        };
    }

    public async Task UpsertAsync(Finding f, CancellationToken ct)
    {
        var doc = ToDoc(f);
        var response = await _ctx.Findings.UpsertItemStreamAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(doc)), new PartitionKey($"{f.TenantId}|{f.WorkspaceId}"), cancellationToken: ct);
        if (!response.IsSuccessStatusCode) throw new Domain.Errors.DomainException(Domain.Errors.ErrorCodes.Internal, $"Finding upsert failed: {(int)response.StatusCode}", retryable: true);
    }

    public async Task<Finding?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct)
    {
        var (doc, _) = await CosmosContext.ReadDocumentAsync(_ctx.Findings, id, $"{tenantId}|{workspaceId}", ct);
        return doc is null ? null : FromDoc(doc);
    }

    public async Task<IReadOnlyList<Finding>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, FindingKind? kind, bool openOnly, CancellationToken ct)
    {
        var sql = "SELECT * FROM c WHERE c.partitionKey = @pk AND (c.adrId = @adrId OR c.otherAdrId = @adrId)" + (kind is null ? "" : " AND c.kind = @kind") + (openOnly ? " AND c.reviewStatus = 'open'" : "") + " ORDER BY c.provenance.timestamp DESC";
        var def = new QueryDefinition(sql).WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@adrId", adrId);
        if (kind is not null) def = def.WithParameter("@kind", JsonNamingPolicy.CamelCase.ConvertName(kind.Value.ToString()));
        var docs = await CosmosContext.QueryDocumentsAsync(_ctx.Findings, def, $"{tenantId}|{workspaceId}", 500, ct);
        return docs.Select(FromDoc).Where(f => f is not null).Select(f => f!).ToList();
    }

    public async Task<Finding?> GetLatestByFingerprintAsync(string tenantId, string workspaceId, string fingerprintKey, CancellationToken ct)
    {
        var def = new QueryDefinition("SELECT TOP 1 * FROM c WHERE c.partitionKey = @pk AND c.fingerprintKey = @fp ORDER BY c.provenance.timestamp DESC").WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@fp", fingerprintKey);
        var docs = await CosmosContext.QueryDocumentsAsync(_ctx.Findings, def, $"{tenantId}|{workspaceId}", 1, ct);
        return docs.Count == 0 ? null : FromDoc(docs[0]);
    }
}

public sealed class CosmosJobRepository : IJobRepository
{
    private readonly CosmosContext _ctx;
    public CosmosJobRepository(CosmosContext ctx) => _ctx = ctx;
    public async Task<AnalysisJob> CreateAsync(AnalysisJob j, CancellationToken ct) { var (_, etag) = await CosmosContext.CreateAsync(_ctx.Jobs, j, j.PartitionKey, ct); return j with { ETag = etag }; }
    public async Task<AnalysisJob?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct) { var (j, etag) = await CosmosContext.ReadAsync<AnalysisJob>(_ctx.Jobs, id, $"{tenantId}|{workspaceId}", ct); return j is null ? null : j with { ETag = etag }; }
    public async Task<AnalysisJob> ReplaceAsync(AnalysisJob j, CancellationToken ct) => j with { ETag = await CosmosContext.ReplaceAsync(_ctx.Jobs, j, j.Id, j.PartitionKey, j.ETag, ct) };
    public async Task<AnalysisJob?> FindActiveByDedupeKeyAsync(string tenantId, string workspaceId, string dedupeKey, CancellationToken ct)
    {
        var def = new QueryDefinition("SELECT TOP 1 * FROM c WHERE c.partitionKey = @pk AND c.dedupeKey = @k AND (c.state = 'queued' OR c.state = 'running') ORDER BY c.createdAt DESC").WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@k", dedupeKey);
        var (items, _) = await CosmosContext.QueryAsync<AnalysisJob>(_ctx.Jobs, def, $"{tenantId}|{workspaceId}", 1, null, ct);
        return items.Count == 0 ? null : items[0].Entity with { ETag = items[0].ETag };
    }
}

public sealed class CosmosAuditRepository : IAuditRepository
{
    private readonly CosmosContext _ctx;
    public CosmosAuditRepository(CosmosContext ctx) => _ctx = ctx;
    public Task AppendAsync(AuditEvent e, CancellationToken ct) => CosmosContext.UpsertAsync(_ctx.Audit, e, e.PartitionKey, ct);
}

public sealed class CosmosMemoryRepository : IMemoryRepository
{
    private readonly CosmosContext _ctx;
    public CosmosMemoryRepository(CosmosContext ctx) => _ctx = ctx;
    public Task AppendAsync(MemoryRecord r, CancellationToken ct) => CosmosContext.UpsertAsync(_ctx.Memory, r, r.PartitionKey, ct);
    public async Task<IReadOnlyList<MemoryRecord>> FindCandidatesAsync(string tenantId, string workspaceId, IReadOnlyList<string> keywords, int take, CancellationToken ct)
    {
        var clauses = keywords.Take(12).Select((_, i) => $"ARRAY_CONTAINS(c.keywords, @k{i})").ToList();
        if (clauses.Count == 0) return Array.Empty<MemoryRecord>();
        var def = new QueryDefinition($"SELECT * FROM c WHERE c.partitionKey = @pk AND ({string.Join(" OR ", clauses)}) ORDER BY c.timestamp DESC").WithParameter("@pk", $"{tenantId}|{workspaceId}");
        for (int i = 0; i < Math.Min(keywords.Count, 12); i++) def = def.WithParameter($"@k{i}", keywords[i].ToLowerInvariant());
        var (items, _) = await CosmosContext.QueryAsync<MemoryRecord>(_ctx.Memory, def, $"{tenantId}|{workspaceId}", take, null, ct);
        return items.Select(i => i.Entity).ToList();
    }
    public async Task<IReadOnlyList<MemoryRecord>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, int take, CancellationToken ct)
    {
        var def = new QueryDefinition("SELECT * FROM c WHERE c.partitionKey = @pk AND ARRAY_CONTAINS(c.adrIds, @adrId) ORDER BY c.timestamp DESC").WithParameter("@pk", $"{tenantId}|{workspaceId}").WithParameter("@adrId", adrId);
        var (items, _) = await CosmosContext.QueryAsync<MemoryRecord>(_ctx.Memory, def, $"{tenantId}|{workspaceId}", take, null, ct);
        return items.Select(i => i.Entity).ToList();
    }
}

public sealed class CosmosIdempotencyStore : IIdempotencyStore
{
    private sealed record Doc(string Id, string PartitionKey, string ToolName, string Key, string RequestHash, string ResponseJson, DateTimeOffset CreatedAt, int Ttl);
    private readonly CosmosContext _ctx;
    public CosmosIdempotencyStore(CosmosContext ctx) => _ctx = ctx;
    private static string DocId(string tool, string key) => $"{tool}:{Hashing.Sha256Hex(key)[..32]}";
    public async Task<IdempotencyEntry?> GetAsync(string partitionKey, string toolName, string key, CancellationToken ct)
    {
        var (d, _) = await CosmosContext.ReadAsync<Doc>(_ctx.Idempotency, DocId(toolName, key), partitionKey, ct);
        return d is null ? null : new IdempotencyEntry(d.Key, d.RequestHash, d.ResponseJson, d.CreatedAt);
    }
    public Task PutAsync(string partitionKey, string toolName, IdempotencyEntry e, TimeSpan ttl, CancellationToken ct)
        => CosmosContext.UpsertAsync(_ctx.Idempotency, new Doc(DocId(toolName, e.Key), partitionKey, toolName, e.Key, e.RequestHash, e.ResponseJson, e.CreatedAt, (int)ttl.TotalSeconds), partitionKey, ct);
}
