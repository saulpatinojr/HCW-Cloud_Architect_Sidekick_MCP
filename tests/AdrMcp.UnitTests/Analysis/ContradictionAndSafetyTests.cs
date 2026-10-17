using System.Text.Json;
using AdrMcp.Analysis.Claims;
using AdrMcp.Analysis.SafetyReview;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Findings;
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

/// <summary>SR-EOL-001 compares .NET end-of-support dates with the review date (SafetyReviewInput.Now, from IClock), pinned here.</summary>
public class EolTechnologyRuleTests
{
    private static IReadOnlyList<SafetyFinding> Evaluate(string decision, DateTimeOffset now)
    {
        var adr = new Adr { Id = "x", TenantId = "t", WorkspaceId = "w", AdrNumber = 1, Human = new HumanOwnedContent { Title = "Runtime", Decision = decision }, CreatedAt = now, Provenance = Provenance.Human("a", now, "c") };
        SafetyFinding Create(string summary) => new()
        {
            Id = "saf_test", TenantId = "t", WorkspaceId = "w", AdrId = adr.Id, Kind = FindingKind.Safety, Severity = FindingSeverity.Medium,
            Confidence = Confidence.Inferred(0.5), Summary = summary, Provenance = Provenance.System("SR-EOL-001", "test", now, "c"), FingerprintKey = summary, Category = "UnsupportedTechnology"
        };
        return new EolTechnologyRule().Evaluate(new SafetyReviewInput(adr, Array.Empty<Adr>(), null, now), Create).ToList();
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("We will run on .NET 7.", "2024-05-15T00:00:00Z")]
    [InlineData("<TargetFramework>net7.0</TargetFramework>", "2024-05-15T00:00:00Z")]
    [InlineData("Target .NET 7.0 for the API.", "2026-10-17T12:00:00Z")]
    [InlineData("Keep net7.0 for now.", "2030-01-01T00:00:00Z")]
    public void Net7_is_end_of_life_after_2024_05_14(string decision, string now)
    {
        var f = Assert.Single(Evaluate(decision, At(now)));
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Equal("Decision references end-of-life or deprecated technology: .NET 7 (end of support 2024-05-14)", f.Summary);
        Assert.Equal("UnsupportedTechnology", f.Category);
        Assert.Equal(EvidenceBasis.Inferred, f.Confidence.Basis);
        Assert.True(f.Confidence.Score <= Confidence.InferredCeiling);
        Assert.StartsWith("Text mentions '", f.ObservedEvidence);
        Assert.True(f.RequiresHumanReview);
    }

    [Theory] // Forms the previous static pattern matched, plus dotnet/TFM spellings; all EOL long before 2026-10-17.
    [InlineData(".NET 6", ".NET 6", ".NET 6")]
    [InlineData(".NET6", ".NET 6", ".NET6")]
    [InlineData(".NET 6.0.36", ".NET 6", ".NET 6.0")]
    [InlineData("dotnet 6", ".NET 6", "dotnet 6")]
    [InlineData("net6.0-windows", ".NET 6", "net6.0")]
    [InlineData(".NET 5.0", ".NET 5", ".NET 5.0")]
    [InlineData(".NET Core 3.1", ".NET Core 3.1", ".NET Core 3.1")]
    [InlineData("ASP.NET Core 3.1", ".NET Core 3.1", ".NET Core 3.1")]
    [InlineData("netcoreapp3.1", ".NET Core 3.1", "netcoreapp3.1")]
    [InlineData("dotnet core 3.0", ".NET Core 3.0", "dotnet core 3.0")]
    [InlineData(".net core 2.2", ".NET Core 2.2", ".net core 2.2")]
    [InlineData(".NET 2.1", ".NET Core 2.1", ".NET 2.1")]
    public void Previously_matched_and_new_spellings_are_flagged(string mention, string release, string excerpt)
    {
        var f = Assert.Single(Evaluate($"The service uses {mention} in production.", At("2026-10-17T12:00:00Z")));
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Contains($": {release} (end of support ", f.Summary);
        Assert.Equal($"Text mentions '{excerpt}'.", f.ObservedEvidence);
    }

