using System.Text.Json.Nodes;

namespace AdrMcp.Application.Tools;

/// <summary>Tiny JSON Schema (draft 2020-12 compatible subset) builder so tool contracts stay readable and consistent.</summary>
public static class S
{
    public static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray());
        return o;
    }

    public static JsonObject Props(params (string Name, JsonNode Schema)[] props)
    {
        var o = new JsonObject();
        foreach (var (name, schema) in props) o[name] = schema;
        return o;
    }

    public static JsonObject Str(string description, int? maxLength = null, string? pattern = null, string[]? enumValues = null)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (maxLength is not null) o["maxLength"] = maxLength;
        if (pattern is not null) o["pattern"] = pattern;
        if (enumValues is not null) o["enum"] = new JsonArray(enumValues.Select(e => (JsonNode)e!).ToArray());
        return o;
    }

    public static JsonObject Int(string description, int? min = null, int? max = null, int? @default = null)
    {
        var o = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (min is not null) o["minimum"] = min;
        if (max is not null) o["maximum"] = max;
        if (@default is not null) o["default"] = @default;
        return o;
    }

    public static JsonObject Num(string description, double? min = null, double? max = null)
    {
        var o = new JsonObject { ["type"] = "number", ["description"] = description };
        if (min is not null) o["minimum"] = min;
        if (max is not null) o["maximum"] = max;
        return o;
    }

    public static JsonObject Bool(string description, bool? @default = null)
    {
        var o = new JsonObject { ["type"] = "boolean", ["description"] = description };
        if (@default is not null) o["default"] = @default;
        return o;
    }

    public static JsonObject Arr(JsonNode items, string description, int? maxItems = null)
    {
        var o = new JsonObject { ["type"] = "array", ["items"] = items, ["description"] = description };
        if (maxItems is not null) o["maxItems"] = maxItems;
        return o;
    }

    public static JsonObject StrArr(string description, int maxItems = 50) => Arr(new JsonObject { ["type"] = "string", ["maxLength"] = 500 }, description, maxItems);

    public static JsonObject Any(string description) => new() { ["description"] = description };

    // Shared fragments
    public static (string, JsonNode) WorkspaceId => ("workspaceId", Str("Workspace to operate in. Optional; defaults to the caller's default workspace. Must be a workspace the caller is authorized for.", 64, "^[A-Za-z0-9._-]+$"));
    public static (string, JsonNode) IdempotencyKey => ("idempotencyKey", Str("Client-supplied key. Repeating the same call with the same key and payload returns the original result instead of repeating the mutation.", 128));
    public static (string, JsonNode) AdrId => ("adrId", Str("ADR identifier, e.g. adr-0007.", 128, "^[A-Za-z0-9._:-]+$"));
    public static readonly string[] StatusValues = { "Draft", "Proposed", "UnderReview", "Accepted", "Rejected", "Deprecated", "Superseded", "Archived" };
    public static readonly string[] ClaimKinds = { "ProhibitedTechnology", "RequiredTechnology", "InfrastructureRequirement", "DatabaseOwnership", "IntegrationStyle", "CloudProvider", "AuthStrategy", "DataResidency", "DataRetention", "Other" };

    public static JsonObject Claim => Obj(Props(
        ("claimId", Str("Stable id for the claim within the ADR.", 64)),
        ("kind", Str("Claim kind.", enumValues: ClaimKinds)),
        ("subject", Str("What the claim is about, e.g. 'Newtonsoft.Json', 'storage accounts', 'OrderService'.", 200)),
        ("predicate", Str("Relation, e.g. 'must have', 'owns'.", 100)),
        ("object", Str("Target value, e.g. 'private endpoint', 'OrdersDb', 'async'.", 200)),
        ("scope", Obj(Props(
            ("system", Str("System the claim applies to; omit for all.", 100)),
            ("boundedContext", Str("Bounded context; omit for all.", 100)),
            ("environment", Str("Environment such as production; omit for all.", 50)),
            ("effectiveFrom", Str("ISO-8601 date-time.")),
            ("effectiveTo", Str("ISO-8601 date-time."))))),
        ("sourceText", Str("Sentence in the decision this claim comes from.", 1000))
    ), "claimId", "kind", "subject");

    public static JsonObject Envelope(JsonObject data) => Obj(Props(
        ("ok", Bool("True when the call succeeded.")),
        ("data", data),
        ("error", Obj(Props(("code", Str("Stable error code.")), ("message", Str("Human-readable message, redacted.")), ("retryable", Bool("Whether retrying may succeed.")), ("correlationId", Str("Correlation id for support.")), ("details", Any("Optional structured details."))))),
        ("meta", Obj(Props(("correlationId", Str("Correlation id.")), ("tool", Str("Tool name.")), ("durationMs", Int("Server duration.")), ("warnings", StrArr("Non-fatal warnings.")), ("idempotentReplay", Bool("True when a stored result was returned for a repeated idempotency key.")))))
    ), "ok", "meta");

    public static JsonObject AdrSummary => Obj(Props(
        ("id", Str("ADR id.")), ("adrNumber", Int("Sequence number within workspace.")), ("title", Str("Title.")), ("status", Str("Lifecycle status.", enumValues: StatusValues)),
        ("tags", StrArr("Tags.")), ("updatedAt", Str("ISO-8601.")), ("humanRevision", Int("Human-owned revision.")), ("observedRevision", Int("System-managed revision.")),
        ("driftStatus", Str("NotScanned, Compliant, Drifted, Uncertain, Unsupported or NoClaims.")), ("openContradictions", Int("Open contradiction findings.")), ("openDriftFindings", Int("Open drift findings.")),
        ("etag", Str("Concurrency token; pass back as expectedEtag on updates."))));

    public static JsonObject Finding => Obj(Props(
        ("id", Str("Finding id.")), ("kind", Str("Contradiction, Drift, Safety or Governance.")), ("severity", Str("Info..Critical.")),
        ("confidence", Obj(Props(("score", Num("0..1")), ("basis", Str("Observed, Declared, Inferred or Uncertain.")), ("rationale", Str("Why."))))),
        ("summary", Str("One-line summary.")), ("explanation", Str("Reasoning with evidence references.")), ("reviewStatus", Str("Open, Confirmed, AcceptableException, FalsePositive, Resolved.")),
        ("requiresHumanReview", Bool("Whether a human must classify this finding.")),
        ("evidence", Arr(Obj(Props(("evidenceId", Str("")), ("sourcePath", Str("")), ("excerpt", Str("Sanitized, truncated excerpt.")), ("lineNumber", Int("")))), "Supporting evidence"))));

    public static JsonObject Job => Obj(Props(
        ("jobId", Str("Job id.")), ("type", Str("Job type.")), ("state", Str("Queued, Running, Succeeded, Failed, Cancelled, Expired, Poisoned.")),
        ("progressPercent", Int("0..100")), ("statusMessage", Str("")), ("createdAt", Str("")), ("completedAt", Str("")), ("errorCode", Str("")), ("expiresAt", Str("When the result is purged."))));
}
