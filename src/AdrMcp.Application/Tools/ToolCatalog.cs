using System.Text.Json.Nodes;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Tenancy;
using static AdrMcp.Application.Tools.S;

namespace AdrMcp.Application.Tools;

/// <summary>
/// Single source of truth for every MCP tool: name, description, authorization, idempotency, schemas, failure codes and phase.
/// The Functions host registers input/output schemas from here; contract tests validate it; docs are generated from it.
/// </summary>
public static class ToolCatalog
{
    private static readonly string[] Common = { ErrorCodes.ValidationFailed, ErrorCodes.Unauthorized, ErrorCodes.Forbidden, ErrorCodes.AuthUnavailable, ErrorCodes.TenantMismatch, ErrorCodes.RateLimited, ErrorCodes.Internal };
    private static string[] With(params string[] codes) => Common.Concat(codes).Distinct().ToArray();

    public static readonly IReadOnlyList<ToolDefinition> All = new List<ToolDefinition>
    {
        // ------------------------------------------------------------------ ADR management
        new("adr_create",
            "Create a new Architectural Decision Record in Draft or Proposed status from Markdown (MADR-style) and/or structured fields. Human-owned content is stored verbatim; the original Markdown is kept as the source of record.",
            Permission.AdrCreate, true, IdempotencyMode.Key, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId,
                ("markdown", Str("Full ADR Markdown (<= 200,000 chars). Headings recognised: Context, Decision, Rationale/Decision Drivers, Considered Options/Alternatives, Consequences, Status.", 200_000)),
                ("fields", FieldsSchema("Structured fields; override parsed Markdown when both are given. title is required when markdown is omitted.")),
                ("repositoryId", Str("Optional repository identifier this ADR belongs to.", 128)),
                IdempotencyKey)),
            Envelope(Obj(Props(("adr", AdrDetailSchema())))),
            With(ErrorCodes.Conflict, ErrorCodes.PayloadTooLarge)),

        new("adr_get", "Get one ADR with human-owned content, declared claims, and a compact view of system-managed observations. Optionally include the rendered Markdown.",
            Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId, ("includeMarkdown", Bool("Include rendered Markdown (larger response).", false))), "adrId"),
            Envelope(Obj(Props(("adr", AdrDetailSchema())))),
            With(ErrorCodes.AdrNotFound)),

        new("adr_list", "List ADRs in a workspace with optional status/tag/owner filters. Returns compact summaries and a continuation token.",
            Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, ("status", Str("Filter by status.", enumValues: StatusValues)), ("tag", Str("Filter by tag.", 100)), ("owner", Str("Filter by owner.", 200)), ("take", Int("Page size.", 1, 50, 25)), ("continuationToken", Str("Token from a previous page.", 4000)))),
            Envelope(Obj(Props(("items", Arr(AdrSummary, "ADR summaries")), ("continuationToken", Str("Pass to fetch the next page; absent when exhausted."))))),
            With()),

        new("adr_search", "Keyword search over ADR titles, tags, context, decision and consequences within a workspace. Returns ranked summaries with the matched terms. Deterministic scoring; no model involved.",
            Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, ("query", Str("Free-text query; up to 8 significant terms are used.", 500)), ("status", Str("Restrict to a status.", enumValues: StatusValues)), ("take", Int("Max results.", 1, 50, 10))), "query"),
            Envelope(Obj(Props(("hits", Arr(Obj(Props(("adr", AdrSummary), ("score", Num("Relevance score.")), ("matchedTerms", StrArr("Terms that matched.")))), "Ranked hits")), ("termsUsed", StrArr("Normalised search terms."))))),
            With()),

        new("adr_update", "Directly edit human-owned fields as an authorized human author. Accepted ADRs' decision content is immutable (supersede instead). Status changes and supersession require Approver permission. Use expectedEtag for optimistic concurrency.",
            Permission.AdrEdit, true, IdempotencyMode.Key, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, AdrId, ("fields", FieldsSchema("Fields to change; omitted fields stay unchanged.")), ("expectedEtag", Str("ETag from adr_get; the update fails with ETAG_MISMATCH if the ADR changed.", 200)), ("note", Str("Change note for the history.", 1000)), IdempotencyKey), "adrId", "fields"),
            Envelope(Obj(Props(("adr", AdrDetailSchema())))),
            With(ErrorCodes.AdrNotFound, ErrorCodes.EtagMismatch, ErrorCodes.InvalidTransition, ErrorCodes.Conflict)),

        new("adr_propose_change", "Propose changes to human-owned fields (title, context, decision, rationale, alternatives, consequences, status, owners, approvers, approvalResult, humanNotes, tags, claims, supersedes, supersededBy). Nothing is applied until a human approves via adr_approve_change. Use this for every analyzer- or assistant-suggested edit.",
            Permission.AdrPropose, true, IdempotencyMode.Key, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId,
                ("changes", Arr(Obj(Props(("field", Str("Human-owned field name.", 50)), ("proposedValue", Str("New value. Lists may be JSON arrays or newline-separated; claims must be a JSON array.", 20_000)), ("reason", Str("Why this change is proposed.", 2000))), "field", "proposedValue", "reason"), "Field changes (1-15)", 15)),
                ("proposerType", Str("'human' or 'agent'. Agent proposals carry lower default confidence.", enumValues: new[] { "human", "agent" })),
                ("confidence", Num("Proposer confidence 0..1 (agent proposals are capped at 0.6).", 0, 1)),
                IdempotencyKey), "adrId", "changes"),
            Envelope(Obj(Props(("proposal", ProposalSchema())))),
            With(ErrorCodes.AdrNotFound, ErrorCodes.InvalidTransition, ErrorCodes.HumanOwnedFieldProtected)),

        new("adr_approve_change", "Approve and apply a pending proposal (Approver role). Enforces four-eyes (proposer cannot approve), stale-field protection, lifecycle transitions and ETag concurrency. Creates a new human revision with full provenance.",
            Permission.AdrApproveChange, true, IdempotencyMode.Key, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, ("proposalId", Str("Proposal id.", 128)), ("note", Str("Approval note.", 1000)), ("expectedAdrEtag", Str("ADR ETag for concurrency.", 200)), IdempotencyKey), "proposalId"),
            Envelope(Obj(Props(("proposal", ProposalSchema()), ("adr", AdrSummary)))),
            With(ErrorCodes.NotFound, ErrorCodes.Conflict, ErrorCodes.EtagMismatch, ErrorCodes.InvalidTransition)),

        new("adr_reject_change", "Reject a pending proposal with a note (Approver role).",
            Permission.AdrApproveChange, true, IdempotencyMode.Key, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, ("proposalId", Str("Proposal id.", 128)), ("note", Str("Rejection note.", 1000)), IdempotencyKey), "proposalId"),
            Envelope(Obj(Props(("proposal", ProposalSchema())))),
            With(ErrorCodes.NotFound, ErrorCodes.Conflict)),

        new("adr_supersede", "Mark an ADR as Superseded by an Accepted ADR (human choice; Approver role). Both ADRs are updated and linked.",
            Permission.AdrSupersede, true, IdempotencyMode.Key, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, ("oldAdrId", Str("ADR being superseded.", 128)), ("newAdrId", Str("Accepted ADR that supersedes it.", 128)), ("note", Str("Note.", 1000)), IdempotencyKey), "oldAdrId", "newAdrId"),
            Envelope(Obj(Props(("old", AdrSummary), ("new", AdrSummary)))),
            With(ErrorCodes.AdrNotFound, ErrorCodes.InvalidTransition)),

        new("adr_archive", "Archive an ADR with a reason (Approver role). Archived ADRs are read-only and excluded from analysis.",
            Permission.AdrArchive, true, IdempotencyMode.Key, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, AdrId, ("reason", Str("Why it is archived.", 1000)), IdempotencyKey), "adrId", "reason"),
            Envelope(Obj(Props(("adr", AdrSummary)))),
            With(ErrorCodes.AdrNotFound, ErrorCodes.InvalidTransition)),

        new("adr_history", "List version snapshots (human and observed revisions) with provenance and content hashes, newest first.",
            Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, AdrId, ("take", Int("Max snapshots.", 1, 100, 20))), "adrId"),
            Envelope(Obj(Props(("versions", Arr(Obj(Props(("id", Str("")), ("humanRevision", Int("")), ("observedRevision", Int("")), ("changeKind", Str("")), ("capturedAt", Str("")), ("actor", Str("")), ("actorType", Str("")), ("contentHash", Str("")), ("summary", Str("")))), "Snapshots"))))),
            With(ErrorCodes.AdrNotFound)),

        new("adr_import", "Load existing decisions (up to 25 per call) in the lifecycle status they already have, keeping their ids and original decision dates. Approver role. Human-owned content is stored verbatim with the original Markdown as source of record. Never overwrites: an existing id is reported as unchanged or conflict. Use dryRun to validate first.",
            Permission.AdrImport, true, IdempotencyMode.Key, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId,
                ("records", Arr(Obj(Props(
                    ("id", Str("Id to keep, e.g. ADR-204001.", 128, "^[A-Za-z0-9._:-]+$")),
                    ("markdown", Str("Original ADR Markdown (<= 200,000 chars).", 200_000)),
                    ("fields", FieldsSchema("Structured fields applied over the parsed Markdown (status, tags, notes, supersession).")),
                    ("decidedOn", Str("Original decision date, yyyy-MM-dd. Becomes the created date.", 10, "^\\d{4}-\\d{2}-\\d{2}$")),
                    ("sourcePath", Str("Where the document came from; recorded in the history.", 500))), "id"), "Decisions to load (1-25)", 25)),
                ("dryRun", Bool("Validate and report what would happen without writing.", false)),
                IdempotencyKey), "records"),
            Envelope(Obj(Props(("dryRun", Bool("")), ("counts", Any("Number of records per outcome.")),
                ("results", Arr(Obj(Props(("id", Str("")), ("outcome", Str("created, would-create, unchanged, conflict or invalid")), ("message", Str("")))), "One result per record"))))),
            With(ErrorCodes.PayloadTooLarge)),

        new("adr_export", "Export an ADR as Markdown or JSON.",
            Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, AdrId, ("format", Str("markdown (default) or json.", enumValues: new[] { "markdown", "json" }))), "adrId"),
            Envelope(Obj(Props(("format", Str("")), ("content", Str("Exported content.")), ("contentHash", Str(""))))),
            With(ErrorCodes.AdrNotFound)),

        // ------------------------------------------------------------------ File ingestion
        new("file_upload_request", "Start a fast direct-to-Blob upload. Validates name, type, size and policy, then returns a short-lived, write-only, single-object upload URL. The file does NOT pass through MCP. Follow with file_register_upload.",
            Permission.EvidenceUpload, true, IdempotencyMode.Key, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId,
                ("fileName", Str("Original file name (path segments are stripped).", 255)),
                ("mediaType", Str("MIME type, e.g. text/plain, application/json, application/zip.", 120)),
                ("expectedSizeBytes", Int("Expected size in bytes (<= 50 MiB by default).", 1)),
                ("sha256", Str("Optional lower-case hex SHA-256 of the content for verification and duplicate detection.", 64, "^[a-fA-F0-9]{64}$")),
                AdrId, ("note", Str("Why this file is evidence.", 1000)), IdempotencyKey), "fileName", "mediaType", "expectedSizeBytes"),
            Envelope(Obj(Props(("uploadId", Str("Use in file_register_upload.")), ("uploadUrl", Str("Pre-authorized URL. Write-only, single blob, expires at expiresAt. Never log or share.")), ("method", Str("HTTP method (PUT).")), ("headers", Any("Headers that must be sent with the PUT.")), ("expiresAt", Str("ISO-8601 expiry.")), ("maxBytes", Int("Hard size limit.")), ("instructions", Str("Human-readable steps."))))),
            With(ErrorCodes.UnsupportedFileType, ErrorCodes.PayloadTooLarge, ErrorCodes.Conflict)),

        new("file_register_upload", "Finish an upload: verifies blob existence, size, SHA-256 and media type; quarantines; scans for secrets/PII/prompt-injection; writes a sanitized variant; promotes to immutable evidence; optionally attaches to an ADR; queues technology detection and contradiction analysis. Safe to repeat (returns the recorded outcome).",
            Permission.EvidenceUpload, true, IdempotencyMode.Natural, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, ("uploadId", Str("From file_upload_request.", 128)), ("sha256", Str("SHA-256 of what was uploaded, if not supplied earlier.", 64))), "uploadId"),
            Envelope(Obj(Props(("evidenceId", Str("Equals uploadId once approved.")), ("state", Str("Requested, Uploaded, Quarantined, Scanning, Approved, Rejected, Expired, Removed or Duplicate")), ("sha256", Str("")), ("sizeBytes", Int("")), ("mediaType", Str("")),
                ("safety", Obj(Props(("detections", Int("Count of masked/flagged items.")), ("bySeverity", Any("Counts by severity.")), ("blocked", Bool("")), ("promptInjectionSuspected", Bool("")), ("limitations", StrArr("Scanner limitations."))))),
                ("duplicateOfEvidenceId", Str("")), ("jobId", Str("Async analysis job.")), ("replayed", Bool("True when this upload had already been registered."))))),
            With(ErrorCodes.UploadNotFound, ErrorCodes.UploadExpired, ErrorCodes.ChecksumMismatch, ErrorCodes.PayloadTooLarge, ErrorCodes.UnsupportedFileType, ErrorCodes.ContentSafetyBlocked, ErrorCodes.UploadRejected)),

        new("file_import_from_repository", "Import files from an allow-listed Git host/organization as evidence (asynchronous job). Repository hosts and organizations are configured server-side; arbitrary URLs are rejected.",
            Permission.EvidenceUpload, true, IdempotencyMode.Key, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, ("repositoryUrl", Str("https URL of an allow-listed repository.", 500)), ("ref", Str("Branch, tag or commit.", 200)), ("pathGlobs", StrArr("Globs to include.", 20)), AdrId, IdempotencyKey), "repositoryUrl"),
            Envelope(Obj(Props(("job", Job)))),
            With(ErrorCodes.PolicyBlocked, ErrorCodes.NotImplemented)),

        new("evidence_attach", "Attach approved evidence to an ADR (system-managed evidence list, human action with provenance).",
            Permission.EvidenceUpload, true, IdempotencyMode.Key, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId, ("evidenceId", Str("Approved evidence id.", 128)), ("note", Str("Why it is relevant.", 1000)), IdempotencyKey), "adrId", "evidenceId"),
            Envelope(Obj(Props(("adrId", Str("")), ("evidenceCount", Int("")), ("evidence", Arr(Obj(Props(("evidenceId", Str("")), ("fileName", Str("")), ("mediaType", Str("")), ("sizeBytes", Int("")), ("sha256", Str("")), ("attachedAt", Str("")))), "Attached evidence"))))),
            With(ErrorCodes.AdrNotFound, ErrorCodes.NotFound, ErrorCodes.Conflict)),

        new("evidence_list", "List approved evidence in the workspace, optionally only for one ADR. Metadata only; never returns file contents.",
            Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, AdrId)),
            Envelope(Obj(Props(("items", Arr(Obj(Props(("evidenceId", Str("")), ("fileName", Str("")), ("mediaType", Str("")), ("sizeBytes", Int("")), ("sha256", Str("")), ("adrId", Str("")), ("state", Str("")), ("safetyDetections", Int("")), ("registeredAt", Str("")))), "Evidence"))))),
            With()),

        new("evidence_remove", "Detach evidence from an ADR with a reason (Approver role). The immutable evidence blob is retained for audit; only the link is removed.",
            Permission.EvidenceRemove, true, IdempotencyMode.Key, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, AdrId, ("evidenceId", Str("", 128)), ("reason", Str("", 1000)), IdempotencyKey), "adrId", "evidenceId", "reason"),
            Envelope(Obj(Props(("adrId", Str("")), ("evidenceCount", Int(""))))),
            With(ErrorCodes.AdrNotFound)),

        // ------------------------------------------------------------------ Analysis
        new("technology_detect", "Deterministically detect technologies and versions from inline files (<= 50, <= 512 KB each) or from the workspace's approved evidence. Nothing is persisted. Each result carries detector, confidence and evidence file.",
            Permission.AnalysisRun, false, IdempotencyMode.ReadOnly, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId, ("files", Arr(Obj(Props(("path", Str("Relative path (used to pick parsers).", 400)), ("content", Str("File content.", 524_288))), "path", "content"), "Inline files", 50)))),
            Envelope(Obj(Props(("technologies", Arr(TechnologySchema(), "Detected technologies")), ("detectors", StrArr("Detectors that ran")), ("filesScanned", Int(""))))),
            With(ErrorCodes.PayloadTooLarge)),

        new("technology_refresh", "Refresh the ADR's system-managed observed technologies from workspace evidence and run drift detection. Preserves first-seen timestamps, marks vanished items Removed, never edits the decision. Set async=true for large workspaces to get a job id.",
            Permission.AnalysisRun, true, IdempotencyMode.Key, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId, ("async", Bool("Run as a background job.", false)), IdempotencyKey), "adrId"),
            Envelope(Obj(Props(("adrId", Str("")), ("added", Int("")), ("updated", Int("")), ("removed", Int("")), ("technologies", Arr(TechnologySchema(), "Current observed technologies")), ("drift", Obj(Props(("overallStatus", Str("")), ("findings", Int("")), ("statusChanges", Int(""))))), ("job", Job)))),
            With(ErrorCodes.AdrNotFound, ErrorCodes.EtagMismatch)),

        new("adr_find_related_code", "Find code and infrastructure files related to an ADR via keyword and claim matching over workspace evidence.",
            Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, AdrId, ("take", Int("", 1, 100, 20))), "adrId"),
            Envelope(Obj(Props(("files", Arr(Obj(Props(("path", Str("")), ("relevance", Num("")), ("reason", Str("")))), ""))))),
            With(ErrorCodes.NotImplemented)),

        new("adr_detect_drift", "Evaluate an accepted ADR's declared and extracted claims against workspace evidence using detector plugins. Each claim is Compliant, Drifted, Uncertain, Unsupported or NotApplicable with evidence and confidence; prior results are preserved and status changes are flagged. The decision text is never altered.",
            Permission.AnalysisRun, true, IdempotencyMode.Natural, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId, ("async", Bool("Run as a background job.", false))), "adrId"),
            Envelope(Obj(Props(("adrId", Str("")), ("overallStatus", Str("")), ("statusChanges", Int("")), ("evidenceFiles", Int("")), ("detectors", StrArr("")), ("findings", Arr(DriftFindingSchema(), "Per-claim results")), ("job", Job)))),
            With(ErrorCodes.AdrNotFound)),

        new("adr_check_contradictions", "Hybrid contradiction check between an ADR (or all active ADRs) and other active/draft ADRs: normalize claims, deterministic candidate matching, scope/date/status filtering, optional semantic pass, explained findings with confidence and severity. Humans classify results with finding_review; ADR status is never changed.",
            Permission.AnalysisRun, true, IdempotencyMode.Natural, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId, ("includeDrafts", Bool("Compare against Draft/Proposed/UnderReview ADRs too.", true)), ("persist", Bool("Persist findings and link them to ADRs (default true).", true)))),
            Envelope(Obj(Props(("findings", Arr(ContradictionFindingSchema(), "Contradiction findings")), ("adrsCompared", Int("")), ("candidatesEvaluated", Int("")), ("semanticAnalyzerUsed", Bool("")), ("guidance", Str("How to proceed with needsSemanticReview findings."))))),
            With(ErrorCodes.AdrNotFound)),

        new("adr_safety_review", "Advisory review of an ADR (stored or inline) for security, availability, data-loss, privacy, compliance, lock-in, cost, EOL technology, scalability, operability, rollback/migration gaps, weak evidence, conflicts, irreversibility, blast radius and responsible-AI concerns. Separates evidence, inference, recommendation and policy; blocks only when organization policy says so.",
            Permission.AnalysisRun, true, IdempotencyMode.Natural, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, AdrId, ("inline", FieldsSchema("Review unsaved ADR content instead of a stored ADR.")), ("persist", Bool("Persist findings when adrId is given (default true).", true)))),
            Envelope(Obj(Props(("findings", Arr(SafetyFindingSchema(), "Findings ordered by severity")), ("humanReviewRequired", Bool("")), ("policyBlocksAcceptance", Bool("")), ("rulesEvaluated", StrArr(""))))),
            With(ErrorCodes.AdrNotFound)),

        new("finding_review", "Human classification of a finding: Confirmed, AcceptableException, FalsePositive or Resolved. Classifications persist across re-scans while the underlying result is unchanged.",
            Permission.FindingReview, true, IdempotencyMode.Key, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, ("findingId", Str("", 128)), ("status", Str("", enumValues: new[] { "Confirmed", "AcceptableException", "FalsePositive", "Resolved" })), ("note", Str("", 2000)), IdempotencyKey), "findingId", "status"),
            Envelope(Obj(Props(("finding", Finding)))),
            With(ErrorCodes.NotFound)),

        new("adr_impact_analysis", "Estimate the impact of changing or superseding an ADR: dependent ADRs, linked evidence, affected services.", Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, AdrId), "adrId"), Envelope(Obj(Props(("impact", Any(""))))), With(ErrorCodes.NotImplemented)),
        new("architecture_fitness_check", "Run executable fitness functions derived from accepted ADR constraints and return pass/fail/unknown with evidence.", Permission.AnalysisRun, false, IdempotencyMode.ReadOnly, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, ("ruleIds", StrArr("Rules to run; all when omitted.")), ("scope", Str("", 200)))), Envelope(Obj(Props(("results", Any("")), ("blocking", Bool(""))))), With(ErrorCodes.NotImplemented)),
        new("dependency_map_generate", "Generate a dependency map from evidence manifests (asynchronous).", Permission.AnalysisRun, true, IdempotencyMode.Key, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, IdempotencyKey)), Envelope(Obj(Props(("job", Job)))), With(ErrorCodes.NotImplemented)),
        new("c4_model_generate", "Generate C4 (context/container) diagrams in Mermaid from ADRs and evidence (asynchronous).", Permission.AnalysisRun, true, IdempotencyMode.Key, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, ("level", Str("", enumValues: new[] { "context", "container" })), IdempotencyKey)), Envelope(Obj(Props(("job", Job)))), With(ErrorCodes.NotImplemented)),

        // ------------------------------------------------------------------ Memory & governance
        new("decision_memory_search", "Search durable, tenant-isolated decision memory (prior tool calls, ADR references, findings, human feedback) with explainable keyword scoring.",
            Permission.MemoryRead, false, IdempotencyMode.ReadOnly, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, ("query", Str("", 500)), ("take", Int("", 1, 50, 10))), "query"),
            Envelope(Obj(Props(("hits", Arr(Obj(Props(("score", Num("")), ("explanation", Str("")), ("matchedTerms", StrArr("")), ("timestamp", Str("")), ("tool", Str("")), ("adrIds", StrArr("")), ("findingIds", StrArr("")), ("summary", Str("")))), ""))))),
            With()),
        new("decision_lineage_get", "Return the supersession chain and related decisions for an ADR.", Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, AdrId), "adrId"), Envelope(Obj(Props(("lineage", Any(""))))), With(ErrorCodes.NotImplemented)),
        new("architecture_rule_create", "Create an executable architecture rule (fitness function) from an accepted ADR claim (Admin).", Permission.RuleManage, true, IdempotencyMode.Key, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, AdrId, ("rule", Any("Rule definition.")), IdempotencyKey), "adrId", "rule"), Envelope(Obj(Props(("rule", Any(""))))), With(ErrorCodes.NotImplemented)),
        new("architecture_rule_validate", "Validate evidence against one rule.", Permission.AnalysisRun, false, IdempotencyMode.ReadOnly, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, ("ruleId", Str("", 128))), "ruleId"), Envelope(Obj(Props(("result", Any(""))))), With(ErrorCodes.NotImplemented)),
        new("governance_check", "Aggregate governance posture for a workspace: open contradictions, drift, blocked safety findings, stale ADRs.", Permission.AdrRead, false, IdempotencyMode.ReadOnly, ToolPhase.Later, false,
            Obj(Props(WorkspaceId)), Envelope(Obj(Props(("summary", Any(""))))), With(ErrorCodes.NotImplemented)),
        new("deployment_readiness_check", "Evaluate deployment readiness against blocking fitness functions and open critical findings.", Permission.AnalysisRun, false, IdempotencyMode.ReadOnly, ToolPhase.Later, false,
            Obj(Props(WorkspaceId, ("serviceId", Str("", 128)))), Envelope(Obj(Props(("ready", Bool("")), ("blockers", Any(""))))), With(ErrorCodes.NotImplemented)),

        // ------------------------------------------------------------------ Jobs
        new("analysis_job_get", "Get the state and progress of an asynchronous analysis job.", Permission.JobRead, false, IdempotencyMode.ReadOnly, ToolPhase.Mvp, true,
            Obj(Props(WorkspaceId, ("jobId", Str("", 128))), "jobId"), Envelope(Obj(Props(("job", Job)))), With(ErrorCodes.JobNotFound)),
        new("analysis_job_cancel", "Request cancellation of a queued or running job.", Permission.JobCancel, true, IdempotencyMode.Natural, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, ("jobId", Str("", 128))), "jobId"), Envelope(Obj(Props(("job", Job)))), With(ErrorCodes.JobNotFound, ErrorCodes.JobNotCancellable)),
        new("analysis_job_results", "Fetch the compact result of a completed job.", Permission.JobRead, false, IdempotencyMode.ReadOnly, ToolPhase.Extended, true,
            Obj(Props(WorkspaceId, ("jobId", Str("", 128))), "jobId"), Envelope(Obj(Props(("job", Job), ("result", Any("Job-type specific result."))))), With(ErrorCodes.JobNotFound)),
    };

    public static ToolDefinition Get(string name) => All.First(t => t.Name == name);
    public static IEnumerable<ToolDefinition> Implemented => All.Where(t => t.Implemented);

    private static JsonObject FieldsSchema(string description) => new JsonObject
    {
        ["type"] = "object",
        ["description"] = description,
        ["additionalProperties"] = false,
        ["properties"] = Props(
            ("title", Str("Title.", 300)), ("context", Str("Context / problem statement.", 20_000)), ("decision", Str("The decision.", 20_000)), ("rationale", Str("Why.", 20_000)),
            ("alternatives", StrArr("Considered alternatives.")), ("consequences", Str("Consequences.", 20_000)), ("status", Str("Lifecycle status.", enumValues: StatusValues)),
            ("owners", StrArr("Owners.")), ("approvers", StrArr("Approvers.")), ("approvalResult", Str("Approval outcome text.", 2000)), ("humanNotes", Str("Free notes.", 20_000)),
            ("tags", StrArr("Tags.")), ("claims", Arr(Claim, "Structured, testable claims that drive drift and contradiction detection.", 200)),
            ("supersedes", Str("ADR id this one supersedes.", 128)), ("supersededBy", Str("ADR id that supersedes this one.", 128)))
    };

    private static JsonObject AdrDetailSchema() => Obj(Props(
        ("summary", AdrSummary), ("context", Str("")), ("decision", Str("")), ("rationale", Str("")), ("alternatives", StrArr("")), ("consequences", Str("")),
        ("owners", StrArr("")), ("approvers", StrArr("")), ("approvalResult", Str("")), ("humanNotes", Str("")), ("claims", Arr(Claim, "")), ("supersedes", Str("")), ("supersededBy", Str("")),
        ("createdAt", Str("")), ("acceptedAt", Str("")), ("contentHash", Str("SHA-256 of canonical human-owned content.")),
        ("technologies", Arr(TechnologySchema(), "Top observed technologies (system-managed)")), ("technologyCount", Int("")), ("evidenceCount", Int("")), ("evidenceIds", StrArr("")), ("safetyFindingCount", Int("")), ("lastAnalyzedAt", Str("")), ("markdown", Str("Only when requested.")), ("provenance", Any("Last human change provenance."))));

    private static JsonObject TechnologySchema() => Obj(Props(("name", Str("Normalized name.")), ("version", Str("")), ("category", Str("")), ("status", Str("Observed, Inferred, Declared, Removed or Uncertain")), ("confidence", Num("")), ("basis", Str("")), ("sourceFile", Str("")), ("detector", Str("name@version")), ("firstObservedAt", Str("")), ("lastObservedAt", Str(""))));

    private static JsonObject ProposalSchema() => Obj(Props(("id", Str("")), ("adrId", Str("")), ("status", Str("Proposed, Approved, Rejected, Applied, Expired, Withdrawn")), ("baseHumanRevision", Int("")), ("proposedBy", Str("")), ("proposerType", Str("")), ("proposedAt", Str("")), ("expiresAt", Str("")), ("decidedBy", Str("")), ("decidedAt", Str("")), ("decisionNote", Str("")),
        ("changes", Arr(Obj(Props(("field", Str("")), ("proposedValuePreview", Str("First 300 chars.")), ("reason", Str("")))), ""))));

    private static JsonObject DriftFindingSchema() { var f = Finding; var p = (JsonObject)f["properties"]!; p["claimId"] = Str(""); p["claimText"] = Str(""); p["result"] = Str("Compliant, Drifted, Uncertain, Unsupported, NotApplicable"); p["previousResult"] = Str(""); p["statusChanged"] = Bool(""); p["detector"] = Str(""); return f; }
    private static JsonObject ContradictionFindingSchema() { var f = Finding; var p = (JsonObject)f["properties"]!; p["adrId"] = Str(""); p["otherAdrId"] = Str(""); p["claimA"] = Str(""); p["claimB"] = Str(""); p["contradictionType"] = Str(""); p["needsSemanticReview"] = Bool("True when the deterministic score is below 0.85 and no semantic analyzer ran; review the two claims and classify with finding_review."); return f; }
    private static JsonObject SafetyFindingSchema() { var f = Finding; var p = (JsonObject)f["properties"]!; p["category"] = Str(""); p["affectedQualityAttributes"] = StrArr(""); p["suggestedMitigations"] = StrArr(""); p["alternativesToConsider"] = StrArr(""); p["policyBlocksAcceptance"] = Bool(""); p["policyId"] = Str(""); p["observedEvidence"] = Str("What was found in the text."); p["inference"] = Str("What the rule inferred."); p["recommendation"] = Str("What to do."); return f; }
}
