using AdrMcp.Domain.Common;

namespace AdrMcp.Domain.Proposals;

public enum ProposalStatus { Proposed, Approved, Rejected, Applied, Expired, Withdrawn }

/// <summary>A proposed change to one human-owned field. The new value is applied only after explicit human approval.</summary>
public sealed record FieldChange(
    string Field,
    string? CurrentValueHash,
    string ProposedValue,
    string Reason,
    IReadOnlyList<EvidenceItem> Evidence);

public sealed record ChangeProposal
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AdrId { get; init; }
    public required int BaseHumanRevision { get; init; }
    public required IReadOnlyList<FieldChange> Changes { get; init; }
    public required string ProposedBy { get; init; }
    public required string ProposerType { get; init; }
    public required DateTimeOffset ProposedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public ProposalStatus Status { get; init; } = ProposalStatus.Proposed;
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecisionNote { get; init; }
    public Confidence? Confidence { get; init; }
    public string? ETag { get; init; }
    public string PartitionKey => $"{TenantId}|{WorkspaceId}";
}
