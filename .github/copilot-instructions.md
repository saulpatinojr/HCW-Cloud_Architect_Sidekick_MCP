# GitHub Copilot instructions

This repository is an ADR Intelligence **MCP server** on Azure Functions (.NET 10 isolated, Azure Functions MCP extension). Follow `CLAUDE.md` for conventions; the same rules apply to Copilot.

- Never write code that modifies human-owned ADR fields outside `AdrService.ApplyHumanChangeAsync`; analyzers only update `ObservedState`.
- Tenant and workspace always come from the resolved principal, never from tool arguments.
- No secrets, keys, SAS tokens or connection strings in code, tests, docs or workflows. Use placeholders and Key Vault references.
- When adding or changing an MCP tool: update `ToolCatalog`, the DTO, the handler, the `[McpToolTrigger]` function, `TestHost.Dispatch`, tests, and regenerate `docs/03-mcp-contracts.md` with `tools/AdrMcp.DocGen`.
- Deterministic analyzers first; anything inferred is labelled `Inferred` with confidence ≤ 0.6.
- Tests: xunit with plain asserts; use `TestHost` for end-to-end tool tests.
- Build/test: `dotnet build AdrMcp.sln -c Release`, `dotnet test AdrMcp.sln`; infra: `bicep build infra/main.bicep` (ADR server) and `bicep build infra/vault/main.bicep` (vault server).
