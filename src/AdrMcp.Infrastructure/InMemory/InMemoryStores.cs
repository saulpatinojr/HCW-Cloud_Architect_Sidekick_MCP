using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Audit;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Files;
using AdrMcp.Domain.Findings;
using AdrMcp.Domain.History;
using AdrMcp.Domain.Jobs;
using AdrMcp.Domain.Memory;
using AdrMcp.Domain.Proposals;

namespace AdrMcp.Infrastructure.InMemory;

/// <summary>
/// In-memory implementations for tests and credential-free local runs. They mimic Cosmos semantics that matter:
/// partition scoping, ETag generation, If-Match conflicts and id uniqueness. Documents are stored as JSON to catch serialization bugs.
/// </summary>
public sealed class InMemoryDocumentStore
{
    private readonly ConcurrentDictionary<(string Pk, string Container, string Id), (string Json, string ETag)> _docs = new();
    private long _etagCounter;

    public string NewEtag() => $"\"{Interlocked.Increment(ref _etagCounter):x}\"";

    public (T Entity, string ETag) Create<T>(string container, string pk, string id, T entity)
    {
        var json = JsonSerializer.Serialize(entity, AppJson.Options);
        var etag = NewEtag();
        if (!_docs.TryAdd((pk, container, id), (json, etag))) throw new DomainException(ErrorCodes.Conflict, $"An item with id '{id}' already exists.");
        return (JsonSerializer.Deserialize<T>(json, AppJson.Options)!, etag);
    }

    public (T Entity, string ETag) Replace<T>(string container, string pk, string id, T entity, string? ifMatch)
    {
        var key = (pk, container, id);
        if (!_docs.TryGetValue(key, out var current)) throw new NotFoundException(ErrorCodes.NotFound, $"Item '{id}' not found.");
        if (ifMatch is not null && current.ETag != ifMatch) throw new ConcurrencyException($"Item '{id}' was modified concurrently (etag mismatch).");
        var json = JsonSerializer.Serialize(entity, AppJson.Options);
        var etag = NewEtag();
        if (!_docs.TryUpdate(key, (json, etag), current)) throw new ConcurrencyException($"Item '{id}' was modified concurrently.");
        return (JsonSerializer.Deserialize<T>(json, AppJson.Options)!, etag);
    }

    public (T Entity, string ETag) Upsert<T>(string container, string pk, string id, T entity)
    {
        var json = JsonSerializer.Serialize(entity, AppJson.Options);
        var etag = NewEtag();
        _docs[(pk, container, id)] = (json, etag);
        return (JsonSerializer.Deserialize<T>(json, AppJson.Options)!, etag);
    }

    public (T Entity, string ETag)? Read<T>(string container, string pk, string id)
        => _docs.TryGetValue((pk, container, id), out var d) ? (JsonSerializer.Deserialize<T>(d.Json, AppJson.Options)!, d.ETag) : null;

    public IEnumerable<(T Entity, string ETag)> Scan<T>(string container, string pk)
        => _docs.Where(kv => kv.Key.Pk == pk && kv.Key.Container == container).Select(kv => (JsonSerializer.Deserialize<T>(kv.Value.Json, AppJson.Options)!, kv.Value.ETag)).ToList();

    public IEnumerable<string> RawJson(string container, string pk) => _docs.Where(kv => kv.Key.Pk == pk && kv.Key.Container == container).Select(kv => kv.Value.Json).ToList();
    public int Count => _docs.Count;
}

public sealed class InMemoryAdrRepository : IAdrRepository
{
    private const string C = "adrs";
    private readonly InMemoryDocumentStore _s;
    public InMemoryAdrRepository(InMemoryDocumentStore s) => _s = s;

