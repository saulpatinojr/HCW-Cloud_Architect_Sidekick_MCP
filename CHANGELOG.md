# Changelog

All notable changes to this project are documented here. The format follows Keep a Changelog and the project uses Semantic Versioning.

## [Unreleased]

### Added
- Agent pack for Claude Code (`.claude/agents/`) and GitHub Copilot (`.github/agents/`).
- Cloud-provider MCP server configurations (Azure, AWS, Google Cloud, Terraform, GitHub) in `.mcp.json`, `.vscode/mcp.json` and `.github/mcp/`.
- Repository community files: CONTRIBUTING, CODE_OF_CONDUCT, SUPPORT, SECURITY, issue and PR templates, CODEOWNERS, CodeQL, labeler and stale workflows.
- VS Code tasks, launch configuration and extension recommendations (`.vscode/`) to build, run and debug the Functions host locally (F5). `.vscode/settings.json` is git-ignored: the Azure Functions extension stores a right-click deploy target there, and deployments go through the CI/CD pipeline's approval gate, what-if preview, health check and smoke test.
- `scripts/setup-adr-deploy.ps1`: one re-runnable step that creates the deploy identity (OIDC federated credential for the subject GitHub reports for the repository, immutable `owner@id/repo@id` subjects included; Contributor plus Role Based Access Control Administrator limited to the roles the template assigns), the protected `adr-dev` environment and the repository variables. Device-code sign-in; no secrets. Safe to re-run: existing repository variables pin the tenant, subscription, region and deploy app (it signs in to that tenant and never creates a second app), auth mode and networking choices are kept, an unconstrained Role Based Access Control Administrator assignment is removed, and every step is read back and verified (`PASS`).

### Changed
- Deploy is explicit opt-in: it runs only from `main` when the repository variable `ADR_DEPLOY_ENABLED` is `true`, with a preflight check for the Azure variables; otherwise pushes to `main` emit a `Deploy skipped` notice. The manual-dispatch environment is a fixed choice (no free-text typos creating unprotected environments). Deploy has per-environment concurrency (no cancel-in-progress); both jobs have timeouts.
- Test results (TRX + coverage) are uploaded as the `test-results` artifact; `dorny/test-reporter` was removed because it needs `checks: write`, which fork pull requests never get.
- Removed the stale DocGen copy from the unit-test assembly (`tests/AdrMcp.UnitTests/tools/`); `tools/AdrMcp.DocGen` is the only generator.
- Consolidated the duplicate root `adr/` folder into `docs/adr/`, the only ADR location.
- GitHub Actions moved to their Node 24 majors (checkout v7, setup-dotnet v6, upload-artifact v7, gitleaks-action v3, azure/login v3, labeler v7, stale v11, codeql-action v4 — v3 is deprecated in December 2026). Dependabot now groups action updates into one weekly PR.
- Dependabot ignores Microsoft.ApplicationInsights.WorkerService 3.x (it removes types that Microsoft.Azure.Functions.Worker.ApplicationInsights 2.x loads, so the worker would crash at startup) and never updates the `samples/evidence` demo fixture.
- Removed the unused NSubstitute test dependency.
- CodeQL drops results located in source-generated code (`obj/`, `*.g.cs`, e.g. `RegexGenerator.g.cs` from `[GeneratedRegex]`) before uploading the SARIF, under the explicit category `/language:csharp`. All queries still run on handwritten code. The switch from the default category left the old configuration's alerts open on `main` until that configuration was deleted.
- CodeQL excludes `cs/linq/missed-where` and `cs/linq/missed-select` (`.github/codeql/codeql-config.yml`): style suggestions to rewrite a `foreach` as LINQ, not defects. Every security query and every other quality query still runs.
- `samples/evidence/package.json` (detector demo fixture, never installed) moves `vitest` to `^5.0.5`, which closes the critical Dependabot alert for Vitest UI file read on Windows (< 3.2.6).
- Runtime moves from .NET 8 to .NET 10 (LTS) ahead of .NET 8 end of support on 2026-11-10 (ADR-0007, Proposed): `net10.0`, SDK 10.0.1xx, and the Flex Consumption runtime version is now the `functionsDotnetVersion` Bicep parameter (default `10.0`).
- Safety rule SR-EOL-001 is date-aware for .NET: it compares official end-of-support dates (.NET Core 2.1 to .NET 10) with the review date, so it flags .NET 8 and 9 as end of life from 2026-11-11. It also raises a Medium warning in the 90 days before end of support, and now matches `dotnet 6`, `net8.0` and `netcoreapp3.1`.

