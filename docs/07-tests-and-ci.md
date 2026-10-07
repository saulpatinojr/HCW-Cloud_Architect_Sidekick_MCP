# J. Tests and K. CI/CD

## Test inventory

| Requirement | Where | What is proven |
|---|---|---|
| Unit tests | `tests/AdrMcp.UnitTests` (124 tests) | Domain rules, services, analyzers, pipeline behaviour over in-memory persistence that mimics Cosmos semantics (ETags, id uniqueness, partition scoping, JSON round-trip). |
| Integration tests (Azurite) | `tests/AdrMcp.IntegrationTests/BlobStoreIntegrationTests.cs` | Real SAS generation and HTTP PUT against Azurite: the grant writes exactly one blob, cannot read or list, cannot write another name; hashing byte cap; promote/delete; tenant-scoped path validation. Skipped automatically when Azurite is down; CI runs Azurite as a service container. |
| Integration tests (Cosmos) | `CosmosIntegrationTests.cs` | ETag conflict and partition isolation against the real SDK; gated by `ADR_TEST_COSMOS_*` env vars (emulator or identity). |
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

Pipeline stages, all required before deployment:

1. **Restore and build** (`dotnet build -c Release`, nullable warnings are errors).
2. **Unit + integration tests** with Azurite service container; TRX results published, failures fail the job.
3. **Dependency scan**: `dotnet list package --vulnerable --include-transitive` fails on any known CVE (plus Dependabot recommended in repo settings).
4. **Secret scan**: gitleaks over full history.
5. **Bicep validate**: `az bicep build` + `az bicep lint`.
6. **Contract docs freshness**: regenerates `docs/03-mcp-contracts.md` and diffs.
7. **Publish artifact** for traceability.
8. **Deploy job** (only on `main` push or manual dispatch) uses a **protected GitHub environment** (required reviewers = human approval gate), **OIDC federated identity** (`azure/login@v2` + `azd auth login --federated-credential-provider github`) — no long-lived Azure credentials. Runs `az deployment sub what-if` (plan) before `azd provision` and `azd deploy`.
9. **Post-deployment smoke test** with the system key fetched into a masked variable.

Required repository configuration: variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION`, optional `ADR_AUTH_MODE`, `ADR_PRIVATE_NETWORKING`; an Entra app registration with a federated credential for `repo:<org>/<repo>:environment:adr-dev` holding Contributor + User Access Administrator (or a custom role) on the target subscription/resource group.
