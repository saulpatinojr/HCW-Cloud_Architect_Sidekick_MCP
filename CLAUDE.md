# CLAUDE.md — project instructions for Claude Code

This repository is an **ADR Intelligence MCP Server** on Azure Functions (.NET 10 isolated, MCP extension 1.6.0). Read `README.md` first, then `docs/`.

## Non-negotiables
- Human-owned ADR fields (title, context, decision, rationale, alternatives, consequences, status, owners, approvers, approvalResult, humanNotes, tags, claims, supersedes, supersededBy) are never written by analyzers. Any code path that writes them must go through `AdrService.ApplyHumanChangeAsync` with a human provenance. Tests in `HumanOwnedFieldProtectionTests` must keep passing.
- Tenant and workspace come from the resolved principal (`ITenantResolver`), never from tool arguments.
- No secrets in code, docs, tests or logs. Use `local.settings.json` (git-ignored) locally, Key Vault references in Azure, managed identity everywhere. If you find a hard-coded credential, flag it instead of working around it.
- Every MCP tool is declared once in `src/AdrMcp.Application/Tools/ToolCatalog.cs`. After changing it, run `dotnet run --project tools/AdrMcp.DocGen -- docs/03-mcp-contracts.md` and the contract tests.
- Deterministic analyzers first; anything inferred is capped at confidence 0.6 and labelled `Inferred`.

## Build, test, run
```bash
dotnet build AdrMcp.sln -c Release
dotnet test AdrMcp.sln
cp src/AdrMcp.Functions/local.settings.json.example src/AdrMcp.Functions/local.settings.json   # once
azurite --silent --location .azurite --skipApiVersionCheck &   # Blob/Queue emulator (SDK API version is newer than Azurite's)
(cd src/AdrMcp.Functions && func start --port 7071)
python3 scripts/smoke-test.py --url http://localhost:7071/runtime/webhooks/mcp --write
bicep build infra/main.bicep --stdout > /dev/null && bicep lint infra/main.bicep
```

## Layout
`src/AdrMcp.Domain` (entities, lifecycle) → `src/AdrMcp.Application` (services, abstractions, ToolCatalog, pipeline, handlers) → `src/AdrMcp.Infrastructure` (Cosmos, Blob, Queue, tenancy, in-memory doubles) and `src/AdrMcp.Analysis` (safety scanners, technology/drift detectors, contradiction and safety rules) → `src/AdrMcp.Functions` (MCP triggers, queue worker, timer, health). Tests in `tests/`, IaC in `infra/`, docs in `docs/`.

## Conventions
- C# 12, file-scoped namespaces, nullable enabled (nullable warnings are errors), records for immutable data, `AppJson.Options` for all JSON.
- Add a technology detector by implementing `ITechnologyDetector`; a drift check by implementing `IDriftDetector`; a contradiction rule by `IContradictionRule`; a safety rule by `ISafetyReviewRule`. Register in `AdrMcp.Analysis/ServiceCollectionExtensions.cs`. Nothing else changes.
- Keep tool outputs compact (summaries, truncated excerpts) to protect the client context window.
- Commit messages: imperative, scoped (`analysis: add helm values detector`). Do not commit `local.settings.json`, `.azure/`, `dist/`.

## Agents
Specialised subagents live in `.claude/agents/`. Use `adr-author` to draft ADRs, `drift-investigator` and `contradiction-reviewer` for analysis, `security-reviewer` and `bicep-infra-reviewer` before merging, `mcp-contract-guardian` after touching tools, `release-engineer` for deployments, `multi-cloud-architect` when evidence from Azure, AWS or Google Cloud is needed.
