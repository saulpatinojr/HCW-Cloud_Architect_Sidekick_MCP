# CLAUDE.md — project instructions for Claude Code

This repository is an **ADR Intelligence MCP Server** on Azure Functions (.NET 10 isolated, MCP extension 1.6.0). Read `README.md` first, then `docs/`.

## Non-negotiables
- Human-owned ADR fields (title, context, decision, rationale, alternatives, consequences, status, owners, approvers, approvalResult, humanNotes, tags, claims, supersedes, supersededBy) are never written by analyzers. Any code path that writes them must go through `AdrService.ApplyHumanChangeAsync` with a human provenance. Tests in `HumanOwnedFieldProtectionTests` must keep passing.
- Tenant and workspace come from the resolved principal (`ITenantResolver`), never from tool arguments.
- No secrets in code, docs, tests or logs. Use `local.settings.json` (git-ignored) locally, Key Vault references in Azure, managed identity everywhere. If you find a hard-coded credential, flag it instead of working around it.
- Every MCP tool is declared once in `src/AdrMcp.Application/Tools/ToolCatalog.cs`. After changing it, run `dotnet run --project tools/AdrMcp.DocGen -- docs/03-mcp-contracts.md` and the contract tests.
- Deterministic analyzers first; anything inferred is capped at confidence 0.6 and labelled `Inferred`.

## How to talk to me
These rules apply to your replies to me. Code comments, commit messages and `docs/` keep the conventions below.

**Write for a junior admin**: smart and willing, but new to Azure, .NET and MCP.
- Plain words first. Say what something is and why it matters before how it works. Explain jargon (ADR, MCP, RBAC, managed identity, what-if) in a short clause the first time it appears in a reply.
- Short sentences, concrete examples, no unexplained acronyms. Lead with the answer, then the reasoning.
- For each step or change, say what to expect when it works (the output or behavior I should see).
- Recommend one option and give the reason in a line. Don't hand me a menu of equal choices.

**When I have to do something** (portal click, command under my own credentials, approval, secret, setting):
- Say so up front, and say what is blocked until I do it.
- Use numbered steps, one action each. Give the exact command or menu path, and where to run it (which terminal, which folder, which portal page).
- Link the source for each action: the official docs page or the repo file (`README.md`, `docs/...`). Only use links you are sure exist. If you aren't sure, name the page to search for instead of guessing a URL.
- Never ask me to paste a secret into chat. Tell me where it belongs (`local.settings.json`, Key Vault).
- End with how to check it worked and how to undo it if it didn't.

Example of the shape I want:
> **I need you to start the storage emulator** (the local tests can't run without it).
> 1. Open a terminal in the repo root.
> 2. Run `azurite --silent --location .azurite --skipApiVersionCheck`.
> 3. You should see a line saying the Blob service is listening on port 10000. Leave the terminal open.
> Reference: [Azurite emulator](https://learn.microsoft.com/azure/storage/common/storage-use-azurite). To undo: press Ctrl+C in that terminal.

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
