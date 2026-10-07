# Changelog

All notable changes to this project are documented here. The format follows Keep a Changelog and the project uses Semantic Versioning.

## [Unreleased]

### Added
- Agent pack for Claude Code (`.claude/agents/`) and GitHub Copilot (`.github/agents/`).
- Cloud-provider MCP server configurations (Azure, AWS, Google Cloud, Terraform, GitHub) in `.mcp.json`, `.vscode/mcp.json` and `.github/mcp/`.
- Repository community files: CONTRIBUTING, CODE_OF_CONDUCT, SUPPORT, SECURITY, issue and PR templates, CODEOWNERS, CodeQL, labeler and stale workflows.

## [1.0.0] - 2026-10-07

### Added
- ADR Intelligence MCP Server on Azure Functions Flex Consumption (.NET 8 isolated, MCP extension 1.6.0) with 27 implemented tools.
- Cosmos DB (serverless) + Blob + Queue persistence with ETag concurrency, version history and tenant/workspace partitioning.
- Human-owned vs system-managed field separation with propose → approve workflow.
- Direct-to-Blob upload flow with quarantine, hashing, MIME sniffing and deterministic content safety.
- Technology detectors (8), drift detectors (3), contradiction rules (7), safety-review rules (12), decision memory, audit.
- Bicep infrastructure, azd configuration, OIDC-based CI/CD, MCP protocol smoke test, documentation set A–L.