### Fixed
- IaC drift: the Bicep/Terraform resource-block parser now skips comments and handles escapes, Bicep `'''` strings and quotes inside `${...}` interpolation. An apostrophe in a comment (`// don't …`, `# it's …`) or a string ending in `\\` used to make one resource swallow the next, so a non-compliant resource could be reported Compliant.
- Evidence loading and the maintenance timer still skip a failing file or tenant, but no longer swallow the caller's cancellation (a cancelled load returned a partial evidence set as if complete; shutdown was logged as a failure per tenant).
- CodeQL quality findings: removed discarded Cosmos `QueryDefinition`s, disposed the finding upsert stream and response, replaced a 15-clause auth normalisation ternary with an ordered table, and tidied test disposables.
- `scripts/*.sh` are committed as executable. azd runs the `postprovision` hook as `./scripts/postprovision.sh`, which failed with "Permission denied" (exit 126) on the first adr-dev provision after every resource had been created. `.gitattributes` keeps `*.sh` at LF line endings so a commit from Windows cannot break the hook either.
- CI was red since the initial commit: Azure.Storage.Blobs 12.30.1 sends API version 2026-10-06, which Azurite rejects. CI now runs Azurite 3.37.0 with `--skipApiVersionCheck` and sets `ADR_TEST_REQUIRE_AZURITE=true`, so Azurite tests fail instead of skipping. Local instructions add the same flag.
- Repository links (badges, clone instructions, issue-template contact links) now point to `saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP`.
- First deployment: `scripts/postprovision.sh` no longer treats a 403 (Key Vault role still propagating) as "secret missing". It retries with backoff and creates `principal-hash-salt` only after Key Vault answers SecretNotFound, so an existing salt is never overwritten. The deploy job retries `azd deploy`, waits for `/api/health`, and polls for the `mcp_extension` key before the smoke test.
- The deploy job's plan step is now `azd provision --preview`, so the what-if resolves `infra/main.parameters.json` exactly as `azd provision` does (it previously omitted the deployer role assignments and Defender settings).
- In Entra mode `/api/health` returns 503 when the worker does not see App Service Authentication, so a deployment that would reject every call fails the deploy job instead of going green.
- Entra tenants can now be configured in Azure: `Auth:Tenants:n:{Id, MapTo, Workspaces}` works as Linux app settings, unlike the GUID-keyed `Auth:TenantWorkspaces`/`Auth:TenantMap` (Linux setting names allow only letters, digits and `_`). Previously every Entra tenant got only the `default` workspace, and the maintenance timer never covered Entra tenants. Bicep now enrolls the deployment tenant, so the app also refuses other tenants (defence in depth behind the edge checks). Startup fails on a tenant ID that is not a GUID or appears twice (in any GUID spelling), on two tenants resolving to one ADR tenant ID (they would share a partition), and on tenant or workspace IDs outside the identifier rule. `Auth:Tenants` and `Auth:TenantMap` apply to Entra principals only; the FunctionKey binding still uses `Auth:TenantWorkspaces` alone.
- Worker telemetry: the isolated worker's Application Insights SDK now authenticates with the app's managed identity (read from `APPLICATIONINSIGHTS_AUTHENTICATION_STRING`); with local authentication disabled on Application Insights it was previously rejected, and only host telemetry arrived.
- Cosmos DB: `enableAutomaticFailover` is set explicitly (`true`, matching the deployed account). The template omitted it, so the what-if plan showed `true => false` on every deploy and a full re-provision would have turned it off. No effect with one region.

