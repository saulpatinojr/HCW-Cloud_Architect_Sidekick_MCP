namespace AdrMcp.Domain.Common;

/// <summary>Who or what produced a value, when, and under which correlation id.</summary>
public sealed record Provenance(
    string Actor,
    string ActorType,
    DateTimeOffset Timestamp,
    string CorrelationId,
    string? AnalyzerName = null,
    string? AnalyzerVersion = null,
    string? Source = null)
{
    public const string ActorTypeHuman = "human";
    public const string ActorTypeSystem = "system";
    public const string ActorTypeAgent = "agent";

    public static Provenance System(string analyzerName, string analyzerVersion, DateTimeOffset now, string correlationId, string? source = null)
        => new("system", ActorTypeSystem, now, correlationId, analyzerName, analyzerVersion, source);

    public static Provenance Human(string principalId, DateTimeOffset now, string correlationId, string? source = null)
        => new(principalId, ActorTypeHuman, now, correlationId, Source: source);
}
