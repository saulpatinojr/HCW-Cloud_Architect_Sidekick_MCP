namespace AdrMcp.Domain.Adrs;

/// <summary>
/// Kinds of testable claims a decision can make. Deterministic drift and contradiction detectors operate on these.
/// </summary>
public enum ClaimKind
{
    ProhibitedTechnology,
    RequiredTechnology,
    InfrastructureRequirement,
    DatabaseOwnership,
    IntegrationStyle,
    CloudProvider,
    AuthStrategy,
    DataResidency,
    DataRetention,
    Other
}

/// <summary>
/// A structured, human-declared claim. Claims are part of the human-owned Decision and are never generated silently;
/// the analyzer may EXTRACT candidate claims from decision text, but those are stored as observed/inferred with lower confidence.
/// </summary>
public sealed record DecisionClaim(
    string ClaimId,
    ClaimKind Kind,
    string Subject,
    string? Predicate,
    string? Object,
    ClaimScope Scope,
    string? SourceText = null,
    bool Declared = true)
{
    /// <summary>Normalized subject for matching (lower-case, trimmed).</summary>
    public string NormalizedSubject => Normalize(Subject);
    public string? NormalizedObject => Object is null ? null : Normalize(Object);

    public static string Normalize(string s) => string.Join(' ', s.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Where a claim applies. Two claims only conflict when their scopes overlap.</summary>
public sealed record ClaimScope(
    string? System = null,
    string? BoundedContext = null,
    string? Environment = null,
    DateTimeOffset? EffectiveFrom = null,
    DateTimeOffset? EffectiveTo = null)
{
    public static readonly ClaimScope Global = new();

    public bool Overlaps(ClaimScope other)
    {
        if (!DimensionOverlaps(System, other.System)) return false;
        if (!DimensionOverlaps(BoundedContext, other.BoundedContext)) return false;
        if (!DimensionOverlaps(Environment, other.Environment)) return false;
        var from = Max(EffectiveFrom, other.EffectiveFrom);
        var to = Min(EffectiveTo, other.EffectiveTo);
        return from is null || to is null || from <= to;
    }

    private static bool DimensionOverlaps(string? a, string? b)
        => a is null || b is null || string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : (a > b ? a : b);
    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : (a < b ? a : b);
}
