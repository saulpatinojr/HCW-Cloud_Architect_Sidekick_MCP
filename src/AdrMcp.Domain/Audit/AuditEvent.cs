namespace AdrMcp.Domain.Audit;

/// <summary>Audit record. Never contains tool arguments verbatim, secrets, or file contents.</summary>
public sealed record AuditEvent
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string CorrelationId { get; init; }
    public required string ToolName { get; init; }
    /// <summary>Hashed principal identifier; the raw identity is kept out of the audit container.</summary>
    public required string PrincipalHash { get; init; }
    public required string Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? Summary { get; init; }
    public long DurationMs { get; init; }
    public int? Ttl { get; init; }
    public string PartitionKey => $"{TenantId}|{WorkspaceId}";
}
