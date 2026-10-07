using AdrMcp.Domain.Common;

namespace AdrMcp.Domain.Findings;

public enum FindingSeverity { Info, Low, Medium, High, Critical }

/// <summary>Human classification of a system-generated finding. The system never sets Confirmed/FalsePositive itself.</summary>
public enum FindingReviewStatus { Open, Confirmed, AcceptableException, FalsePositive, Resolved, Superseded }

public enum FindingKind { Contradiction, Drift, Safety, Governance }

public enum DriftClaimResult { Compliant, Drifted, Uncertain, Unsupported, NotApplicable }

/// <summary>Base finding persisted in the findings container. Findings are append-only; later scans create new versions.</summary>
public abstract record Finding
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AdrId { get; init; }
    public required FindingKind Kind { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required Confidence Confidence { get; init; }
    public required string Summary { get; init; }
    public string? Explanation { get; init; }
    public IReadOnlyList<EvidenceItem> Evidence { get; init; } = Array.Empty<EvidenceItem>();
    public FindingReviewStatus ReviewStatus { get; init; } = FindingReviewStatus.Open;
    public string? ReviewedBy { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? ReviewNote { get; init; }
    public required Provenance Provenance { get; init; }
    /// <summary>Stable key so re-scans can detect status changes for the same logical finding.</summary>
    public required string FingerprintKey { get; init; }
    public string? PreviousFindingId { get; init; }
    public bool RequiresHumanReview { get; init; } = true;
}

public sealed record ContradictionFinding : Finding
{
    public required string OtherAdrId { get; init; }
    public required string ClaimA { get; init; }
    public required string ClaimB { get; init; }
    public required string ContradictionType { get; init; }
    public bool NeedsSemanticReview { get; init; }
}

public sealed record DriftFinding : Finding
{
    public required string ClaimId { get; init; }
    public required string ClaimText { get; init; }
    public required DriftClaimResult Result { get; init; }
    public DriftClaimResult? PreviousResult { get; init; }
    public required string DetectorName { get; init; }
    public required string DetectorVersion { get; init; }
    public bool StatusChanged => PreviousResult is not null && PreviousResult != Result;
}

public sealed record SafetyFinding : Finding
{
    public required string Category { get; init; }
    public IReadOnlyList<string> AffectedQualityAttributes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SuggestedMitigations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AlternativesToConsider { get; init; } = Array.Empty<string>();
    public bool PolicyBlocksAcceptance { get; init; }
    public string? PolicyId { get; init; }
    /// <summary>Separates what was observed from what was inferred and recommended.</summary>
    public string? ObservedEvidence { get; init; }
    public string? Inference { get; init; }
    public string? Recommendation { get; init; }
}

public sealed record GovernanceFinding : Finding
{
    public required string RuleId { get; init; }
    public required string Result { get; init; }
}
