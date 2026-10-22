using System.Text.Json.Nodes;
using AdrMcp.Domain.Adrs;

namespace AdrMcp.Application.Tools;

public interface IToolRequest { string? WorkspaceId { get; } }
public interface IMutatingRequest : IToolRequest { string? IdempotencyKey { get; } }

// ---------- ADR management ----------
public sealed record AdrFieldsDto(string? Title, string? Context, string? Decision, string? Rationale, IReadOnlyList<string>? Alternatives, string? Consequences, string? Status,
    IReadOnlyList<string>? Owners, IReadOnlyList<string>? Approvers, string? ApprovalResult, string? HumanNotes, IReadOnlyList<string>? Tags, IReadOnlyList<DecisionClaim>? Claims, string? Supersedes, string? SupersededBy);

public sealed record AdrCreateRequest(string? WorkspaceId, string? Markdown, AdrFieldsDto? Fields, string? RepositoryId, string? IdempotencyKey) : IMutatingRequest;
public sealed record AdrGetRequest(string? WorkspaceId, string AdrId, bool? IncludeMarkdown) : IToolRequest;
public sealed record AdrListRequest(string? WorkspaceId, string? Status, string? Tag, string? Owner, int? Take, string? ContinuationToken) : IToolRequest;
public sealed record AdrSearchRequest(string? WorkspaceId, string Query, string? Status, int? Take) : IToolRequest;
public sealed record AdrUpdateRequest(string? WorkspaceId, string AdrId, AdrFieldsDto Fields, string? ExpectedEtag, string? Note, string? IdempotencyKey) : IMutatingRequest;
public sealed record ProposedChangeDto(string Field, string ProposedValue, string Reason);
public sealed record AdrProposeChangeRequest(string? WorkspaceId, string AdrId, IReadOnlyList<ProposedChangeDto> Changes, string? ProposerType, double? Confidence, string? IdempotencyKey) : IMutatingRequest;
public sealed record AdrApproveChangeRequest(string? WorkspaceId, string ProposalId, string? Note, string? ExpectedAdrEtag, string? IdempotencyKey) : IMutatingRequest;
public sealed record AdrRejectChangeRequest(string? WorkspaceId, string ProposalId, string? Note, string? IdempotencyKey) : IMutatingRequest;
public sealed record AdrSupersedeRequest(string? WorkspaceId, string OldAdrId, string NewAdrId, string? Note, string? IdempotencyKey) : IMutatingRequest;
public sealed record AdrArchiveRequest(string? WorkspaceId, string AdrId, string Reason, string? IdempotencyKey) : IMutatingRequest;
public sealed record AdrHistoryRequest(string? WorkspaceId, string AdrId, int? Take) : IToolRequest;
public sealed record AdrExportRequest(string? WorkspaceId, string AdrId, string? Format) : IToolRequest;
public sealed record AdrImportRecordDto(string Id, string? Markdown, AdrFieldsDto? Fields, string? DecidedOn, string? SourcePath);
public sealed record AdrImportRequest(string? WorkspaceId, IReadOnlyList<AdrImportRecordDto>? Records, bool? DryRun, string? IdempotencyKey) : IMutatingRequest;

// ---------- Files / evidence ----------
public sealed record FileUploadRequestRequest(string? WorkspaceId, string FileName, string MediaType, long ExpectedSizeBytes, string? Sha256, string? AdrId, string? Note, string? IdempotencyKey) : IMutatingRequest;
public sealed record FileRegisterUploadRequest(string? WorkspaceId, string UploadId, string? Sha256) : IToolRequest;
public sealed record EvidenceAttachRequest(string? WorkspaceId, string AdrId, string EvidenceId, string? Note, string? IdempotencyKey) : IMutatingRequest;
public sealed record EvidenceListRequest(string? WorkspaceId, string? AdrId) : IToolRequest;
public sealed record EvidenceRemoveRequest(string? WorkspaceId, string AdrId, string EvidenceId, string Reason, string? IdempotencyKey) : IMutatingRequest;