    [Theory] // Warning window: 90 days before the last supported day (2026-08-12) through 2026-11-10 inclusive; EOL from 2026-11-11 UTC.
    [InlineData("2026-08-11T23:59:59Z", null)]
    [InlineData("2026-08-12T00:00:00Z", FindingSeverity.Medium)]
    [InlineData("2026-10-17T12:00:00Z", FindingSeverity.Medium)]
    [InlineData("2026-11-10T23:59:59Z", FindingSeverity.Medium)]
    [InlineData("2026-11-11T00:00:00Z", FindingSeverity.High)]
    [InlineData("2026-11-10T20:00:00-05:00", FindingSeverity.High)] // 2026-11-11T01:00Z: the UTC date decides
    public void Net8_warns_inside_the_window_and_is_end_of_life_from_2026_11_11(string now, FindingSeverity? expected)
    {
        foreach (var mention in new[] { ".NET 8", ".NET 8.0", "net8.0", "dotnet 8" })
        {
            var findings = Evaluate($"All functions run on {mention} isolated.", At(now));
            if (expected is null) { Assert.Empty(findings); continue; }
            var f = Assert.Single(findings);
            Assert.Equal(expected, f.Severity);
            Assert.Equal(EvidenceBasis.Inferred, f.Confidence.Basis);
            Assert.Equal($"Text mentions '{mention}'.", f.ObservedEvidence);
            Assert.Equal(expected == FindingSeverity.High
                ? "Decision references end-of-life or deprecated technology: .NET 8 (end of support 2026-11-10)"
                : "Decision references technology approaching end of support: .NET 8 (end of support 2026-11-10)", f.Summary);
            Assert.Contains(".NET 10", f.Recommendation);
        }
    }

    [Fact]
    public void Net8_on_2026_10_17_is_a_warning_with_days_left_not_end_of_life()
    {
        var f = Assert.Single(Evaluate("We standardise on .NET 8 (LTS).", At("2026-10-17T09:00:00Z")));
        Assert.Equal(FindingSeverity.Medium, f.Severity);
        Assert.DoesNotContain("end-of-life", f.Summary);
        Assert.Contains(".NET 8 is supported through 2026-11-10 (24 days left)", f.Inference);
        Assert.Equal("Plan the upgrade to .NET 10 (LTS until 2028-11-14) before 2026-11-10.", f.Recommendation);
        Assert.Contains("today is the last day", Assert.Single(Evaluate("We use .NET 8.", At("2026-11-10T08:00:00Z"))).Inference);
    }

    [Fact]
    public void Net9_shares_the_net8_end_of_support_date()
    {
        var warn = Assert.Single(Evaluate("Run .NET 8 for the API and .NET 9 for the worker.", At("2026-10-17T12:00:00Z")));
        Assert.Equal(FindingSeverity.Medium, warn.Severity);
        Assert.EndsWith(".NET 8 (end of support 2026-11-10), .NET 9 (end of support 2026-11-10)", warn.Summary);
        var eol = Assert.Single(Evaluate("<TargetFramework>net9.0</TargetFramework>", At("2026-11-11T00:00:00Z")));
        Assert.Equal(FindingSeverity.High, eol.Severity);
    }

    [Theory]
    [InlineData(".NET 10")]
    [InlineData(".NET 10.0 LTS")]
    [InlineData("net10.0")]
    [InlineData("dotnet 10")]
    [InlineData(".NET 11")]
    [InlineData(".NET Framework 4.8")]
    [InlineData("net48 and net472")]
    [InlineData("Microsoft.NET.Sdk.Web")]
    [InlineData("a net 5 million budget")]
    public void Supported_or_unknown_versions_are_not_flagged(string mention)
    {
        Assert.Empty(Evaluate($"The platform targets {mention}.", At("2026-10-17T12:00:00Z")));
        Assert.Empty(Evaluate($"The platform targets {mention}.", At("2026-11-11T00:00:00Z")));
    }

    [Fact]
    public void Eol_and_approaching_releases_are_reported_separately_and_never_recommend_an_eol_release()
    {
        const string text = "Migrate the API from .NET 6 to .NET 8, then to .NET 10.";
        var before = Evaluate(text, At("2026-10-17T12:00:00Z"));
        Assert.Equal(2, before.Count);
        Assert.Contains(before, f => f.Severity == FindingSeverity.High && f.Summary.EndsWith(".NET 6 (end of support 2024-11-12)"));
        Assert.Contains(before, f => f.Severity == FindingSeverity.Medium && f.Summary.EndsWith(".NET 8 (end of support 2026-11-10)"));

        var after = Assert.Single(Evaluate(text, At("2026-11-11T00:00:00Z")));
        Assert.Equal(FindingSeverity.High, after.Severity);
        Assert.EndsWith(".NET 6 (end of support 2024-11-12), .NET 8 (end of support 2026-11-10)", after.Summary);
        Assert.Equal("Text mentions '.NET 6', '.NET 8'.", after.ObservedEvidence);
        Assert.Equal("Upgrade to a supported LTS (.NET 10 until 2028-11-14).", after.Recommendation);
    }
}
