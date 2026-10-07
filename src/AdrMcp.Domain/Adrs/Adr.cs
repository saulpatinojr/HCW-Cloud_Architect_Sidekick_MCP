using AdrMcp.Domain.Common;
using AdrMcp.Domain.Errors;

namespace AdrMcp.Domain.Adrs;

/// <summary>
/// Human-owned content. Only changed through an explicit human action (adr_create, adr_update by an author with
/// permission, or adr_approve_change). Automated analysis has no write path to this record.
/// </summary>
public sealed record HumanOwnedContent
{
    public required string Title { get; init; }
    public string Context { get; init; } = "";
    public string Decision { get; init; } = "";
    public string Rationale { get; init; } = "";
    public IReadOnlyList<string> Alternatives { get; init; } = Array.Empty<string>();
    public string Consequences { get; init; } = "";
    public AdrStatus Status { get; init; } = AdrStatus.Draft;
    public IReadOnlyList<string> Owners { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Approvers { get; init; } = Array.Empty<string>();
    public string? ApprovalResult { get; init; }
    public string? HumanNotes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DecisionClaim> Claims { get; init; } = Array.Empty<DecisionClaim>();
    /// <summary>Supersession is a human choice.</summary>
    public string? Supersedes { get; init; }
    public string? SupersededBy { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
}

/// <summary>Names of human-owned fields as exposed through MCP. Used by the proposal workflow and by tests.</summary>
public static class HumanOwnedFields
{
    public const string Title = "title";
    public const string Context = "context";
    public const string Decision = "decision";
    public const string Rationale = "rationale";
    public const string Alternatives = "alternatives";
    public const string Consequences = "consequences";
    public const string Status = "status";
    public const string Owners = "owners";
    public const string Approvers = "approvers";
    public const string ApprovalResult = "approvalResult";
    public const string HumanNotes = "humanNotes";
    public const string Tags = "tags";
    public const string Claims = "claims";
    public const string Supersedes = "supersedes";
    public const string SupersededBy = "supersededBy";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Title, Context, Decision, Rationale, Alternatives, Consequences, Status, Owners, Approvers, ApprovalResult, HumanNotes, Tags, Claims, Supersedes, SupersededBy
    };

    public static bool IsHumanOwned(string field) => All.Contains(field);
}

/// <summary>System-managed observations. Refreshed by analyzers; history preserved via snapshots and findings.</summary>
public sealed record ObservedState
{
    public IReadOnlyList<DetectedTechnology> DetectedTechnologies { get; init; } = Array.Empty<DetectedTechnology>();
    public IReadOnlyList<LinkedFile> LinkedFiles { get; init; } = Array.Empty<LinkedFile>();
    public IReadOnlyList<string> InfrastructureResourceRefs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ArchitecturePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = Array.Empty<EvidenceReference>();
    /// <summary>Ids of open findings, by kind. Full findings live in the findings container.</summary>
    public IReadOnlyList<string> ContradictionFindingIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DriftFindingIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SafetyFindingIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GovernanceFindingIds { get; init; } = Array.Empty<string>();
    public string DriftStatus { get; init; } = "NotScanned";
    public Confidence? Confidence { get; init; }
    public DateTimeOffset? LastAnalyzedAt { get; init; }
    public string? AnalyzerVersion { get; init; }
    public Provenance? LastProvenance { get; init; }
}

/// <summary>Aggregate root persisted in the adrs container.</summary>
public sealed class Adr
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? RepositoryId { get; init; }
    public required int AdrNumber { get; init; }
    public required HumanOwnedContent Human { get; set; }
    public ObservedState Observed { get; set; } = new();
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int HumanRevision { get; set; } = 1;
    public int ObservedRevision { get; set; } = 0;
    public required Provenance Provenance { get; set; }
    /// <summary>SHA-256 of the canonical human-owned content; proves no silent edit occurred.</summary>
    public string ContentHash { get; set; } = "";
    /// <summary>Storage ETag for optimistic concurrency. Null for unsaved instances.</summary>
    public string? ETag { get; set; }
    public string? SourceMarkdownBlobPath { get; set; }
    public bool IsDeleted { get; set; }

    public string PartitionKey => BuildPartitionKey(TenantId, WorkspaceId);
    public static string BuildPartitionKey(string tenantId, string workspaceId) => $"{tenantId}|{workspaceId}";

    // Convenience projections
    public string Title => Human.Title;
    public AdrStatus Status => Human.Status;

    /// <summary>Apply a human-authored change. Caller must have already authorized the actor.</summary>
    public void ApplyHumanChange(HumanOwnedContent updated, Provenance provenance, Func<HumanOwnedContent, string> hash)
    {
        if (provenance.ActorType != Provenance.ActorTypeHuman)
            throw new HumanOwnedFieldException("*");
        if (updated.Status != Human.Status) AdrLifecycle.EnsureTransition(Human.Status, updated.Status);
        if (updated.Status == AdrStatus.Accepted && updated.AcceptedAt is null)
            updated = updated with { AcceptedAt = provenance.Timestamp };
        Human = updated;
        HumanRevision++;
        UpdatedAt = provenance.Timestamp;
        Provenance = provenance;
        ContentHash = hash(Human);
    }

    /// <summary>Apply a system-managed refresh. Human content is untouched by construction.</summary>
    public void ApplyObservedUpdate(ObservedState updated, Provenance provenance)
    {
        Observed = updated with { LastProvenance = provenance, LastAnalyzedAt = provenance.Timestamp, AnalyzerVersion = provenance.AnalyzerVersion ?? Observed.AnalyzerVersion };
        ObservedRevision++;
        UpdatedAt = provenance.Timestamp;
    }
}
