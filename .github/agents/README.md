# GitHub Copilot custom agents

Repository-level custom agents for GitHub Copilot (`*.agent.md`, YAML frontmatter + instructions). They mirror the Claude Code agent pack in `.claude/agents/` so both assistants follow the same guardrails: never edit human-owned ADR fields directly, tenant from principal only, no secrets, deterministic analysis first, read-only cloud access.

| Agent | Purpose |
|---|---|
| `adr-author` | Draft MADR-style ADRs with claims; file as Draft/Proposed |
| `drift-investigator` | Technology refresh + drift detection, evidence-backed explanations |
| `contradiction-reviewer` | Semantic review of contradiction findings; recommends classification |
| `security-reviewer` | Read-only security review (secrets, SAS scope, isolation, redaction, Bicep hardening) |
| `bicep-infra-reviewer` | Bicep build/lint/what-if review |
| `mcp-contract-guardian` | Keeps ToolCatalog, handlers, triggers, docs and tests in sync |
| `dotnet-test-engineer` | xunit tests with the in-memory TestHost |
| `release-engineer` | Release checklist, previews, smoke tests; no unapproved deploys |
| `docs-keeper` | README/docs/CHANGELOG consistency |
| `multi-cloud-architect` | Read-only evidence from Azure, AWS and Google Cloud MCP servers (`.github/mcp/`) |

MCP servers for the Copilot coding agent are configured in repository settings (not read from files); copy the JSON from `.github/mcp/*.json`.
