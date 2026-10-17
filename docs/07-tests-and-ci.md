# J. Tests and K. CI/CD

## Test inventory

| Requirement | Where | What is proven |
|---|---|---|
| Unit tests | `tests/AdrMcp.UnitTests` (124 tests) | Domain rules, services, analyzers, pipeline behaviour over in-memory persistence that mimics Cosmos semantics (ETags, id uniqueness, partition scoping, JSON round-trip). |
| Integration tests (Azurite) | `tests/AdrMcp.IntegrationTests/BlobStoreIntegrationTests.cs` (6 tests) | Real SAS generation and HTTP PUT against Azurite: the grant writes exactly one blob, cannot read or list, cannot write another name; hashing byte cap; promote/delete; tenant-scoped path validation. Azurite-backed tests skip locally when Azurite is down; CI sets `ADR_TEST_REQUIRE_AZURITE=true`, so they fail instead of skipping. |
| Integration tests (Cosmos) | `CosmosIntegrationTests.cs` (1 test) | ETag conflict and partition isolation against the real SDK. Skipped unless `ADR_TEST_COSMOS_ENDPOINT` (identity) or `ADR_TEST_COSMOS_CONNECTION_STRING` (emulator) is set; CI sets neither. |
| MCP contract tests | `Contracts/ToolContractTests.cs` | Every catalog tool: valid input/output schema, `additionalProperties:false`, `workspaceId` present, compact description, failure codes, idempotency key where required; MVP tools implemented; one `[McpToolTrigger]` per implemented tool with matching name; stable error envelope; correlation id handling; Markdown round trip. |
| Protocol smoke test | `scripts/smoke-test.py` (run locally and post-deploy) | `initialize`, `tools/list` (schemas advertised), read tools, optional write round-trip through the real Functions host. Executed against the local host during development: 27 tools, passed. |
| Authorization tests | `AuthorizationTests` | Reader cannot create/approve; Author can propose but not transition status or approve. |
| Cross-tenant isolation | `TenantIsolationTests` | Unauthorized workspace → `TENANT_MISMATCH`; ADRs/search/memory invisible across workspaces; tenant never taken from arguments; Entra mode requires principal and maps app roles; FunctionKey mode ignores spoofed principal headers. |
| Secret redaction | `RedactionTests`, `ContentSafetyTests` | Log redactor masks keys/SAS/bearer/passwords/emails; error messages and audit summaries never contain secrets; principals are hashed. |
| File validation | `FileValidationTests`, `UploadFlowTests` | Traversal, control chars, disallowed extensions, oversize, executable sniffing, checksum mismatch, critical-secret block, duplicate linking, replay safety. |
| ETag conflict | `EtagConcurrencyTests`, Cosmos integration | Stale `expectedEtag` → `ETAG_MISMATCH` (retryable); idempotency replay/conflict. |
| Contradiction false positives | `ContradictionScenarioTests` (6 scenarios) | Prohibited vs required (true positive, high confidence); different systems (none); different environments and predecessor/supersession (none); database ownership + human classification persists; extracted claims capped as Inferred and compatible auth families → `needsSemanticReview`; data residency sub-region compatible vs conflicting. |
| Drift detectors | `DriftDetectorTests` | Prohibited dependency (drifted / compliant / uncertain), infrastructure requirement over Bicep + Terraform (compliant / drifted with line numbers / not applicable / uncertain / unsupported), database ownership (compliant / drifted / uncertain). |
| Automatic analysis cannot modify human fields | `HumanOwnedFieldProtectionTests` | System provenance cannot apply a human change; observed updates leave `HumanOwnedContent` and `contentHash` untouched; a full refresh+drift run against a real upload changes `observedRevision` only; proposals for system-managed fields are rejected. |

Run everything: `dotnet test AdrMcp.sln`.

## CI/CD (`.github/workflows/ci-cd.yml`)