### Security
- `authMode=EntraBuiltIn` now deploys App Service Authentication (`authsettingsV2`: Entra provider, require authentication, 401, only the client apps in the new `entraAllowedClientIds` parameter), `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` and `WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES` from the new `entraClientId` parameter. The deployment fails without both, and the deploy job asserts an unauthenticated MCP call gets 401. Previously the MCP webhook became `Anonymous` with no authentication in front of it until someone configured it by hand.
- In Entra mode the server rejects every request unless App Service Authentication is enabled (`WEBSITE_AUTH_ENABLED`), so a disabled or drifted configuration cannot turn a forged `X-MS-CLIENT-PRINCIPAL` header into access.
- An Entra principal without a mapped app role now gets no access (`Auth:DefaultRoleWhenNoAppRoles` defaults to `None`, was `Reader`), so every caller needs an assigned app role. Set it to `Reader` to restore the old behaviour.
- `scripts/postprovision.sh` passes the generated salt to `az` through a private temporary file instead of the command line, and fails fast with a network-specific message when the vault firewall or private endpoint blocks the deployer. The example local salt is now too short for durable persistence, so it can never be reused in Azure.
- With durable persistence the app refuses to start when `Adr:PrincipalHashSalt` is the default placeholder, shorter than 32 characters, or an unresolved Key Vault reference, instead of silently hashing principals with a guessable salt.
- Replaced the broken `dotnet list package --vulnerable` step (it printed `MSB0001`, exited 0 and could never fail) with NuGet Audit in `Directory.Build.props` (`NuGetAuditMode=all`, `NuGetAuditLevel=low`). In CI, NU1901–NU1904 are errors, so restore fails on any known vulnerable direct or transitive package.
- Least-privilege workflow token permissions: `{}` at workflow level; `build-test` gets `contents: read` and `pull-requests: read`, `deploy` gets `contents: read` and `id-token: write`.
- Non-GitHub actions (gitleaks-action, Azure/setup-azd, azure/login) are pinned to commit SHAs; the deploy job can mint Azure tokens. Gitleaks now runs before any pull-request code. NuGet Audit fails closed when vulnerability data is unavailable (NU1900/NU1905), and a new CI step scans the Functions host-extension packages (generated `WorkerExtensions` project), which NuGet Audit settings never reach.
- Manually dispatched runs (the deploy path) scan the full git history with gitleaks and failed on 8 false positives in the initial commit (public role IDs, the redactor's replacement text, a sample idempotency key, and the secret-scanner test's deliberate fakes). They are baselined in `.gitleaksignore` by exact fingerprint, and the file is CODEOWNERS-protected.
- The smoke test reads the function key from `ADR_MCP_KEY` and the Entra token from `ADR_MCP_BEARER`, so neither appears on the command line; Azurite in CI binds to `127.0.0.1` and readiness waits for an HTTP answer.

## [1.0.0] - 2026-10-07

### Added
- ADR Intelligence MCP Server on Azure Functions Flex Consumption (.NET 8 isolated, MCP extension 1.6.0) with 27 implemented tools.
- Cosmos DB (serverless) + Blob + Queue persistence with ETag concurrency, version history and tenant/workspace partitioning.
- Human-owned vs system-managed field separation with propose → approve workflow.
- Direct-to-Blob upload flow with quarantine, hashing, MIME sniffing and deterministic content safety.
- Technology detectors (8), drift detectors (3), contradiction rules (7), safety-review rules (12), decision memory, audit.
- Bicep infrastructure, azd configuration, OIDC-based CI/CD, MCP protocol smoke test, documentation set A–L.
