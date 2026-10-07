namespace AdrMcp.Domain.Common;

/// <summary>How a fact or finding was established. Drives the confidence ceiling.</summary>
public enum EvidenceBasis
{
    /// <summary>Deterministically parsed from a file or resource export.</summary>
    Observed,
    /// <summary>Declared by a human in the ADR itself.</summary>
    Declared,
    /// <summary>Inferred heuristically or by a model. Always lower confidence.</summary>
    Inferred,
    /// <summary>Evidence was contradictory or insufficient.</summary>
    Uncertain
}

/// <summary>A bounded confidence score with an explanation of its basis.</summary>
public sealed record Confidence(double Score, EvidenceBasis Basis, string? Rationale = null)
{
    public const double InferredCeiling = 0.6;

    public static Confidence Observed(double score, string? rationale = null) => new(Clamp(score), EvidenceBasis.Observed, rationale);
    public static Confidence Declared(double score, string? rationale = null) => new(Clamp(score), EvidenceBasis.Declared, rationale);
    /// <summary>Inferred confidence is capped so model/heuristic output can never look certain.</summary>
    public static Confidence Inferred(double score, string? rationale = null) => new(Math.Min(Clamp(score), InferredCeiling), EvidenceBasis.Inferred, rationale);
    public static Confidence Uncertain(string? rationale = null) => new(0.2, EvidenceBasis.Uncertain, rationale);

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
