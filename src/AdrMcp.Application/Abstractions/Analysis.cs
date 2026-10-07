using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Findings;

namespace AdrMcp.Application.Abstractions;

/// <summary>A piece of evidence text available to analyzers. Always the sanitized variant.</summary>
public sealed record EvidenceFile(string EvidenceId, string Path, string MediaType, string Text, string Sha256, string? AdrId);

public sealed class EvidenceSet
{
    public EvidenceSet(IReadOnlyList<EvidenceFile> files) => Files = files;
    public IReadOnlyList<EvidenceFile> Files { get; }
    public IEnumerable<EvidenceFile> Where(Func<string, bool> pathPredicate) => Files.Where(f => pathPredicate(f.Path));
}

/// <summary>Deterministic technology detector plugin. Pure function of (path, content).</summary>
public interface ITechnologyDetector
{
    string Name { get; }
    string Version { get; }
    bool CanHandle(string path);
    IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now);
}

public sealed record DriftEvaluation(
    DriftClaimResult Result,
    Confidence Confidence,
    string Explanation,
    IReadOnlyList<EvidenceItem> Evidence);

/// <summary>Drift detector plugin. Evaluates one claim against an evidence set. Adding detectors never changes AdrService.</summary>
public interface IDriftDetector
{
    string Name { get; }
    string Version { get; }
    bool Supports(DecisionClaim claim);
    DriftEvaluation Evaluate(DecisionClaim claim, EvidenceSet evidence);
}

/// <summary>Extracts candidate claims from free-text decision sections. Output is marked Declared=false (inferred).</summary>
public interface IClaimExtractor
{
    IReadOnlyList<DecisionClaim> Extract(Adr adr);
}

public sealed record ContradictionCandidate(Adr A, DecisionClaim ClaimA, Adr B, DecisionClaim ClaimB, string ContradictionType, string DeterministicExplanation, double DeterministicScore);

/// <summary>
/// Optional semantic second-pass over deterministic candidate pairs only. Default implementation is "none":
/// the MCP client (Claude) is asked to review flagged pairs and a human classifies the result.
/// </summary>
public interface ISemanticContradictionAnalyzer
{
    bool IsEnabled { get; }
    Task<(bool IsContradiction, double Confidence, string Explanation)?> AnalyzeAsync(ContradictionCandidate candidate, CancellationToken ct);
}

public interface IEvidenceSetProvider
{
    Task<EvidenceSet> LoadForWorkspaceAsync(string tenantId, string workspaceId, string? adrId, int maxFiles, int maxBytesPerFile, CancellationToken ct);
}

/// <summary>Deterministic contradiction rule plugin. Produces candidate pairs only; it never marks an ADR invalid.</summary>
public interface IContradictionRule
{
    string Name { get; }
    IEnumerable<ContradictionCandidate> Match(Adr a, IReadOnlyList<DecisionClaim> claimsA, Adr b, IReadOnlyList<DecisionClaim> claimsB);
}

public sealed record SafetyReviewInput(Adr Adr, IReadOnlyList<Adr> OtherActiveAdrs, EvidenceSet? Evidence, DateTimeOffset Now);

/// <summary>Advisory safety-review rule plugin. Output is a recommendation unless an explicit policy marks the rule blocking.</summary>
public interface ISafetyReviewRule
{
    string RuleId { get; }
    string Category { get; }
    IEnumerable<SafetyFinding> Evaluate(SafetyReviewInput input, Func<string, SafetyFinding> create);
}

/// <summary>Organization policy: which safety rule ids hard-block acceptance. Set by Admins, never by analyzers.</summary>
public interface IPolicyProvider
{
    bool BlocksAcceptance(string ruleId);
    string? PolicyIdFor(string ruleId);
}
