namespace AdrMcp.Application.Common;

/// <summary>Uniform MCP tool response. Kept small so it does not exhaust the client context window.</summary>
public sealed record ToolError(string Code, string Message, bool Retryable, string CorrelationId, IReadOnlyDictionary<string, string>? Details = null);

public sealed record ToolMeta(string CorrelationId, string Tool, long DurationMs, IReadOnlyList<string>? Warnings = null, bool? IdempotentReplay = null);

public sealed record ToolEnvelope<T>(bool Ok, T? Data, ToolError? Error, ToolMeta Meta)
{
    public static ToolEnvelope<T> Success(T data, ToolMeta meta) => new(true, data, null, meta);
    public static ToolEnvelope<T> Failure(ToolError error, ToolMeta meta) => new(false, default, error, meta);
}
