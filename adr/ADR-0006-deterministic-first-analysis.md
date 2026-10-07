# ADR-0006: Deterministic analysis first; model-assisted analysis only for labelled, capped inference

- Status: Accepted
- Date: 2026-10-07
- Tags: analysis, llm, confidence

## Context
Technology detection, drift, contradictions and safety review could all be delegated to an LLM, but model output is non-deterministic, expensive, unverifiable and vulnerable to prompt injection embedded in evidence. The server runs on behalf of Claude, which is itself an LLM.

## Decision
1. Parsers and rules run first: csproj/package.json/requirements/Dockerfile/Bicep/Terraform/Kubernetes/CI detectors; claim normalization; drift detector plugins; contradiction rules with scope/date/status filters; heuristic safety rules.
2. Every result carries `Confidence{score, basis}`; anything not parsed from evidence or declared by a human is **Inferred** and capped at **0.6**.
3. The server makes **no model calls** in the MVP. Contradiction candidates below 0.85 are returned with `needsSemanticReview=true` and guidance; Claude (the client) performs the semantic judgement and a human records it with `finding_review`. An `ISemanticContradictionAnalyzer` seam exists for a server-side model later (Azure OpenAI via managed identity), whose verdicts would remain Inferred.
4. All model-facing text is the sanitized variant with an `[UNTRUSTED CONTENT]` banner when injection indicators were found.

## Rationale
Evidence-backed, reproducible findings are the product's trust basis; LLMs add recall where parsers cannot reach, but their output must be labelled, capped and human-confirmed. Keeping the server model-free also removes API keys and egress from the server.

## Consequences
- Some real contradictions expressed in prose will be missed by regex extraction; the roadmap adds a model-assisted extractor behind the same interfaces.
- Safety-review findings are heuristics with explicit observed/inferred/recommendation parts and never block unless organization policy says so.
