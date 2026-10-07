using AdrMcp.Domain.Common;

namespace AdrMcp.Domain.Adrs;

public enum TechnologyStatus { Observed, Inferred, Declared, Removed, Uncertain }

public enum TechnologyCategory
{
    Language, Framework, Library, Database, Messaging, Container, Orchestration, InfrastructureAsCode,
    CloudService, CiCd, Identity, Observability, Runtime, Tooling, Other
}

/// <summary>System-managed observation of a technology. Never edited by humans directly; refreshed by detectors.</summary>
public sealed record DetectedTechnology(
    string NormalizedName,
    string? DetectedVersion,
    TechnologyCategory Category,
    string SourceFile,
    string? EvidenceExcerpt,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    Confidence Confidence,
    string DetectorName,
    string DetectorVersion,
    TechnologyStatus Status,
    string? EvidenceId = null)
{
    public string Key => $"{NormalizedName}|{SourceFile}".ToLowerInvariant();
}
