using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Domain.Tenancy;

namespace AdrMcp.Application.Tools;

public enum ToolPhase { Mvp, Extended, Later }

public enum IdempotencyMode { ReadOnly, Key, Natural }

public sealed record ToolDefinition(
    string Name,
    string Description,
    Permission Permission,
    bool Mutating,
    IdempotencyMode Idempotency,
    ToolPhase Phase,
    bool Implemented,
    JsonObject InputSchema,
    JsonObject OutputSchema,
    IReadOnlyList<string> FailureCodes)
{
    public string InputSchemaJson => InputSchema.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    public string OutputSchemaJson => OutputSchema.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
