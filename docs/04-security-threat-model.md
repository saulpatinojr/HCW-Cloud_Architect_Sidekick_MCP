# Security, multitenancy, content safety and the upload policy

## 1. Authentication and authorization

| Layer | Mechanism | Notes |
|---|---|---|
| Edge (GA default) | `mcp_extension` **system key** in `x-functions-key` header | Rotated with `az functionapp keys set`. Single shared secret per deployment → suitable for a single team/tenant. Never written to logs (redactor strips `x-functions-key` and `code=`). |
| Edge (recommended for production, **preview**) | **Built-in MCP authentication** (App Service Authentication, Entra ID, OAuth 2.1 with protected resource metadata) | Deploy with `authMode=EntraBuiltIn`, `entraClientId` (the server app registration) and `entraAllowedClientIds`: Bicep then configures App Service Authentication (`authsettingsV2`: require authentication, 401, tenant-specific v2.0 issuer, `allowedApplications`), `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` and `WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES`, and fails the deployment if either ID list is missing. The deploy job asserts an unauthenticated MCP call gets 401. Pre-authorize client app ids (Claude's client id is required because Entra lacks DCR — see docs/06). The webhook level becomes `Anonymous` only together with that configuration, because the platform then authenticates. |
| Server | `HeaderTenantResolver` parses `X-MS-CLIENT-PRINCIPAL` (Entra mode only; the platform strips inbound copies, and the resolver rejects every request unless the platform-injected `WEBSITE_AUTH_ENABLED` is true) → tenant (`tid`), principal (`oid`/`azp`), roles (`roles` claim mapped by `Auth:AppRoleMap`; no mapped role means no access unless `Auth:DefaultRoleWhenNoAppRoles` opts in), workspaces (`Auth:TenantWorkspaces`). In FunctionKey mode the header is **ignored** (spoofable) and a static binding is used. | Tests: `TenantIsolationTests`, `AuthorizationTests`. |
| Server | `RoleAuthorizationService` checks the tool's `Permission` (from `ToolCatalog`) and resolves/validates `workspaceId` against the principal's allowed set. | Reader ⊂ Author ⊂ Approver ⊂ Admin. |

**Entra app roles to create on the server app registration:** `Adr.Reader`, `Adr.Author`, `Adr.Approver`, `Adr.Admin` (value = display name). Assign users/groups in Enterprise Applications. **[REVIEW REQUIRED]** by the identity team before production.

## 2. Tenant and workspace isolation

- Every Cosmos document lives in partition `"{tenantId}|{workspaceId}"`; every repository call takes tenant and workspace explicitly and queries are single-partition. The in-memory and Cosmos implementations both re-check `TenantId/WorkspaceId` on read (defence in depth).
- Blob paths are `{tenantId}/{workspaceId}/{id}/{safeName}`; `BlobEvidenceStore.ValidatePath` rejects anything that is not at least tenant/workspace scoped or contains traversal.
- Decision memory, audit, idempotency and jobs share the same partitioning; the memory service additionally filters by tenant/workspace after retrieval.
- Principals are stored hashed (`SHA-256(salt:principal)` first 32 hex chars) in audit and memory records; the salt comes from Key Vault. With durable persistence the app refuses to start if the salt is the default placeholder, shorter than 32 characters, or an unresolved Key Vault reference.

## 3. Managed identity and least privilege

User-assigned identity with: Storage Blob Data Owner (evidence + index tags), Storage Blob Delegator (user-delegation SAS), Storage Queue Data Contributor + Message Processor (host backplane + job queue), Storage Table Data Contributor (timer state), Cosmos DB Built-in Data Contributor (account scope; `disableLocalAuth=true`), Key Vault Secrets User, Monitoring Metrics Publisher. Shared-key access on Storage is disabled; Cosmos local auth is disabled; Application Insights uses AAD ingestion.

## 4. Encryption, network, retention

- TLS 1.2 minimum everywhere; HTTPS only; infrastructure encryption on Storage; Cosmos and Key Vault encrypted at rest by default (CMK is a hardening option).
- `enablePrivateNetworking=true` deploys a VNet, private endpoints (blob, queue, Cosmos SQL, Key Vault) with private DNS zones and VNet-integrates the Flex Consumption app; public network access on data services is disabled.
- Retention: quarantine 1 day (lifecycle) · evidence immutable 365 days (time-based policy, unlocked) · jobs 30 d · idempotency 24 h · memory 180 d · audit 365 d · Cosmos continuous backup 7 days. Deletion: `evidence_remove` detaches but retains the immutable blob for audit; hard deletion is an Admin runbook after the immutability window.

## 5. Input validation, output encoding, limits

- Tool arguments: strict DTO binding, `additionalProperties:false` in advertised schemas, per-field length/pattern limits, 2 MB argument cap, ids `^[A-Za-z0-9._:-]+$`.
- Outputs are JSON (no HTML rendering); excerpts truncated (≤ 240 chars) and taken only from **sanitized** text; error messages pass through `SensitiveDataRedactor`; internal exceptions never reach the client (only a correlation id).
- Rate limiting: platform-level (Flex Consumption `maximumInstanceCount`, HTTP `perInstanceConcurrency`), plus Azure Front Door / APIM in front of the app is the recommended hardening path for per-client quotas (`RATE_LIMITED` is already in the error contract). Cosmos 429s are retried then surfaced as retryable.
- Payload limits: 50 MiB per file (configurable), 10 MiB text-scan cap, 512 KB per inline file × 50, 200 k chars Markdown.

## 6. No dangerous tools by construction

No shell execution, no arbitrary filesystem paths (client supplies only relative evidence paths that are sanitized), no URL fetching (repository import will use a server-side host/org allow-list), uploaded code is never executed or parsed with native binaries.

## 7. Content safety pipeline (§9)

| Stage | Detector | Type | Action |
|---|---|---|---|
| 1 | `SecretPatterns` (Azure storage keys, SAS `sig=`, connection-string passwords, Service Bus keys, private key blocks, AWS keys, GitHub/Slack/Google/Stripe tokens, JWTs, bearer tokens, client secrets, generic api keys/passwords, functions keys, basic-auth URLs, Cosmos keys) | deterministic regex | mask `[REDACTED:<Type>]`, Critical/High severity |
| 2 | `PiiPatterns` (email, phone, US SSN, payment card **with Luhn**, IBAN **with mod-97**, UK NINO) | deterministic + validation | mask, Low–High |
| 3 | `ISensitivePatternProvider` custom organization patterns (from config/Key Vault) | deterministic | mask |
| 4 | `PromptInjectionPatterns` (ignore-previous-instructions, role override, system-prompt probes, tool-abuse phrases, chat markup, hidden HTML comments with instructions, zero-width runs) | heuristic | remove line + `[UNTRUSTED CONTENT]` banner, flag `promptInjectionSuspected` |
| 5 (later phase) | Azure AI Content Safety **Prompt Shields**, Azure AI Language **PII detection** | model-based | label as Inferred; never the only control |

Guarantees and limits: findings record type, severity, line/column, action, detector and a 12-hex **fingerprint**, never the value; allow-lists are fingerprints; `BlockOnCriticalSafetyFinding` blocks ingestion and deletes the quarantine blob; originals are kept only in the access-controlled `evidence` container; binary and oversized files are not text-scanned; detection is not claimed to be complete (no entropy analysis, no free-text PII such as names).

## 8. Upload policy (§8)

| Control | Value / behaviour |
|---|---|
| Supported types | Text/code/IaC/config extensions in `Uploads:AllowedExtensions`; extensionless allow-list (Dockerfile, package.json, …); media types `text/*`, JSON/XML/YAML/TOML, zip, pdf, png/jpeg/svg, octet-stream |
| Max size | 50 MiB (`Uploads:MaxFileBytes`); text scan up to 10 MiB |
| Archive handling | Stored as evidence; expansion is a later-phase job with limits: ≤ 2000 entries, ≤ 200 MiB uncompressed, compression ratio ≤ 100, entry-name traversal rejected |
| Path traversal | `SanitizeFileName` keeps only the final segment, strips control chars; blob paths validated |
| MIME verification | Declared type allow-listed; first 512 bytes sniffed (PE/ELF rejected; zip/pdf/png/jpeg/text plausibility) |
| Duplicate detection | SHA-256 per tenant/workspace at request (declared) and register (actual); duplicates are linked, not stored twice |
| Malware scanning | Optional Microsoft Defender for Storage on-upload scanning; verdict read from blob index tag when `Uploads:RequireDefenderScanResult=true` (retryable until present) |
| Quarantine | Separate container; promotion only after all checks; lifecycle purge after 1 day; maintenance timer marks records Expired |
| Timeout / retry | Grant TTL 15 min; `file_register_upload` is retryable before the PUT (`UPLOAD_NOT_FOUND`, retryable=true) and single-use after |
| Partial uploads | Never promoted (size/hash mismatch → Rejected, blob deleted) |
| Replay | Registration returns the recorded outcome; grants expire; SAS cannot list/read |

## 9. Threat model (STRIDE-style)

| Threat | Vector | Mitigations | Residual risk |
|---|---|---|---|
| MCP tool abuse | Prompt-injected Claude calls destructive tools | No destructive tools (archive is reversible via history; evidence detach keeps blobs); approvals require Approver role and four-eyes; `[UNTRUSTED CONTENT]` banners; tool descriptions instruct to propose, not change | Approver misuse — mitigated by audit |
| Cross-tenant data access | Forged `tenantId`/`workspaceId`, header spoofing | Tenant from platform-injected principal only; workspace validated against membership; partition-scoped queries; path validation | Misconfigured `TenantWorkspaces` |
| Malicious uploads | Executables, zip bombs, oversized files, polyglots | Allow-lists, sniffing, size caps, quarantine, Defender scan option, no execution, archive limits | Novel polyglots pass sniffing → still never executed |
| Secret exfiltration | Secrets in evidence surfaced to Claude; secrets in logs | Masking before storage of the sanitized variant; analyzers read sanitized only; log redactor; error redaction; hashed principals | Regex misses → sanitized copy may contain an unknown-format secret; original is access-controlled |
| Prompt injection | Instructions inside README/code/ADR text | Neutralization + banner; server never calls a model; humans classify findings | Client-side (Claude) still sees sanitized text |
| Oversized payload DoS | Huge arguments, many uploads | 2 MB argument cap, schema maxLength, 50 MiB file cap, platform throttling, Flex max instances, Cosmos retry limits | Volumetric DoS → Front Door/WAF recommended |
| SAS leakage | Upload URL logged or shared | Single blob, Create+Write only, 15 min, HTTPS, no list/read; redactor strips `sig=`; URL only in the tool response | Attacker overwrites blob within 15 min → hash check at register detects mismatch |
| Replay attacks | Re-sending `tools/call` | Idempotency keys for mutating tools; registration single-use; approvals check status and field hashes | Read replays harmless |
| Dependency / supply chain | Vulnerable NuGet packages, malicious actions | Central pinned versions; NuGet Audit on every restore (CI fails on any known vulnerable direct or transitive package); Dependabot; pinned GitHub Actions by major; job-scoped least-privilege workflow token permissions; Newtonsoft pinned ≥ 13.0.1 | Zero-days |
| Unauthorized decision changes | Analyzer or agent rewriting decisions | Type-separated human-owned record; human-provenance-only write path; proposals + approvals; content hash + snapshots; tests | Admin with legitimate rights — audited |

## 10. Audit trail

Every tool call writes an `AuditEvent` (tool, outcome, error code, target type/id, redacted summary, duration, correlation id, hashed principal). Audit failures are logged but do not fail the call in the MVP (hardening makes them mandatory). Application Insights receives structured logs with `CorrelationId` and `Tool` scopes; all messages pass through the redactor.
