# Changelog

All notable changes to this project are documented here. The format follows Keep a Changelog and the project uses Semantic Versioning.

## [Unreleased]

### Added
- Agent pack for Claude Code (`.claude/agents/`) and GitHub Copilot (`.github/agents/`).
- Cloud-provider MCP server configurations (Azure, AWS, Google Cloud, Terraform, GitHub) in `.mcp.json`, `.vscode/mcp.json` and `.github/mcp/`.
- Repository community files: CONTRIBUTING, CODE_OF_CONDUCT, SUPPORT, SECURITY, issue and PR templates, CODEOWNERS, CodeQL, labeler and stale workflows.

### Changed
- Deploy is explicit opt-in: it runs only from `main` when the repository variable `ADR_DEPLOY_ENABLED` is `true`, with a preflight check for the Azure variables; otherwise pushes to `main` emit a `Deploy skipped` notice. The manual-dispatch environment is a fixed choice (no free-text typos creating unprotected environments). Deploy has per-environment concurrency (no cancel-in-progress); both jobs have timeouts.
- Test results (TRX + coverage) are uploaded as the `test-results` artifact; `dorny/test-reporter` was removed because it needs `checks: write`, which fork pull requests never get.
- Removed the stale DocGen copy from the unit-test assembly (`tests/AdrMcp.UnitTests/tools/`); `tools/AdrMcp.DocGen` is the only generator.
- Consolidated the duplicate root `adr/` folder into `docs/adr/`, the only ADR location.
- GitHub Actions moved to their Node 24 majors (checkout v7, setup-dotnet v6, upload-artifact v7, gitleaks-action v3, azure/login v3, labeler v7, stale v11, codeql-action v4 — v3 is deprecated in December 2026). Dependabot now groups action updates into one weekly PR.
- Dependabot ignores Microsoft.ApplicationInsights.WorkerService 3.x (it removes types that Microsoft.Azure.Functions.Worker.ApplicationInsights 2.x loads, so the worker would crash at startup) and never updates the `samples/evidence` demo fixture.
- Removed the unused NSubstitute test dependency.

### Fixed
- CI was red since the initial commit: Azure.Storage.Blobs 12.30.1 sends API version 2026-10-06, which Azurite rejects. CI now runs Azurite 3.37.0 with `--skipApiVersionCheck` and sets `ADR_TEST_REQUIRE_AZURITE=true`, so Azurite tests fail instead of skipping. Local instructions add the same flag.
- Repository links (badges, clone instructions, issue-template contact links) now point to `saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP`.

### Security
- Replaced the broken `dotnet list package --vulnerable` step (it printed `MSB0001`, exited 0 and could never fail) with NuGet Audit in `Directory.Build.props` (`NuGetAuditMode=all`, `NuGetAuditLevel=low`). In CI, NU1901–NU1904 are errors, so restore fails on any known vulnerable direct or transitive package.
- Least-privilege workflow token permissions: `{}` at workflow level; `build-test` gets `contents: read` and `pull-requests: read`, `deploy` gets `contents: read` and `id-token: write`.
- Non-GitHub actions (gitleaks-action, Azure/setup-azd, azure/login) are pinned to commit SHAs; the deploy job can mint Azure tokens. Gitleaks now runs before any pull-request code. NuGet Audit fails closed when vulnerability data is unavailable (NU1900/NU1905), and a new CI step scans the Functions host-extension packages (generated `WorkerExtensions` project), which NuGet Audit settings never reach.
- The smoke test reads the function key from `ADR_MCP_KEY` so it stays off the command line; Azurite in CI binds to `127.0.0.1` and readiness waits for an HTTP answer.

## [1.0.0] - 2026-10-07

### Added
- ADR Intelligence MCP Server on Azure Functions Flex Consumption (.NET 8 isolated, MCP extension 1.6.0) with 27 implemented tools.
- Cosmos DB (serverless) + Blob + Queue persistence with ETag concurrency, version history and tenant/workspace partitioning.
- Human-owned vs system-managed field separation with propose → approve workflow.
- Direct-to-Blob upload flow with quarantine, hashing, MIME sniffing and deterministic content safety.
- Technology detectors (8), drift detectors (3), contradiction rules (7), safety-review rules (12), decision memory, audit.
- Bicep infrastructure, azd configuration, OIDC-based CI/CD, MCP protocol smoke test, documentation set A–L.
