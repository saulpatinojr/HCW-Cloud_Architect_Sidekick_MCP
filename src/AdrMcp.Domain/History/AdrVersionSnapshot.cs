using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;

namespace AdrMcp.Domain.History;

/// <summary>Immutable snapshot taken before every change to an ADR (human or observed).</summary>
public sealed record AdrVersionSnapshot
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AdrId { get; init; }
    public required int HumanRevision { get; init; }
    public required int ObservedRevision { get; init; }
    public required string ChangeKind { get; init; }
    public required HumanOwnedContent Human { get; init; }
    public required ObservedState Observed { get; init; }
    public required string ContentHash { get; init; }
    public required Provenance Provenance { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public string? Summary { get; init; }
    public string PartitionKey => $"{TenantId}|{WorkspaceId}";

    public static string BuildId(string adrId, int humanRevision, int observedRevision) => $"{adrId}:h{humanRevision}.o{observedRevision}";
}
