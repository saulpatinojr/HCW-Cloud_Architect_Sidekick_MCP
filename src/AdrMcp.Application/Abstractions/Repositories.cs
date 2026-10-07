using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Audit;
using AdrMcp.Domain.Files;
using AdrMcp.Domain.Findings;
using AdrMcp.Domain.History;
using AdrMcp.Domain.Jobs;
using AdrMcp.Domain.Memory;
using AdrMcp.Domain.Proposals;

namespace AdrMcp.Application.Abstractions;

public sealed record AdrQuery(
    string TenantId,
    string WorkspaceId,
    AdrStatus? Status = null,
    string? Tag = null,
    string? Owner = null,
    string? RepositoryId = null,
    int Take = 25,
    string? ContinuationToken = null);

public sealed record Page<T>(IReadOnlyList<T> Items, string? ContinuationToken, int? TotalEstimate = null);

/// <summary>ADR persistence. Implementations must enforce the partition (tenant|workspace) on every call.</summary>
public interface IAdrRepository
{
    Task<Adr?> GetAsync(string tenantId, string workspaceId, string adrId, CancellationToken ct);
    /// <summary>Create. Throws ConflictException-style DomainException when id already exists.</summary>
    Task<Adr> CreateAsync(Adr adr, CancellationToken ct);
    /// <summary>Replace with If-Match on adr.ETag. Throws ConcurrencyException on mismatch.</summary>
    Task<Adr> ReplaceAsync(Adr adr, CancellationToken ct);
    Task<Page<Adr>> ListAsync(AdrQuery query, CancellationToken ct);
    /// <summary>Keyword search over title/context/decision/tags within one partition. MVP uses CONTAINS-based search.</summary>
    Task<IReadOnlyList<Adr>> SearchAsync(string tenantId, string workspaceId, IReadOnlyList<string> terms, AdrStatus? status, int take, CancellationToken ct);
    Task<IReadOnlyList<Adr>> ListByStatusesAsync(string tenantId, string workspaceId, IReadOnlyCollection<AdrStatus> statuses, CancellationToken ct);
    Task<int> NextAdrNumberAsync(string tenantId, string workspaceId, CancellationToken ct);
}

public interface IAdrHistoryRepository
{
    Task AppendAsync(AdrVersionSnapshot snapshot, CancellationToken ct);
    Task<IReadOnlyList<AdrVersionSnapshot>> ListAsync(string tenantId, string workspaceId, string adrId, int take, CancellationToken ct);
    Task<AdrVersionSnapshot?> GetAsync(string tenantId, string workspaceId, string adrId, int humanRevision, int observedRevision, CancellationToken ct);
}

public interface IProposalRepository
{
    Task<ChangeProposal> CreateAsync(ChangeProposal proposal, CancellationToken ct);
    Task<ChangeProposal?> GetAsync(string tenantId, string workspaceId, string proposalId, CancellationToken ct);
    Task<ChangeProposal> ReplaceAsync(ChangeProposal proposal, CancellationToken ct);
    Task<IReadOnlyList<ChangeProposal>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, ProposalStatus? status, CancellationToken ct);
}

public interface IUploadRepository
{
    Task<UploadRecord> CreateAsync(UploadRecord record, CancellationToken ct);
    Task<UploadRecord?> GetAsync(string tenantId, string workspaceId, string uploadId, CancellationToken ct);
    Task<UploadRecord> ReplaceAsync(UploadRecord record, CancellationToken ct);
    Task<UploadRecord?> FindApprovedBySha256Async(string tenantId, string workspaceId, string sha256, CancellationToken ct);
    Task<IReadOnlyList<UploadRecord>> ListApprovedAsync(string tenantId, string workspaceId, string? adrId, CancellationToken ct);
    Task<IReadOnlyList<UploadRecord>> ListExpiredRequestsAsync(string tenantId, string workspaceId, DateTimeOffset olderThan, CancellationToken ct);
}

public interface IFindingRepository
{
    Task UpsertAsync(Finding finding, CancellationToken ct);
    Task<Finding?> GetAsync(string tenantId, string workspaceId, string findingId, CancellationToken ct);
    Task<IReadOnlyList<Finding>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, FindingKind? kind, bool openOnly, CancellationToken ct);
    Task<Finding?> GetLatestByFingerprintAsync(string tenantId, string workspaceId, string fingerprintKey, CancellationToken ct);
}

public interface IJobRepository
{
    Task<AnalysisJob> CreateAsync(AnalysisJob job, CancellationToken ct);
    Task<AnalysisJob?> GetAsync(string tenantId, string workspaceId, string jobId, CancellationToken ct);
    Task<AnalysisJob> ReplaceAsync(AnalysisJob job, CancellationToken ct);
    Task<AnalysisJob?> FindActiveByDedupeKeyAsync(string tenantId, string workspaceId, string dedupeKey, CancellationToken ct);
}

public interface IAuditRepository
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken ct);
}

public interface IMemoryRepository
{
    Task AppendAsync(MemoryRecord record, CancellationToken ct);
    /// <summary>Returns candidate records containing any of the keywords. Scoring happens in the service so it is explainable and testable.</summary>
    Task<IReadOnlyList<MemoryRecord>> FindCandidatesAsync(string tenantId, string workspaceId, IReadOnlyList<string> keywords, int take, CancellationToken ct);
    Task<IReadOnlyList<MemoryRecord>> ListForAdrAsync(string tenantId, string workspaceId, string adrId, int take, CancellationToken ct);
}

public sealed record IdempotencyEntry(string Key, string RequestHash, string ResponseJson, DateTimeOffset CreatedAt);

public interface IIdempotencyStore
{
    Task<IdempotencyEntry?> GetAsync(string partitionKey, string toolName, string key, CancellationToken ct);
    Task PutAsync(string partitionKey, string toolName, IdempotencyEntry entry, TimeSpan ttl, CancellationToken ct);
}
