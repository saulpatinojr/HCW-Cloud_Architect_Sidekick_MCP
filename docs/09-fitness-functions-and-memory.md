# Fitness functions (§14), decision memory (§15) and asynchronous processing (§16)

## Fitness functions

An `ArchitectureRule` (`src/AdrMcp.Domain/Rules/ArchitectureRule.cs`) is an executable check derived from an accepted ADR claim:

| Field | Meaning |
|---|---|
| `ruleId`, `adrId` | Identity and the decision it enforces |
| `scope` | System / bounded context / environment the rule applies to |
| `description`, `severity` | Human text and Info..Critical |
| `evaluator` + `evaluatorParameters` | Name of the registered plugin (`IDriftDetector` or a dedicated `IFitnessEvaluator`) and its parameters (e.g. the claim) |
| `evidenceSource` | `workspace-evidence`, `repository:<id>`, `resource-export` |
| result | `Pass`, `Fail`, `Unknown` (from `DriftClaimResult`: Compliant → Pass, Drifted → Fail, otherwise Unknown) |
| `policyBlocking` | **Only an Admin-set flag makes a rule blocking**; analyzer output alone never blocks |
| `exemptionProcess` | Text/URL of the exemption workflow; exemptions are `finding_review` = AcceptableException with note and expiry |
| `effectiveFrom`, `expiresAt` | Validity window |

Running the same checks in different contexts:

| Context | How |
|---|---|
| Through Claude | `adr_detect_drift` today; `architecture_fitness_check` (phase 4) returns pass/fail/unknown per rule |
| On demand via MCP | Any MCP client (MCP Inspector, scripts) calls the same tools with the system key or an Entra token |
| In a pull request | A CI step uploads changed manifests/IaC via `file_upload_request`/`file_register_upload` (curl) and calls `adr_detect_drift`/`architecture_fitness_check`; the step fails only if a finding has `policyBlocksAcceptance=true` |
| In CI/CD | Same as PR, run against the merged artifact; results posted as a check |
| Before deployment | `deployment_readiness_check` (phase 4) aggregates blocking rules + open critical findings for the target service; the deploy job gates on `ready=true` |

Hard blocks always trace back to `SafetyPolicy:BlockingRuleIds` or `ArchitectureRule.policyBlocking`, both set by Admins — never to an unreviewed recommendation.

## Decision memory

- Stored per tool call by the pipeline (`DecisionMemoryService.RecordAsync`): tool name, hashed principal, session id, ADR ids touched, finding ids, evidence ids, keywords extracted deterministically from the request/ADR titles, redacted summary. **No raw secrets, no file contents, no tool arguments verbatim.**
- Isolation: partition `tenant|workspace`; retrieval filters again post-query; TTL `Memory:RetentionDays` (180).
- Retrieval (`decision_memory_search`): candidates by `ARRAY_CONTAINS(keywords)` then explainable scoring `0.7·overlap + 0.3·recency(half-life 30 d) × 1.15 if human feedback`, returned with matched terms and an explanation string.
- Human feedback and confirmed/rejected contradiction classifications are linked through finding ids; `finding_review` writes the classification on the finding and the memory record references it.
- Abstraction: `IMemoryRepository` + the scorer are the seam for vector/hybrid retrieval (Azure AI Search) without changing callers.

## Asynchronous processing

See ADR-0004. Job states: `Queued → Running → Succeeded | Failed | Cancelled | Expired | Poisoned`. Retry: queue redelivery up to `maxDequeueCount=3` with `visibilityTimeout=1m`; a handler marks retryable failures to request redelivery. Poison: after the last attempt the job is `Poisoned` and the message moves to `analysis-jobs-poison`. Dedupe: `dedupeKey` returns the active job. Correlation: the originating tool call's correlation id is stored on the job and used in logs. Cancellation: `analysis_job_cancel` sets a flag checked at each progress step. Expiration: `ExpiresAt`/TTL 30 days; `analysis_job_get` marks stale jobs Expired. Result retention: compact JSON on the job; large reports in Blob `reports` (30-day cool tier).
