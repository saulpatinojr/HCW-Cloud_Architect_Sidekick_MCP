namespace AdrMcp.Domain.Common;

/// <summary>A pointer to evidence that supports a fact or finding. Excerpts are always sanitized before storage.</summary>
public sealed record EvidenceItem(
    string EvidenceId,
    string SourcePath,
    string? Excerpt,
    string? ContentHash,
    int? LineNumber = null,
    string? Kind = null);
