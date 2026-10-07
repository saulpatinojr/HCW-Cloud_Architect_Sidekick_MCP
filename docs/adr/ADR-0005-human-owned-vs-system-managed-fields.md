# ADR-0005: Human-owned content and system-managed observations are separate records with separate write paths

- Status: Accepted
- Date: 2026-10-07
- Tags: governance, domain-model, safety

## Context
The system must refresh observed metadata automatically while guaranteeing that it never changes a human decision. Field-level permission checks are easy to forget; the guarantee should be structural and testable.

## Decision
`Adr` aggregates two records: `HumanOwnedContent` (title, context, decision, rationale, alternatives, consequences, status, owners, approvers, approvalResult, humanNotes, tags, claims, supersedes, supersededBy) and `ObservedState` (technologies, linked files, evidence refs, finding ids, drift status, confidence, timestamps, analyzer version). `Adr.ApplyHumanChange` requires a provenance with `ActorType == human`, validates lifecycle transitions and bumps `humanRevision` and `contentHash`. `Adr.ApplyObservedUpdate` can only receive an `ObservedState` and bumps `observedRevision`; `AdrService.ApplyObservedUpdateAsync` re-hashes human content afterwards and throws if it changed. Every change is preceded and followed by an immutable snapshot in `adr-history`. Suggested edits go through `ChangeProposal` (propose → review → approve/reject → apply) with stale-field hashes, expiry and a four-eyes rule.

## Rationale
Type-level separation makes "the analyzer edited the decision" unrepresentable in code; tests prove it (`HumanOwnedFieldProtectionTests`). Provenance plus content hash makes every revision auditable and reversible (restoring from a snapshot is itself a human change with the snapshot's content).

## Alternatives
Field-level ACL on a single record (rejected: relies on every caller), event sourcing (deferred: heavier than needed for the MVP; snapshots give equivalent auditability).

## Consequences
- Observed updates can conflict with each other; they retry as merges. Human changes use strict ETag checks.
- Accepted decisions are immutable except status/supersession/approvers/notes; changing a decision means a new ADR that supersedes the old one.
