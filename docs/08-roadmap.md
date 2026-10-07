# L. Delivery roadmap

No time estimates by design. Each phase lists features, dependencies, acceptance criteria, risks and measurable outcomes.

## Phase 1 — MVP (this repository)

**Features.** 27 implemented tools incl. the required 15 MVP tools; Cosmos + Blob persistence; version history and snapshots; ETag concurrency; human-owned field protection with proposal workflow; deterministic content safety; tenant/workspace isolation; audit + decision memory; 8 technology detectors; 3 drift detectors; 7 contradiction rules; 12 safety-review rules; queue-based jobs; Bicep + azd; CI/CD with OIDC; Claude Code and Claude custom-connector instructions.

**Dependencies.** Azure subscription with Flex Consumption region; Entra app roles (for Entra mode); Core Tools ≥ 4.0.7030.

**Acceptance criteria.** `dotnet test` green; `scripts/smoke-test.py --write` passes against the deployed endpoint; a human-owned field cannot be changed by any analysis path (tests); contradiction scenarios 1–6 pass; upload flow rejects secrets when policy says so; all tools advertise schemas in `tools/list`.

**Risks.** Regex-based extraction misses prose-only claims; CONTAINS search is approximate; key-based auth is a shared secret; Azure built-in MCP auth is preview.

**Measurable outcomes.** Time from "decision made" to ADR with evidence < 1 session; ≥ 90 % of declared claims evaluated by a detector (not Unsupported) on the sample workspace; zero secrets in sanitized evidence for the test corpus.

## Phase 2 — Production hardening

**Features.** Built-in Entra auth as default with pre-authorized Claude clients; private endpoints + Front Door/APIM with per-client rate limiting and WAF; mandatory audit (fail call on audit failure); Key Vault reference fail-fast; Defender for Storage malware verdict required; archive expansion job with zip-bomb limits; PDF/image text extraction (Document Intelligence) behind `IContentSafetyService`; Azure AI Content Safety Prompt Shields + Language PII as additional **labelled** detectors; Durable Functions for multi-step imports; alerting on drift status changes (Event Grid → Teams/email); .NET 10 upgrade; load tests; chaos tests for queue poison paths; SBOM generation.

**Dependencies.** Identity team for app registrations and consent; network team for VNet/DNS; Defender plan; Content Safety resource.

**Acceptance.** Pen-test findings closed; p95 tool latency < 1.5 s for reads; poison-queue runbook exercised; auth switch validated with Claude Code and Claude.ai connector.

**Risks.** Preview auth changes; Anthropic egress IP changes; cost of model-based scanners.

**Outcomes.** 100 % of calls attributable to an Entra principal; zero shared keys; SLO 99.9 % availability.

## Phase 3 — Advanced architecture intelligence

**Features.** `file_import_from_repository` with server-side host/org allow-lists (GitHub App / Azure DevOps PAT in Key Vault); `adr_find_related_code` (keyword + claim matching over evidence index); `adr_impact_analysis` (dependency + supersession graph); `dependency_map_generate` and `c4_model_generate` (Mermaid from manifests/IaC); hybrid retrieval for search and memory (Azure AI Search vectors behind `IMemoryRepository`/`IAdrRepository.SearchAsync`); model-assisted claim extractor and `ISemanticContradictionAnalyzer` (Azure OpenAI via managed identity, outputs Inferred); additional detectors (Helm values, GitHub Actions security, OpenAPI, Terraform modules); more drift kinds (auth strategy, data residency region checks, integration style via messaging SDK presence).

**Dependencies.** Repository credentials governance; AI Search / OpenAI resources; evaluation corpus of labelled contradictions.

**Acceptance.** Precision ≥ 0.9 on the labelled contradiction corpus (deterministic + semantic); repository import of a 5k-file repo completes as a job within limits; C4 diagrams render for the sample workspace.

**Risks.** Model cost and drift; false positives undermining trust; repository credential scope creep.

**Outcomes.** Mean time to detect drift after a merge < 1 hour; ≥ 50 % of ADRs with ≥ 1 linked code file.

## Phase 4 — Enterprise governance

**Features.** `architecture_rule_create/validate`, `architecture_fitness_check`, `governance_check`, `deployment_readiness_check` with policy-driven hard blocks; GitHub Action / Azure Pipelines task that calls the fitness tools in PRs and pre-deploy gates; `decision_lineage_get`; retention/deletion runbooks and legal hold; per-tenant storage/Cosmos isolation option; SCIM/group-based workspace membership; exportable evidence packs for auditors; Azure Policy alignment.

**Dependencies.** Organization policy definitions (which rules block); CI integration owners; compliance stakeholders.

**Acceptance.** A PR that violates a blocking rule is blocked with an evidence-backed message; exemptions have owner and expiry; governance dashboard shows open contradictions/drift per workspace.

**Risks.** Over-blocking slows delivery; policy ownership ambiguity.

**Outcomes.** Blocking rules sourced only from accepted ADRs with explicit policy flags; audit pack generation < 5 minutes.