    public Task<Adr?> GetAsync(string tenantId, string workspaceId, string adrId, CancellationToken ct)
    {
        var r = _s.Read<Adr>(C, Adr.BuildPartitionKey(tenantId, workspaceId), adrId);
        if (r is null) return Task.FromResult<Adr?>(null);
        r.Value.Entity.ETag = r.Value.ETag;
        return Task.FromResult<Adr?>(r.Value.Entity);
    }
    public Task<Adr> CreateAsync(Adr adr, CancellationToken ct) { var (e, etag) = _s.Create(C, adr.PartitionKey, adr.Id, adr); e.ETag = etag; return Task.FromResult(e); }
    public Task<Adr> ReplaceAsync(Adr adr, CancellationToken ct) { var (e, etag) = _s.Replace(C, adr.PartitionKey, adr.Id, adr, adr.ETag); e.ETag = etag; return Task.FromResult(e); }
    public Task<Page<Adr>> ListAsync(AdrQuery q, CancellationToken ct)
    {
        var all = _s.Scan<Adr>(C, Adr.BuildPartitionKey(q.TenantId, q.WorkspaceId)).Select(x => { x.Entity.ETag = x.ETag; return x.Entity; })
            .Where(a => !a.IsDeleted)
            .Where(a => q.Status is null || a.Status == q.Status)
            .Where(a => q.Tag is null || a.Human.Tags.Contains(q.Tag, StringComparer.OrdinalIgnoreCase))
            .Where(a => q.Owner is null || a.Human.Owners.Contains(q.Owner, StringComparer.OrdinalIgnoreCase))
            .Where(a => q.RepositoryId is null || a.RepositoryId == q.RepositoryId)
            .OrderByDescending(a => a.AdrNumber).ToList();
        var skip = int.TryParse(q.ContinuationToken, out var s) ? s : 0;
        var page = all.Skip(skip).Take(q.Take).ToList();
        return Task.FromResult(new Page<Adr>(page, skip + q.Take < all.Count ? (skip + q.Take).ToString() : null, all.Count));
    }
    public Task<IReadOnlyList<Adr>> SearchAsync(string tenantId, string workspaceId, IReadOnlyList<string> terms, AdrStatus? status, int take, CancellationToken ct)
    {
        var res = _s.Scan<Adr>(C, Adr.BuildPartitionKey(tenantId, workspaceId)).Select(x => { x.Entity.ETag = x.ETag; return x.Entity; })
            .Where(a => !a.IsDeleted && (status is null || a.Status == status))
            .Where(a => { var hay = (a.Human.Title + " " + a.Human.Decision + " " + a.Human.Context + " " + a.Human.Consequences + " " + string.Join(" ", a.Human.Tags)).ToLowerInvariant(); return terms.Any(t => hay.Contains(t.ToLowerInvariant())); })
            .Take(take).ToList();
        return Task.FromResult<IReadOnlyList<Adr>>(res);
    }
    public Task<IReadOnlyList<Adr>> ListByStatusesAsync(string tenantId, string workspaceId, IReadOnlyCollection<AdrStatus> statuses, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Adr>>(_s.Scan<Adr>(C, Adr.BuildPartitionKey(tenantId, workspaceId)).Select(x => { x.Entity.ETag = x.ETag; return x.Entity; }).Where(a => !a.IsDeleted && statuses.Contains(a.Status)).ToList());
    public Task<int> NextAdrNumberAsync(string tenantId, string workspaceId, CancellationToken ct)
        => Task.FromResult(_s.Scan<Adr>(C, Adr.BuildPartitionKey(tenantId, workspaceId)).Select(x => x.Entity.AdrNumber).DefaultIfEmpty(0).Max() + 1);
}

public sealed class InMemoryAdrHistoryRepository : IAdrHistoryRepository
{
    private const string C = "adr-history";
    private readonly InMemoryDocumentStore _s;
    public InMemoryAdrHistoryRepository(InMemoryDocumentStore s) => _s = s;
    public Task AppendAsync(AdrVersionSnapshot snapshot, CancellationToken ct) { _s.Upsert(C, snapshot.PartitionKey, snapshot.Id, snapshot); return Task.CompletedTask; }
    public Task<IReadOnlyList<AdrVersionSnapshot>> ListAsync(string tenantId, string workspaceId, string adrId, int take, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AdrVersionSnapshot>>(_s.Scan<AdrVersionSnapshot>(C, $"{tenantId}|{workspaceId}").Select(x => x.Entity).Where(v => v.AdrId == adrId).OrderByDescending(v => v.CapturedAt).ThenByDescending(v => v.Id).Take(take).ToList());
    public Task<AdrVersionSnapshot?> GetAsync(string tenantId, string workspaceId, string adrId, int humanRevision, int observedRevision, CancellationToken ct)
        => Task.FromResult(_s.Scan<AdrVersionSnapshot>(C, $"{tenantId}|{workspaceId}").Select(x => x.Entity).Where(v => v.AdrId == adrId && v.HumanRevision == humanRevision && v.ObservedRevision == observedRevision).OrderByDescending(v => v.CapturedAt).FirstOrDefault());
}

public sealed class InMemoryProposalRepository : IProposalRepository
{
    private const string C = "proposals";
    private readonly InMemoryDocumentStore _s;
    public InMemoryProposalRepository(InMemoryDocumentStore s) => _s = s;
    public Task<ChangeProposal> CreateAsync(ChangeProposal p, CancellationToken ct) { var (e, etag) = _s.Create(C, p.PartitionKey, p.Id, p); return Task.FromResult(e with { ETag = etag }); }
    public Task<ChangeProposal?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct) { var r = _s.Read<ChangeProposal>(C, $"{tenantId}|{workspaceId}", id); return Task.FromResult(r is null ? null : r.Value.Entity with { ETag = r.Value.ETag }); }
    public Task<ChangeProposal> ReplaceAsync(ChangeProposal p, CancellationToken ct) { var (e, etag) = _s.Replace(C, p.PartitionKey, p.Id, p, p.ETag); return Task.FromResult(e with { ETag = etag }); }
    public Task<IReadOnlyList<ChangeProposal>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, ProposalStatus? status, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ChangeProposal>>(_s.Scan<ChangeProposal>(C, $"{tenantId}|{workspaceId}").Select(x => x.Entity with { ETag = x.ETag }).Where(p => p.AdrId == adrId && (status is null || p.Status == status)).OrderByDescending(p => p.ProposedAt).ToList());
}

public sealed class InMemoryUploadRepository : IUploadRepository
{
    private const string C = "evidence";
    private readonly InMemoryDocumentStore _s;
    public InMemoryUploadRepository(InMemoryDocumentStore s) => _s = s;
    public Task<UploadRecord> CreateAsync(UploadRecord r, CancellationToken ct) { var (e, etag) = _s.Create(C, r.PartitionKey, r.Id, r); return Task.FromResult(e with { ETag = etag }); }
    public Task<UploadRecord?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct) { var r = _s.Read<UploadRecord>(C, $"{tenantId}|{workspaceId}", id); return Task.FromResult(r is null ? null : r.Value.Entity with { ETag = r.Value.ETag }); }
    public Task<UploadRecord> ReplaceAsync(UploadRecord r, CancellationToken ct) { var (e, etag) = _s.Replace(C, r.PartitionKey, r.Id, r, r.ETag); return Task.FromResult(e with { ETag = etag }); }
    public Task<UploadRecord?> FindApprovedBySha256Async(string tenantId, string workspaceId, string sha256, CancellationToken ct)
        => Task.FromResult(_s.Scan<UploadRecord>(C, $"{tenantId}|{workspaceId}").Select(x => x.Entity with { ETag = x.ETag }).FirstOrDefault(u => u.State == UploadState.Approved && u.ActualSha256 == sha256));
    public Task<IReadOnlyList<UploadRecord>> ListApprovedAsync(string tenantId, string workspaceId, string? adrId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<UploadRecord>>(_s.Scan<UploadRecord>(C, $"{tenantId}|{workspaceId}").Select(x => x.Entity with { ETag = x.ETag }).Where(u => u.State == UploadState.Approved && (adrId is null || u.AdrId == adrId)).OrderByDescending(u => u.RegisteredAt).ToList());
    public Task<IReadOnlyList<UploadRecord>> ListExpiredRequestsAsync(string tenantId, string workspaceId, DateTimeOffset olderThan, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<UploadRecord>>(_s.Scan<UploadRecord>(C, $"{tenantId}|{workspaceId}").Select(x => x.Entity with { ETag = x.ETag }).Where(u => u.State == UploadState.Requested && u.UploadUrlExpiresAt < olderThan).ToList());
}

public sealed class InMemoryFindingRepository : IFindingRepository
{
    private readonly ConcurrentDictionary<(string Pk, string Id), Finding> _f = new();
    public Task UpsertAsync(Finding f, CancellationToken ct) { _f[($"{f.TenantId}|{f.WorkspaceId}", f.Id)] = f; return Task.CompletedTask; }
    public Task<Finding?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct) => Task.FromResult(_f.TryGetValue(($"{tenantId}|{workspaceId}", id), out var f) ? f : null);
    public Task<IReadOnlyList<Finding>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, FindingKind? kind, bool openOnly, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Finding>>(_f.Where(kv => kv.Key.Pk == $"{tenantId}|{workspaceId}").Select(kv => kv.Value).Where(f => (f.AdrId == adrId || (f is ContradictionFinding c && c.OtherAdrId == adrId)) && (kind is null || f.Kind == kind) && (!openOnly || f.ReviewStatus == FindingReviewStatus.Open)).OrderByDescending(f => f.Provenance.Timestamp).ToList());
    public Task<Finding?> GetLatestByFingerprintAsync(string tenantId, string workspaceId, string fp, CancellationToken ct)
        => Task.FromResult(_f.Where(kv => kv.Key.Pk == $"{tenantId}|{workspaceId}").Select(kv => kv.Value).Where(f => f.FingerprintKey == fp).OrderByDescending(f => f.Provenance.Timestamp).FirstOrDefault());
    public int Count => _f.Count;
}

public sealed class InMemoryJobRepository : IJobRepository
{
    private const string C = "jobs";
    private readonly InMemoryDocumentStore _s;
    public InMemoryJobRepository(InMemoryDocumentStore s) => _s = s;
    public Task<AnalysisJob> CreateAsync(AnalysisJob j, CancellationToken ct) { var (e, etag) = _s.Create(C, j.PartitionKey, j.Id, j); return Task.FromResult(e with { ETag = etag }); }
    public Task<AnalysisJob?> GetAsync(string tenantId, string workspaceId, string id, CancellationToken ct) { var r = _s.Read<AnalysisJob>(C, $"{tenantId}|{workspaceId}", id); return Task.FromResult(r is null ? null : r.Value.Entity with { ETag = r.Value.ETag }); }
    public Task<AnalysisJob> ReplaceAsync(AnalysisJob j, CancellationToken ct) { var (e, etag) = _s.Replace(C, j.PartitionKey, j.Id, j, j.ETag); return Task.FromResult(e with { ETag = etag }); }
    public Task<AnalysisJob?> FindActiveByDedupeKeyAsync(string tenantId, string workspaceId, string key, CancellationToken ct)
        => Task.FromResult(_s.Scan<AnalysisJob>(C, $"{tenantId}|{workspaceId}").Select(x => x.Entity with { ETag = x.ETag }).FirstOrDefault(j => j.DedupeKey == key && j.State is JobState.Queued or JobState.Running));
}

public sealed class InMemoryAuditRepository : IAuditRepository
{
    public ConcurrentQueue<AuditEvent> Events { get; } = new();
    public Task AppendAsync(AuditEvent e, CancellationToken ct) { Events.Enqueue(e); return Task.CompletedTask; }
}

public sealed class InMemoryMemoryRepository : IMemoryRepository
{
    public ConcurrentBag<MemoryRecord> Records { get; } = new();
    public Task AppendAsync(MemoryRecord r, CancellationToken ct) { Records.Add(r); return Task.CompletedTask; }
    public Task<IReadOnlyList<MemoryRecord>> FindCandidatesAsync(string tenantId, string workspaceId, IReadOnlyList<string> keywords, int take, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MemoryRecord>>(Records.Where(r => r.TenantId == tenantId && r.WorkspaceId == workspaceId && r.Keywords.Any(k => keywords.Contains(k, StringComparer.OrdinalIgnoreCase))).OrderByDescending(r => r.Timestamp).Take(take).ToList());
    public Task<IReadOnlyList<MemoryRecord>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, int take, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MemoryRecord>>(Records.Where(r => r.TenantId == tenantId && r.WorkspaceId == workspaceId && r.AdrIds.Contains(adrId)).OrderByDescending(r => r.Timestamp).Take(take).ToList());
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, (IdempotencyEntry Entry, DateTimeOffset Expires)> _d = new();
    public Task<IdempotencyEntry?> GetAsync(string pk, string tool, string key, CancellationToken ct)
        => Task.FromResult(_d.TryGetValue($"{pk}|{tool}|{key}", out var v) && v.Expires > DateTimeOffset.UtcNow ? v.Entry : null);
    public Task PutAsync(string pk, string tool, IdempotencyEntry e, TimeSpan ttl, CancellationToken ct) { _d[$"{pk}|{tool}|{e.Key}"] = (e, DateTimeOffset.UtcNow.Add(ttl)); return Task.CompletedTask; }
}

/// <summary>Records enqueued jobs; tests drive AnalysisJobService.ProcessAsync directly.</summary>
public sealed class InMemoryJobQueue : IJobQueue
{
    public ConcurrentQueue<(string JobId, string PartitionKey, string CorrelationId)> Messages { get; } = new();
    public Task EnqueueAsync(string jobId, string pk, string correlationId, TimeSpan? delay, CancellationToken ct) { Messages.Enqueue((jobId, pk, correlationId)); return Task.CompletedTask; }
}

/// <summary>In-memory blob store with the same path validation as the real one. Grants are fake URIs.</summary>
public sealed class InMemoryEvidenceBlobStore : IEvidenceBlobStore
{
    private readonly ConcurrentDictionary<(EvidenceContainer, string), (byte[] Bytes, string ContentType, IReadOnlyDictionary<string, string> Metadata)> _blobs = new();

