# ADR-0003: Direct-to-Blob uploads with single-object, write-only, short-lived grants

- Status: Accepted
- Date: 2026-10-07
- Tags: uploads, security, blob

## Context
MCP tool arguments travel as JSON inside JSON-RPC; large or binary evidence would bloat the model context, hit payload limits and block the Functions request. Files are untrusted and may contain secrets, PII or prompt-injection text.

## Decision
A two-step flow: `file_upload_request` validates (name sanitization, extension and MIME allow-lists, size cap, optional sha256 duplicate check) and returns a **user-delegation SAS** for exactly one blob in `uploads-quarantine/{tenant}/{workspace}/{uploadId}/{safeName}` with permissions **Create + Write only**, HTTPS only, 15-minute expiry. The client PUTs bytes directly to Blob. `file_register_upload` then verifies existence, size, server-computed SHA-256 (compared with the declared value), MIME plausibility (magic bytes), the optional Defender for Storage verdict, runs the content-safety pipeline, writes a sanitized variant, copies the original to the immutable `evidence` container, deletes the quarantine blob, attaches to the ADR and queues analysis. Registration is single-use; repeats return the recorded outcome.

Small text ADRs still go through `adr_create` (`markdown` ≤ 200 k chars).

## Rationale
- No storage credentials appear in MCP output beyond a scoped, expiring, write-only SAS that cannot list or read (verified by the Azurite integration test).
- User-delegation SAS is signed with an Entra token from the managed identity; shared-key access on the account is disabled.
- Server-side hashing and sniffing make the client's declarations advisory, not trusted.

## Alternatives considered
1. Base64 in tool arguments — rejected (context window, 2 MB argument cap, host timeout).
2. An HTTP-trigger upload endpoint on the Function App — workable but routes bytes through compute and needs its own auth; kept as a fallback for clients that cannot PUT to Blob.
3. Account or container SAS — rejected (too broad).

## Consequences
- Clients need an HTTP PUT capability outside MCP (Claude Code can run `curl`; Claude.ai connectors cannot upload binaries directly, so the user or a helper performs the PUT — documented in H and I).
- Quarantine blobs that are never registered are purged by a lifecycle rule (1 day) and the maintenance timer.
- Binary types (zip, pdf, images) are stored but not text-scanned in the MVP; archive expansion with zip-bomb limits is a later-phase job.
