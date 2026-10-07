# ADR-0004: Queue-backed asynchronous analysis with a job record

- Status: Accepted
- Date: 2026-10-07
- Tags: jobs, queue, reliability

## Context
Repository-scale technology detection, drift scans and bulk contradiction analysis can exceed what an MCP request should hold open (Flex Consumption default timeout, client timeouts, model context). Work must be cancellable, deduplicated, retried and observable.

## Decision
Mutating analysis tools accept `async=true` (or always queue for bulk work) and return a **job id** immediately. Jobs live in Cosmos `jobs` (states Queued → Running → Succeeded/Failed/Cancelled/Expired/Poisoned, TTL 30 days) and are dispatched through an **Azure Storage Queue** (`analysis-jobs`) consumed by a `QueueTrigger` function in the same app. `analysis_job_get`, `analysis_job_cancel`, `analysis_job_results` expose the lifecycle. Dedupe is by `dedupeKey` while a job is active. Retries come from the queue (`maxDequeueCount: 3`); after the last attempt the job is marked `Poisoned` and the message lands in `analysis-jobs-poison`. Handlers are idempotent and report progress; cancellation is checked at each progress report. Correlation ids propagate from the originating tool call.

## Rationale
- Storage Queues are already required by the Functions host (identity-based), cost nothing extra and give at-least-once delivery with poison handling.
- Durable Functions was considered for fan-out orchestration; deferred because the MVP jobs are linear and a job record already provides status and results.

## Alternatives
Service Bus (sessions, dead-letter, larger messages) — reasonable upgrade when ordering per workspace matters. Durable Functions — adopt for multi-step repository imports and C4 generation in a later phase.

## Consequences
- Job results are compact JSON on the record; large reports go to Blob `reports` with a pointer.
- The timer-driven refresh enqueues one job per Accepted ADR every 6 hours; dedupe prevents pile-ups.