    public void Put(EvidenceContainer c, string path, byte[] bytes, string contentType = "application/octet-stream") => _blobs[(c, path)] = (bytes, contentType, new Dictionary<string, string>());
    public bool Exists(EvidenceContainer c, string path) => _blobs.ContainsKey((c, path));
    public string? GetText(EvidenceContainer c, string path) => _blobs.TryGetValue((c, path), out var b) ? Encoding.UTF8.GetString(b.Bytes) : null;

    public Task<UploadGrant> CreateSingleBlobWriteGrantAsync(string path, string contentType, long maxBytes, TimeSpan ttl, CancellationToken ct)
    {
        Blob.BlobEvidenceStore.ValidatePath(path);
        var expires = DateTimeOffset.UtcNow.Add(ttl);
        return Task.FromResult(new UploadGrant(new Uri($"https://inmemory.blob.local/quarantine/{path}?sig=TEST-SAS-NOT-REAL&se={Uri.EscapeDataString(expires.ToString("O"))}"), "PUT", new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob", ["Content-Type"] = contentType }, expires));
    }
    public Task<Application.Abstractions.BlobProperties> GetPropertiesAsync(EvidenceContainer c, string path, CancellationToken ct)
        => Task.FromResult(_blobs.TryGetValue((c, path), out var b) ? new Application.Abstractions.BlobProperties(true, b.Bytes.Length, b.ContentType, "\"1\"", DateTimeOffset.UtcNow, b.Metadata) : new Application.Abstractions.BlobProperties(false, 0, null, null, null, new Dictionary<string, string>()));
    public Task<(string Sha256, long Bytes)> ComputeSha256Async(EvidenceContainer c, string path, long maxBytes, CancellationToken ct)
    {
        var b = _blobs[(c, path)].Bytes;
        if (b.Length > maxBytes) throw new DomainException(ErrorCodes.PayloadTooLarge, "too large");
        return Task.FromResult((Hashing.Sha256Hex(b), (long)b.Length));
    }
    public Task<string> ReadTextAsync(EvidenceContainer c, string path, int maxBytes, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetString(_blobs[(c, path)].Bytes.Take(maxBytes).ToArray()));
    public Task<byte[]> ReadBytesAsync(EvidenceContainer c, string path, int maxBytes, CancellationToken ct) => Task.FromResult(_blobs[(c, path)].Bytes.Take(maxBytes).ToArray());
    public Task WriteTextAsync(EvidenceContainer c, string path, string text, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct) { Blob.BlobEvidenceStore.ValidatePath(path); _blobs[(c, path)] = (Encoding.UTF8.GetBytes(text), contentType, metadata ?? new Dictionary<string, string>()); return Task.CompletedTask; }
    public Task CopyAsync(EvidenceContainer sc, string sp, EvidenceContainer tc, string tp, CancellationToken ct) { _blobs[(tc, tp)] = _blobs[(sc, sp)]; return Task.CompletedTask; }
    public Task DeleteIfExistsAsync(EvidenceContainer c, string path, CancellationToken ct) { _blobs.TryRemove((c, path), out _); return Task.CompletedTask; }
}
