# ADR-0002: Cosmos DB for NoSQL (serverless) for metadata, Blob Storage for content

- Status: Accepted
- Date: 2026-10-07
- Tags: persistence, cosmos-db, storage

## Context
ADR metadata needs point reads, filtered lists per workspace, keyword search, version history, polymorphic findings, TTL-based retention (jobs, memory, audit, idempotency) and optimistic concurrency. Files need durable, cheap, immutable storage with direct client upload. Two designs were evaluated: (A) Cosmos DB NoSQL + Blob, (B) Blob + Table Storage only.

## Decision
**(A) Cosmos DB for NoSQL in serverless mode** for all metadata, **Blob Storage** for content. Partition key for every container is the synthetic value `/partitionKey = "{tenantId}|{workspaceId}"`.

Containers: `adrs`, `adr-history`, `proposals`, `evidence` (upload/evidence records), `findings` (polymorphic with `findingType`), `jobs` (TTL 30 d), `memory` (TTL 180 d), `audit` (TTL 365 d), `idempotency` (TTL 24 h), `rules` (later phase).

Expected query patterns (all single-partition):
| Pattern | Container | Shape |
|---|---|---|
| Get ADR by id | adrs | point read (1 RU) |
| List ADRs by status/tag/owner | adrs | `WHERE c.partitionKey=@pk AND c.human.status=@s ORDER BY c.adrNumber DESC` + continuation |
| Keyword search | adrs | `CONTAINS(LOWER(c.human.title), @t) OR …` (MVP); Azure AI Search / vector later |
| Active ADRs for contradiction scan | adrs | `ARRAY_CONTAINS(@statuses, c.human.status)` |
| Next ADR number | adrs | `SELECT VALUE MAX(c.adrNumber)` (id uniqueness resolves races) |
| Latest finding by fingerprint | findings | `WHERE c.fingerprintKey=@fp ORDER BY c.provenance.timestamp DESC` TOP 1 |
| Evidence by content hash | evidence | `WHERE c.state='approved' AND c.actualSha256=@sha` |
| Active job by dedupe key | jobs | `WHERE c.dedupeKey=@k AND state IN (queued, running)` |
| Memory candidates | memory | `ARRAY_CONTAINS(c.keywords, @k0) OR …` then scored in memory |

Concurrency: every replace sends `If-Match: <_etag>`; `412` maps to `ETAG_MISMATCH`. Creates use the id uniqueness constraint. Idempotency keys are stored per (partition, tool, key) with the request hash.

## Rationale
- Polymorphic documents, TTL per container, rich filtering and ETags are native; Table Storage would need secondary index tables, manual TTL cleanup and client-side filtering for most patterns.
- Serverless billing keeps the MVP cheap (no provisioned RU/s) while preserving a path to autoscale throughput.
- Synthetic partition key is simple to reason about, makes every query single-partition and enforces tenant isolation at the storage layer; hierarchical partition keys (`/tenantId`, `/workspaceId`) are a drop-in alternative if a workspace ever exceeds 20 GB.

## Lower-cost alternative (documented, not implemented)
**Blob + Table Storage**: entities in Table (PartitionKey = tenant|workspace, RowKey = id) with ETag concurrency; secondary tables for status/tag/hash lookups; findings serialized as JSON blobs with Table pointers; TTL via a timer function; search via Azure AI Search or client-side filtering. Cost ≈ storage only, but every list/search becomes a scan or a maintained index. `IAdrRepository` and friends are the seam: a `TableAdrRepository` can replace the Cosmos implementation without touching services.

## Consequences
- Cosmos SDK requires an explicit `Newtonsoft.Json` reference even though we serialize with System.Text.Json (`UseSystemTextJsonSerializerWithOptions`).
- Hot-partition risk is bounded by workspace size; monitor RU per partition.
- Full-text search is approximate in the MVP (CONTAINS); roadmap adds Azure AI Search.