// ---------- Analysis ----------
public sealed record InlineFileDto(string Path, string Content);
public sealed record TechnologyDetectRequest(string? WorkspaceId, string? AdrId, IReadOnlyList<InlineFileDto>? Files) : IToolRequest;
public sealed record TechnologyRefreshRequest(string? WorkspaceId, string AdrId, bool? Async, string? IdempotencyKey) : IMutatingRequest;
public sealed record AdrCheckContradictionsRequest(string? WorkspaceId, string? AdrId, bool? IncludeDrafts, bool? Persist) : IToolRequest;
public sealed record AdrSafetyReviewRequest(string? WorkspaceId, string? AdrId, AdrFieldsDto? Inline, bool? Persist) : IToolRequest;
public sealed record AdrDetectDriftRequest(string? WorkspaceId, string AdrId, bool? Async) : IToolRequest;
public sealed record FindingReviewRequest(string? WorkspaceId, string FindingId, string Status, string? Note, string? IdempotencyKey) : IMutatingRequest;

// ---------- Jobs / memory ----------
public sealed record AnalysisJobGetRequest(string? WorkspaceId, string JobId) : IToolRequest;
public sealed record AnalysisJobCancelRequest(string? WorkspaceId, string JobId) : IToolRequest;
public sealed record AnalysisJobResultsRequest(string? WorkspaceId, string JobId) : IToolRequest;
public sealed record DecisionMemorySearchRequest(string? WorkspaceId, string Query, int? Take) : IToolRequest;

// ---------- Response DTOs (kept compact) ----------
public sealed record AdrSummaryDto(string Id, int AdrNumber, string Title, string Status, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt, int HumanRevision, int ObservedRevision, string DriftStatus, int OpenContradictions, int OpenDriftFindings, string? Etag)
{
    public static AdrSummaryDto From(Adr a) => new(a.Id, a.AdrNumber, a.Human.Title, a.Human.Status.ToString(), a.Human.Tags, a.UpdatedAt, a.HumanRevision, a.ObservedRevision, a.Observed.DriftStatus, a.Observed.ContradictionFindingIds.Count, a.Observed.DriftFindingIds.Count, a.ETag);
}

public sealed record TechnologyDto(string Name, string? Version, string Category, string Status, double Confidence, string Basis, string SourceFile, string Detector, DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt)
{
    public static TechnologyDto From(DetectedTechnology t) => new(t.NormalizedName, t.DetectedVersion, t.Category.ToString(), t.Status.ToString(), Math.Round(t.Confidence.Score, 2), t.Confidence.Basis.ToString(), t.SourceFile, $"{t.DetectorName}@{t.DetectorVersion}", t.FirstObservedAt, t.LastObservedAt);
}

public sealed record AdrDetailDto(
    AdrSummaryDto Summary,
    string Context, string Decision, string Rationale, IReadOnlyList<string> Alternatives, string Consequences,
    IReadOnlyList<string> Owners, IReadOnlyList<string> Approvers, string? ApprovalResult, string? HumanNotes, IReadOnlyList<DecisionClaim> Claims,
    string? Supersedes, string? SupersededBy, DateTimeOffset CreatedAt, DateTimeOffset? AcceptedAt, string ContentHash,
    IReadOnlyList<TechnologyDto> Technologies, int TechnologyCount, int EvidenceCount, IReadOnlyList<string> EvidenceIds, int SafetyFindingCount, DateTimeOffset? LastAnalyzedAt, string? Markdown, JsonNode? Provenance)
{
    public static AdrDetailDto From(Adr a, string? markdown, int maxTechnologies = 25) => new(
        AdrSummaryDto.From(a), a.Human.Context, a.Human.Decision, a.Human.Rationale, a.Human.Alternatives, a.Human.Consequences,
        a.Human.Owners, a.Human.Approvers, a.Human.ApprovalResult, a.Human.HumanNotes, a.Human.Claims, a.Human.Supersedes, a.Human.SupersededBy, a.CreatedAt, a.Human.AcceptedAt, a.ContentHash,
        a.Observed.DetectedTechnologies.Where(t => t.Status != TechnologyStatus.Removed).Take(maxTechnologies).Select(TechnologyDto.From).ToList(), a.Observed.DetectedTechnologies.Count,
        a.Observed.Evidence.Count, a.Observed.Evidence.Select(e => e.EvidenceId).ToList(), a.Observed.SafetyFindingIds.Count, a.Observed.LastAnalyzedAt, markdown,
        new JsonObject { ["actor"] = a.Provenance.Actor, ["actorType"] = a.Provenance.ActorType, ["source"] = a.Provenance.Source, ["timestamp"] = a.Provenance.Timestamp.ToString("O"), ["correlationId"] = a.Provenance.CorrelationId });
}
