using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Application.Tools;
using AdrMcp.Domain.Tenancy;
using AdrMcp.UnitTests.Support;

namespace AdrMcp.UnitTests.Contracts;

/// <summary>Contract tests over the tool catalog and the live request/response shapes.</summary>
public class ToolContractTests
{
    public static IEnumerable<object[]> Tools() => ToolCatalog.All.Select(t => new object[] { t.Name });

    [Theory]
    [MemberData(nameof(Tools))]
    public void Every_tool_has_valid_schemas_description_and_failure_codes(string name)
    {
        var t = ToolCatalog.Get(name);
        Assert.False(string.IsNullOrWhiteSpace(t.Description));
        Assert.True(t.Description.Length < 700, "Descriptions must stay compact for the client context window.");
        var input = JsonNode.Parse(t.InputSchemaJson)!.AsObject();
        Assert.Equal("object", input["type"]!.ToString());
        Assert.False(input["additionalProperties"]!.GetValue<bool>());
        Assert.True(input["properties"]!.AsObject().ContainsKey("workspaceId"));
        var output = JsonNode.Parse(t.OutputSchemaJson)!.AsObject();
        Assert.True(output["properties"]!.AsObject().ContainsKey("ok"));
        Assert.True(output["properties"]!.AsObject().ContainsKey("error"));
        Assert.Contains("VALIDATION_FAILED", t.FailureCodes);
        if (t.Mutating && t.Idempotency == IdempotencyMode.Key) Assert.True(input["properties"]!.AsObject().ContainsKey("idempotencyKey"), $"{name} should accept idempotencyKey");
        if (!t.Mutating) Assert.Equal(IdempotencyMode.ReadOnly, t.Idempotency);
        if (t.Mutating) Assert.NotEqual(Permission.AdrRead, t.Permission);
    }

    [Fact]
    public void Required_mvp_tools_are_implemented()
    {
        string[] mvp = { "adr_create", "adr_get", "adr_list", "adr_search", "adr_propose_change", "adr_approve_change", "file_upload_request", "file_register_upload", "evidence_attach", "technology_detect", "technology_refresh", "adr_check_contradictions", "adr_safety_review", "adr_detect_drift", "analysis_job_get" };
        foreach (var name in mvp) Assert.True(ToolCatalog.Get(name).Implemented && ToolCatalog.Get(name).Phase == ToolPhase.Mvp, name);
        Assert.Equal(ToolCatalog.All.Select(t => t.Name).Distinct().Count(), ToolCatalog.All.Count);
    }

    [Fact]
    public void Tool_names_follow_snake_case_and_match_functions()
    {
        foreach (var t in ToolCatalog.All) Assert.Matches("^[a-z][a-z0-9_]+$", t.Name);
        // Each implemented tool must have exactly one [McpToolTrigger] in the Functions assembly with the same name.
        var functionsAssembly = typeof(AdrMcp.Functions.Mcp.McpToolAdapter).Assembly;
        var triggerNames = functionsAssembly.GetTypes().SelectMany(t => t.GetMethods()).SelectMany(m => m.GetParameters())
            .SelectMany(p => p.GetCustomAttributes(true)).Where(a => a.GetType().Name == "McpToolTriggerAttribute")
            .Select(a => (string)a.GetType().GetProperty("ToolName")!.GetValue(a)!).ToList();
        foreach (var t in ToolCatalog.Implemented) Assert.Contains(t.Name, triggerNames);
        foreach (var n in triggerNames) Assert.True(ToolCatalog.Get(n).Implemented, $"Trigger {n} exists but catalog says not implemented");
    }

