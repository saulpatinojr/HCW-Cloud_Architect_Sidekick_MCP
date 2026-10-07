# Agent pack

Project-level Claude Code subagents tailored to this repository. Each file is `name.md` with YAML frontmatter; Claude delegates automatically based on `description`, or you invoke one explicitly ("use the security-reviewer agent on this diff"). GitHub Copilot equivalents live in `.github/agents/*.agent.md`.

| Agent | Purpose | Writes code? |
|---|---|---|
| `adr-author` | Drafts MADR-style ADRs with testable claims and files them through the ADR MCP tools as Draft/Proposed; never accepts | no (uses MCP tools) |
| `drift-investigator` | Runs technology refresh and drift detection, interprets evidence, proposes (never applies) ADR updates | no |
| `contradiction-reviewer` | Reviews `needsSemanticReview` contradiction findings and recommends a classification for a human | no |
| `security-reviewer` | Read-only review for secrets, SAS scope, tenant isolation, input validation, prompt-injection surfaces | no |
| `bicep-infra-reviewer` | Validates Bicep, runs what-if, checks least privilege and private networking | no |
| `mcp-contract-guardian` | Keeps `ToolCatalog`, handlers, Functions triggers, docs and contract tests in sync | yes |
| `dotnet-test-engineer` | Adds or repairs xunit tests, keeps the suite green | yes |
| `release-engineer` | Drives CI, azd provision/deploy, smoke tests; plan mode for production | no (plans + commands) |
| `docs-keeper` | Keeps README/docs consistent with code and the roadmap | yes (docs only) |
| `multi-cloud-architect` | Gathers read-only evidence from Azure, AWS and Google Cloud via their MCP servers and turns it into ADR claims/evidence | no |

All agents inherit `CLAUDE.md` and must respect the non-negotiables there. Cloud MCP servers referenced by `multi-cloud-architect` are defined in `.mcp.json` and documented in `.github/mcp/README.md`.