Token permissions are `{}` at workflow level. Each job requests only what it needs: `build-test` gets `contents: read` and `pull-requests: read` (gitleaks lists the PR's commits); `deploy` gets `contents: read` and `id-token: write` (OIDC). Timeouts: 30 minutes for `build-test`, 60 minutes for `deploy`. GitHub-owned actions (`actions/*`, `github/*`) are pinned by major version; every other action is pinned to a full commit SHA with a version comment, and Dependabot updates both.

Pipeline stages, all required before deployment:

1. **Secret scan** (gitleaks, pinned to a commit) runs first — before any pull-request code executes, so that code cannot plant a `.gitleaksignore`, and regardless of later test results.
2. **Start Azurite**: `docker run mcr.microsoft.com/azure-storage/azurite:3.37.0 ... --skipApiVersionCheck --disableTelemetry`, bound to `127.0.0.1`, then wait until Azurite answers HTTP on ports 10000/10001. A service container is not used because Azurite needs the `--skipApiVersionCheck` flag. Azure.Storage.Blobs 12.30.1 sends service API version 2026-10-06, which no Azurite release supports yet; without the flag even 3.37.0 returns `400 InvalidHeaderValue` (verified 2026-10-16).
3. **Restore with NuGet Audit**, configured in `Directory.Build.props` (`NuGetAuditMode=all`, `NuGetAuditLevel=low`). When `CI=true`, NU1901–NU1904 (known vulnerabilities) and NU1900/NU1905 (vulnerability data unavailable) are errors, so restore fails closed on any known vulnerable direct or transitive package; locally they are warnings. Dependabot (`.github/dependabot.yml`) proposes NuGet and GitHub Actions updates weekly.
4. **Build** (`dotnet build -c Release`, nullable warnings are errors).
5. **Host-extension vulnerability check**: the Functions SDK restores a generated `WorkerExtensions` project (the host-side extensions that ship to Azure) without `Directory.Build.props`, so NuGet Audit settings never reach it. CI runs `dotnet list package --vulnerable --include-transitive --format json` on it and fails on any finding — or if no report can be produced.
6. **Unit + integration tests**; failures fail the job. The integration step sets `ADR_TEST_REQUIRE_AZURITE=true`, so Azurite tests fail instead of silently skipping. TRX results and coverage are uploaded as the `test-results` artifact. They are not published as a check run: that needs `checks: write`, which pull requests from forks never get.
7. **Bicep validate**: `az bicep build` + `az bicep lint`.
8. **Contract docs freshness**: regenerates `docs/03-mcp-contracts.md` and diffs.
9. **Publish artifact** (`api`) for traceability.
10. **Deploy job** runs on a `main` push or manual dispatch from `main`, and only when the repository variable `ADR_DEPLOY_ENABLED` is `true` (explicit opt-in, so forks and fresh clones never deploy). Without it, a push to `main` emits a `Deploy skipped` notice and the run stays green; with it, a preflight step fails loudly if any required Azure variable is empty. The job uses a **protected GitHub environment** (required reviewers = human approval gate) and **OIDC federated identity** (`azure/login` + `azd auth login --federated-credential-provider github`) — no long-lived Azure credentials. One deployment per environment runs at a time; a running deployment is never cancelled. It runs `az deployment sub what-if` (plan) before `azd provision` and `azd deploy`; `azd deploy` is retried up to 3 times because the app identity's role on the deployment container can still be propagating on a first deployment.
11. **Wait for start**: polls the anonymous `/api/health` probe for up to 5 minutes. A worker that never starts usually means an unresolved Key Vault reference for `Adr__PrincipalHashSalt` (the app refuses to start with a placeholder salt).
12. **Post-deployment smoke test** (FunctionKey mode) with the system key polled until the host has created it, held in a masked variable and passed to the script through `ADR_MCP_KEY` (never on the command line). In EntraBuiltIn mode the job emits a notice instead; run `scripts/smoke-test.py --bearer <token>` manually.

Required configuration, in this order (GitHub auto-creates a missing environment **without** protection rules, so create it first):
1. GitHub environment `adr-dev` with required reviewers and a deployment branch policy of `main` only.
2. An Entra app registration (or user-assigned managed identity) with a federated credential for `repo:<owner>/<repo>:environment:adr-dev` (issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`), holding **Contributor** plus **Role Based Access Control Administrator** (ideally constrained to the role IDs assigned in `infra/modules/rbac.bicep`) on the target subscription — the template deploys at subscription scope and creates role assignments.
3. Variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION` (repository or `adr-dev` environment level), optional `ADR_AUTH_MODE` (`FunctionKey` default; `EntraBuiltIn` also requires `ADR_ENTRA_CLIENT_ID`, see docs/05), `ADR_ENTRA_TENANT_ID`, `ADR_PRIVATE_NETWORKING`.
4. Last, the **repository** variable `ADR_DEPLOY_ENABLED=true` (it must be repository-level: the job-level `if` cannot see environment variables).
