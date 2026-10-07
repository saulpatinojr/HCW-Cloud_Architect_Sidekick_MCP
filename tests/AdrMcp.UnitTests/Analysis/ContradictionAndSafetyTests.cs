using System.Text.Json;
using AdrMcp.Analysis.Claims;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.UnitTests.Support;

namespace AdrMcp.UnitTests.Analysis;

/// <summary>Five+ contradiction scenarios including false-positive guards (scope, supersession, compatible families).</summary>
public class ContradictionScenarioTests
{
    private static object Claim(string id, string kind, string subject, string? predicate = null, string? obj = null, string? system = null, string? env = null)
        => new { claimId = id, kind, subject, predicate, @object = obj, scope = new { system, environment = env } };

    [Fact]
    public async Task Scenario1_prohibited_vs_required_same_scope_is_high_confidence_contradiction()
    {
        var host = new TestHost();
        var a = await host.CreateAdrAsync("Ban Newtonsoft", "Must not use Newtonsoft.Json.", claims: new[] { Claim("a1", "ProhibitedTechnology", "Newtonsoft.Json") });
        await host.AcceptAsync(a);
        var b = await host.CreateAdrAsync("Use Newtonsoft", "We will use Newtonsoft.Json for all serialization.", claims: new[] { Claim("b1", "RequiredTechnology", "Newtonsoft.Json") });
        var r = await host.CallAsync("adr_check_contradictions", new { adrId = b, includeDrafts = true });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        var findings = r.GetProperty("data").GetProperty("findings").EnumerateArray().ToList();
        var f = Assert.Single(findings, x => x.GetProperty("contradictionType").GetString() == "ProhibitedVsRequired");
        Assert.True(f.GetProperty("confidence").GetProperty("score").GetDouble() >= 0.9);
        Assert.Equal("Observed", f.GetProperty("confidence").GetProperty("basis").GetString());
        Assert.False(f.GetProperty("needsSemanticReview").GetBoolean());
        Assert.Equal("Open", f.GetProperty("reviewStatus").GetString());
        // ADR status untouched
        var adr = await host.CallAsync("adr_get", new { adrId = b });
        Assert.Equal("Draft", adr.GetProperty("data").GetProperty("adr").GetProperty("summary").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Scenario2_different_systems_are_not_a_contradiction()
    {
        var host = new TestHost();
        var a = await host.CreateAdrAsync("Orders: no MongoDB", "Orders must not use MongoDB.", claims: new[] { Claim("a1", "ProhibitedTechnology", "MongoDB", system: "Orders") });
        await host.AcceptAsync(a);
        var b = await host.CreateAdrAsync("Catalog: MongoDB", "Catalog must use MongoDB.", claims: new[] { Claim("b1", "RequiredTechnology", "MongoDB", system: "Catalog") });
        var r = await host.CallAsync("adr_check_contradictions", new { adrId = b });
        Assert.Equal(0, r.GetProperty("data").GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public async Task Scenario3_different_environments_and_predecessor_are_excluded()
    {
        var host = new TestHost();
        var a = await host.CreateAdrAsync("Prod sync", "Production integration is synchronous.", claims: new[] { Claim("a1", "IntegrationStyle", "integration", "is", "sync", env: "production") });
        await host.AcceptAsync(a);
        var b = await host.CreateAdrAsync("Dev async", "Development integration is asynchronous.", claims: new[] { Claim("b1", "IntegrationStyle", "integration", "is", "async", env: "development") });
        var r = await host.CallAsync("adr_check_contradictions", new { adrId = b });
        Assert.Equal(0, r.GetProperty("data").GetProperty("findings").GetArrayLength());

        // Same scope, but the new ADR supersedes the old one: expected disagreement, not a contradiction.
        var c = await host.CreateAdrAsync("Prod async", "Production integration is asynchronous.", claims: new[] { Claim("c1", "IntegrationStyle", "integration", "is", "async", env: "production") });
        await host.AcceptAsync(c);
        var r2 = await host.CallAsync("adr_check_contradictions", new { adrId = c });
        Assert.Equal(1, r2.GetProperty("data").GetProperty("findings").GetArrayLength());
        var sup = await host.CallAsync("adr_supersede", new { oldAdrId = a, newAdrId = c });
        Assert.True(sup.GetProperty("ok").GetBoolean(), sup.ToString());
        var r3 = await host.CallAsync("adr_check_contradictions", new { adrId = c });
        Assert.Equal(0, r3.GetProperty("data").GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public async Task Scenario4_database_ownership_conflict_and_human_classification_persists()
    {
        var host = new TestHost();
        var a = await host.CreateAdrAsync("Orders owns OrdersDb", "OrderService owns OrdersDb.", claims: new[] { Claim("a1", "DatabaseOwnership", "OrderService", "owns", "OrdersDb") });
        await host.AcceptAsync(a);
        var b = await host.CreateAdrAsync("Billing owns OrdersDb", "BillingService owns OrdersDb.", claims: new[] { Claim("b1", "DatabaseOwnership", "BillingService", "owns", "OrdersDb") });
        await host.AcceptAsync(b);
        var r = await host.CallAsync("adr_check_contradictions", new { adrId = b });
        var f = Assert.Single(r.GetProperty("data").GetProperty("findings").EnumerateArray());
        Assert.Equal("DatabaseOwnershipConflict", f.GetProperty("contradictionType").GetString());
        Assert.Equal("High", f.GetProperty("severity").GetString()); // both accepted + high confidence
        var fid = f.GetProperty("id").GetString();
        var review = await host.CallAsync("finding_review", new { findingId = fid, status = "AcceptableException", note = "Transitional shared DB until Q3." });
        Assert.True(review.GetProperty("ok").GetBoolean(), review.ToString());
        var again = await host.CallAsync("adr_check_contradictions", new { adrId = b });
        var f2 = Assert.Single(again.GetProperty("data").GetProperty("findings").EnumerateArray());
        Assert.Equal("AcceptableException", f2.GetProperty("reviewStatus").GetString());
        Assert.False(f2.GetProperty("requiresHumanReview").GetBoolean());
    }

    [Fact]
    public async Task Scenario5_text_extracted_claims_are_inferred_with_capped_confidence_and_compatible_auth_needs_semantic_review()
    {
        var host = new TestHost();
        var a = await host.CreateAdrAsync("Azure hosting", "All services will be deployed to Azure. Authentication via Microsoft Entra ID.");
        await host.AcceptAsync(a);
        var b = await host.CreateAdrAsync("AWS hosting", "The platform will be hosted on AWS. Users authenticate with OAuth 2.0.");
        var r = await host.CallAsync("adr_check_contradictions", new { adrId = b });
        var findings = r.GetProperty("data").GetProperty("findings").EnumerateArray().ToList();
        var cloud = Assert.Single(findings, f => f.GetProperty("contradictionType").GetString() == "CloudProviderConflict");
        Assert.Equal("Inferred", cloud.GetProperty("confidence").GetProperty("basis").GetString());
        Assert.True(cloud.GetProperty("confidence").GetProperty("score").GetDouble() <= 0.6);
        var auth = Assert.Single(findings, f => f.GetProperty("contradictionType").GetString() == "AuthStrategyConflict");
        Assert.True(auth.GetProperty("needsSemanticReview").GetBoolean());
        Assert.Contains("finding_review", r.GetProperty("data").GetProperty("guidance").GetString());
    }

    [Fact]
    public async Task Scenario6_data_residency_subregion_is_compatible_but_different_regions_conflict()
    {
        var host = new TestHost();
        var eu = await host.CreateAdrAsync("EU data", "Customer data must reside in the EU.", claims: new[] { Claim("a1", "DataResidency", "data", "resides in", "EU") });
        await host.AcceptAsync(eu);
        var de = await host.CreateAdrAsync("German data", "Customer data must reside in Germany.", claims: new[] { Claim("b1", "DataResidency", "data", "resides in", "Germany") });
        Assert.Equal(0, (await host.CallAsync("adr_check_contradictions", new { adrId = de, includeDrafts = false })).GetProperty("data").GetProperty("findings").GetArrayLength());
        var us = await host.CreateAdrAsync("US data", "Customer data must reside in the US.", claims: new[] { Claim("c1", "DataResidency", "data", "resides in", "US") });
        Assert.Equal(1, (await host.CallAsync("adr_check_contradictions", new { adrId = us, includeDrafts = false })).GetProperty("data").GetProperty("findings").GetArrayLength()); // EU vs US only
        Assert.Equal(2, (await host.CallAsync("adr_check_contradictions", new { adrId = us, includeDrafts = true })).GetProperty("data").GetProperty("findings").GetArrayLength()); // + Germany draft vs US
    }
}

public class ClaimExtractorTests
{
    private static Adr Adr(string decision) => new() { Id = "x", TenantId = "t", WorkspaceId = "w", AdrNumber = 1, Human = new HumanOwnedContent { Title = "t", Decision = decision }, CreatedAt = DateTimeOffset.UtcNow, Provenance = Provenance.Human("a", DateTimeOffset.UtcNow, "c") };

    [Fact]
    public void Extracts_prohibitions_requirements_ownership_and_infrastructure_claims_as_inferred()
    {
        var claims = new RegexClaimExtractor().Extract(Adr("We must not use Newtonsoft.Json. We will adopt Azure Service Bus for messaging. OrderService owns the OrdersDb database. All storage accounts must have private endpoints. Services communicate asynchronously in production."));
        Assert.All(claims, c => Assert.False(c.Declared));
        Assert.Contains(claims, c => c.Kind == ClaimKind.ProhibitedTechnology && c.Subject == "Newtonsoft.Json");
        Assert.Contains(claims, c => c.Kind == ClaimKind.RequiredTechnology && c.Subject.StartsWith("Azure Service Bus"));
        Assert.Contains(claims, c => c.Kind == ClaimKind.DatabaseOwnership && c.Subject == "OrderService" && c.Object == "OrdersDb");
        Assert.Contains(claims, c => c.Kind == ClaimKind.InfrastructureRequirement && c.Subject == "storage accounts" && c.Object == "private endpoints");
        Assert.Contains(claims, c => c.Kind == ClaimKind.IntegrationStyle && c.Object == "async" && c.Scope.Environment == "production");
    }
}

public class SafetyReviewTests
{
    [Fact]
    public async Task Risky_adr_produces_structured_advisory_findings_without_blocking()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_safety_review", new
        {
            inline = new
            {
                title = "Move all services to a single VM with basic auth",
                context = "Cost pressure",
                decision = "All services will run on a single server in production using .NET 6 and basic authentication over http://api.internal. Customer PII is stored without backups. This change is irreversible.",
                rationale = "Cheap.",
                consequences = "Faster."
            }
        });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        var d = r.GetProperty("data");
        var findings = d.GetProperty("findings").EnumerateArray().ToList();
        string[] categories = findings.Select(f => f.GetProperty("category").GetString()!).Distinct().ToArray();
        Assert.Contains("Security", categories);
        Assert.Contains("UnsupportedTechnology", categories);
        Assert.Contains("Availability", categories);
        Assert.Contains("Privacy", categories);
        Assert.Contains("Operability", categories);
        Assert.Contains("WeakEvidence", categories);
        Assert.Contains("Risk", categories);
        Assert.True(d.GetProperty("humanReviewRequired").GetBoolean());
        Assert.False(d.GetProperty("policyBlocksAcceptance").GetBoolean()); // advisory by default
        Assert.All(findings, f =>
        {
            Assert.Equal("Inferred", f.GetProperty("confidence").GetProperty("basis").GetString());
            Assert.False(string.IsNullOrEmpty(f.GetProperty("observedEvidence").GetString()));
            Assert.False(string.IsNullOrEmpty(f.GetProperty("recommendation").GetString()));
        });
    }

    [Fact]
    public async Task Policy_can_make_a_rule_blocking()
    {
        var host = new TestHost(s => s["SafetyPolicy:BlockingRuleIds:0"] = "SR-SEC-001");
        var r = await host.CallAsync("adr_safety_review", new { inline = new { title = "x", decision = "Use basic auth for the public API.", rationale = "Simple enough for now and documented thoroughly in the wiki.", alternatives = new[] { "OAuth" }, consequences = "Rollback by config. Monitoring via alerts." } });
        Assert.True(r.GetProperty("data").GetProperty("policyBlocksAcceptance").GetBoolean());
        Assert.Contains(r.GetProperty("data").GetProperty("findings").EnumerateArray(), f => f.GetProperty("policyBlocksAcceptance").GetBoolean() && f.GetProperty("policyId").GetString() == "org-default");
    }

    [Fact]
    public async Task Well_formed_adr_has_few_findings()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_safety_review", new { inline = new { title = "Adopt zone-redundant storage", decision = "Use ZRS for all production storage accounts with soft delete enabled.", rationale = "Zone failures have caused two incidents; ZRS removes the single-zone dependency at modest cost (estimate: +$120/month).", alternatives = new[] { "LRS with manual failover", "GRS" }, consequences = "Slightly higher cost. Rollback: switch SKU back. Monitoring via Azure Monitor alerts on availability." } });
        Assert.True(r.GetProperty("data").GetProperty("findings").GetArrayLength() <= 1);
    }
}
