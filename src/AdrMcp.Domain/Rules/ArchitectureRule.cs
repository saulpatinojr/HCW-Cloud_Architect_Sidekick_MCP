using AdrMcp.Domain.Findings;

namespace AdrMcp.Domain.Rules;

/// <summary>
/// An executable fitness function derived from an accepted ADR constraint. Hard blocking requires an explicit policy flag
/// set by an Admin; analyzer recommendations are never blocking on their own.
/// </summary>
public sealed record ArchitectureRule
{
    public required string RuleId { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AdrId { get; init; }
    public required string Scope { get; init; }
    public required string Description { get; init; }
    public required FindingSeverity Severity { get; init; }
    /// <summary>Name of the registered evaluator (a drift detector or fitness evaluator plugin).</summary>
    public required string Evaluator { get; init; }
    public IReadOnlyDictionary<string, string> EvaluatorParameters { get; init; } = new Dictionary<string, string>();
    public required string EvidenceSource { get; init; }
    public bool PolicyBlocking { get; init; }
    public string? ExemptionProcess { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ETag { get; init; }
    public string PartitionKey => $"{TenantId}|{WorkspaceId}";
}

public enum RuleResult { Pass, Fail, Unknown }