    [Fact]
    public async Task Error_envelope_shape_is_stable()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_get", new { adrId = "adr-9999" });
        Assert.False(r.GetProperty("ok").GetBoolean());
        var e = r.GetProperty("error");
        Assert.Equal("ADR_NOT_FOUND", e.GetProperty("code").GetString());
        Assert.False(e.GetProperty("retryable").GetBoolean());
        Assert.Equal(32, e.GetProperty("correlationId").GetString()!.Length);
        Assert.Equal("adr_get", r.GetProperty("meta").GetProperty("tool").GetString());
        var v = await host.CallAsync("adr_search", new { query = "" });
        Assert.Equal("VALIDATION_FAILED", v.GetProperty("error").GetProperty("code").GetString());
        var bad = await host.CallAsync("adr_list", new { take = "not-a-number" });
        Assert.Equal("VALIDATION_FAILED", bad.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Correlation_id_header_is_honoured_and_unsafe_values_ignored()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_list", new { }, new Dictionary<string, string> { ["x-correlation-id"] = "req-123" });
        Assert.Equal("req-123", r.GetProperty("meta").GetProperty("correlationId").GetString());
        var r2 = await host.CallAsync("adr_list", new { }, new Dictionary<string, string> { ["x-correlation-id"] = "<script>alert(1)</script>" });
        Assert.DoesNotContain("<", r2.GetProperty("meta").GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Markdown_round_trip_and_export()
    {
        var host = new TestHost();
        var md = "# Use event sourcing for Orders\n\n- Status: Proposed\n- Tags: orders, persistence\n\n## Context\nWe need an audit trail.\n\n## Decision\nWe will use event sourcing in the Orders bounded context.\n\n## Considered Options\n- CRUD with audit table\n- Event sourcing\n\n## Consequences\nMore complex reads. Rollback: snapshot and replay.\n";
        var r = await host.CallAsync("adr_create", new { markdown = md });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        var adr = r.GetProperty("data").GetProperty("adr");
        Assert.Equal("Use event sourcing for Orders", adr.GetProperty("summary").GetProperty("title").GetString());
        Assert.Equal("Proposed", adr.GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(2, adr.GetProperty("alternatives").GetArrayLength());
        Assert.Contains("orders", adr.GetProperty("summary").GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        var id = adr.GetProperty("summary").GetProperty("id").GetString();
        var exp = await host.CallAsync("adr_export", new { adrId = id, format = "markdown" });
        Assert.Contains("## Decision", exp.GetProperty("data").GetProperty("content").GetString());
        Assert.NotNull(host.Blobs.GetText(AdrMcp.Application.Abstractions.EvidenceContainer.AdrSource, $"tenant-a/ws-1/{id}/h1.md"));
    }
}

public class MemoryAndJobTests
{
    [Fact]
    public async Task Memory_records_tool_calls_with_keywords_and_explainable_scores()
    {
        var host = new TestHost();
        var id = await host.CreateAdrAsync("Adopt Kubernetes for workloads", "We will adopt Kubernetes via AKS.");
        var r = await host.CallAsync("decision_memory_search", new { query = "kubernetes aks" });
        var hit = r.GetProperty("data").GetProperty("hits").EnumerateArray().First();
        Assert.Contains("matched", hit.GetProperty("explanation").GetString());
        Assert.Contains(id, hit.GetProperty("adrIds").EnumerateArray().Select(x => x.GetString()));
        Assert.All(host.Memory.Records, m => Assert.NotEqual("dev-user", m.PrincipalHash));
    }

    [Fact]
    public async Task Async_drift_returns_job_and_job_lifecycle_works()
    {
        var host = new TestHost();
        var id = await host.CreateAdrAsync("x", "Must not use MongoDB.");
        var r = await host.CallAsync("adr_detect_drift", new { adrId = id, async = true });
        var jobId = r.GetProperty("data").GetProperty("job").GetProperty("jobId").GetString();
        Assert.Equal("Queued", r.GetProperty("data").GetProperty("job").GetProperty("state").GetString());
        var dup = await host.CallAsync("adr_detect_drift", new { adrId = id, async = true });
        Assert.Equal(jobId, dup.GetProperty("data").GetProperty("job").GetProperty("jobId").GetString()); // dedupe
        await host.DrainJobsAsync();
        var res = await host.CallAsync("analysis_job_results", new { jobId });
        Assert.Equal("Succeeded", res.GetProperty("data").GetProperty("job").GetProperty("state").GetString());
        Assert.Equal(id, res.GetProperty("data").GetProperty("result").GetProperty("adrId").GetString());
        var cancel = await host.CallAsync("analysis_job_cancel", new { jobId });
        Assert.Equal("JOB_NOT_CANCELLABLE", cancel.GetProperty("error").GetProperty("code").GetString());
    }
}
