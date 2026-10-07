namespace AdrMcp.Domain.Memory;

/// <summary>Decision-memory entry recorded per tool call. Contains keywords and references only, never raw content or secrets.</summary>
public sealed record MemoryRecord
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ToolName { get; init; }
    public required string PrincipalHash { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyList<string> AdrIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EvidenceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FindingIds { get; init; } = Array.Empty<string>();
    public string? Summary { get; init; }
    public string? HumanFeedback { get; init; }
    public int? Ttl { get; init; }
    public string PartitionKey => $"{TenantId}|{WorkspaceId}";
}

/// <summary>Explainable retrieval result.</summary>
public sealed record MemoryHit(MemoryRecord Record, double Score, IReadOnlyList<string> MatchedTerms, string Explanation);
