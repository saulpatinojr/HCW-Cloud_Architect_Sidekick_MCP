// Generates docs/03-mcp-contracts.md from the ToolCatalog so documentation can never drift from the advertised schemas.
// Usage: dotnet run --project tools/AdrMcp.DocGen -- docs/03-mcp-contracts.md
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Application.Tools;

var output = args.Length > 0 ? args[0] : "docs/03-mcp-contracts.md";
var pretty = new JsonSerializerOptions { WriteIndented = true };
var sb = new StringBuilder();
sb.AppendLine("# F. MCP tool contracts");
sb.AppendLine();
sb.AppendLine("_Generated from `src/AdrMcp.Application/Tools/ToolCatalog.cs` by `tools/AdrMcp.DocGen`. Do not edit by hand._");
sb.AppendLine();
sb.AppendLine("Every tool returns a `ToolEnvelope`: `{ ok, data?, error?: { code, message, retryable, correlationId, details? }, meta: { correlationId, tool, durationMs, warnings?, idempotentReplay? } }` as the text content of the MCP `CallToolResult`. Outputs are intentionally compact (summaries, truncated excerpts, capped lists) to protect the client context window. `workspaceId` is optional everywhere and must be one the caller is authorized for; tenant identity is **never** taken from arguments. Enum values are PascalCase (e.g. `Accepted`, `Drifted`, `Inferred`).");
sb.AppendLine();
sb.AppendLine("## Overview");
sb.AppendLine();
sb.AppendLine("| Tool | Phase | Implemented | Mutating | Idempotency | Permission |");
sb.AppendLine("|---|---|---|---|---|---|");
foreach (var t in ToolCatalog.All)
    sb.AppendLine($"| `{t.Name}` | {t.Phase} | {(t.Implemented ? "yes" : "specified only")} | {(t.Mutating ? "mutating" : "read-only")} | {Idem(t.Idempotency)} | {t.Permission} |");
sb.AppendLine();
sb.AppendLine("Idempotency modes: **Key** = client-supplied `idempotencyKey`; same key + same payload replays the stored result (24 h), same key + different payload → `IDEMPOTENCY_CONFLICT`. **Natural** = the operation is idempotent by its own identifiers (e.g. registering the same upload twice returns the recorded outcome; a drift scan is a merge). **ReadOnly** = no side effects beyond audit/memory records.");
sb.AppendLine();
sb.AppendLine("Authorization roles: Reader ⊂ Author ⊂ Approver ⊂ Admin (see `RolePermissions`). In `EntraBuiltIn` mode roles come from Entra app roles `Adr.Reader`, `Adr.Author`, `Adr.Approver`, `Adr.Admin`; in `FunctionKey` mode from `Auth:StaticRoles`.");
sb.AppendLine();
foreach (var t in ToolCatalog.All)
{
    sb.AppendLine($"## `{t.Name}`");
    sb.AppendLine();
    sb.AppendLine($"**Purpose.** {t.Description}");
    sb.AppendLine();
    sb.AppendLine($"- Classification: **{(t.Mutating ? "mutating" : "read-only")}**, phase **{t.Phase}**, {(t.Implemented ? "implemented" : "**specified only (not exposed by the host until implemented)**")}");
    sb.AppendLine($"- Required permission: `{t.Permission}`");
    sb.AppendLine($"- Idempotency: {Idem(t.Idempotency)}");
    sb.AppendLine($"- Failure codes: {string.Join(", ", t.FailureCodes.Select(c => $"`{c}`"))}");
    sb.AppendLine();
    sb.AppendLine("<details><summary>Input schema</summary>");
    sb.AppendLine();
    sb.AppendLine("```json");
    sb.AppendLine(t.InputSchema.ToJsonString(pretty));
    sb.AppendLine("```");
    sb.AppendLine("</details>");
    sb.AppendLine();
    sb.AppendLine("<details><summary>Output schema</summary>");
    sb.AppendLine();
    sb.AppendLine("```json");
    sb.AppendLine(t.OutputSchema.ToJsonString(pretty));
    sb.AppendLine("```");
    sb.AppendLine("</details>");
    sb.AppendLine();
    if (Examples.Map.TryGetValue(t.Name, out var ex))
    {
        sb.AppendLine("**Example request (arguments)**");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonNode.Parse(ex.Request)!.ToJsonString(pretty));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("**Example response (`data`)**");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonNode.Parse(ex.Response)!.ToJsonString(pretty));
        sb.AppendLine("```");
        sb.AppendLine();
    }
}
File.WriteAllText(output, sb.ToString());
Console.WriteLine($"Wrote {output} ({ToolCatalog.All.Count} tools)");

static string Idem(IdempotencyMode m) => m switch { IdempotencyMode.Key => "Key", IdempotencyMode.Natural => "Natural", _ => "ReadOnly" };
