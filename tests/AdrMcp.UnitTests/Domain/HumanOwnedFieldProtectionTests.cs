using AdrMcp.Application.Services;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AdrMcp.UnitTests.Domain;

/// <summary>Proves that automated analysis has no write path to human-owned fields.</summary>
public class HumanOwnedFieldProtectionTests
{
    private static Adr NewAdr() => new()
    {
        Id = "adr-0001", TenantId = "t", WorkspaceId = "w", AdrNumber = 1,
        Human = new HumanOwnedContent { Title = "Use Cosmos DB", Decision = "We will use Cosmos DB." },
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        Provenance = Provenance.Human("alice", DateTimeOffset.UtcNow, "c1"), ContentHash = AdrService.HashHuman(new HumanOwnedContent { Title = "Use Cosmos DB", Decision = "We will use Cosmos DB." })
    };

    [Fact]
    public void System_provenance_cannot_apply_human_change()
    {
        var adr = NewAdr();
        var system = Provenance.System("analyzer", "1.0", DateTimeOffset.UtcNow, "c2");
        Assert.Throws<HumanOwnedFieldException>(() => adr.ApplyHumanChange(adr.Human with { Decision = "Changed by robot" }, system, AdrService.HashHuman));
        Assert.Equal("We will use Cosmos DB.", adr.Human.Decision);
    }

    [Fact]
    public void Observed_update_cannot_touch_human_content_by_construction()
    {
        var adr = NewAdr();
        var before = adr.Human; var hash = adr.ContentHash;
        adr.ApplyObservedUpdate(adr.Observed with { DriftStatus = "Drifted" }, Provenance.System("drift", "1.0", DateTimeOffset.UtcNow, "c3"));
        Assert.Same(before, adr.Human);
        Assert.Equal(hash, adr.ContentHash);
        Assert.Equal(1, adr.ObservedRevision);
        Assert.Equal(1, adr.HumanRevision);
    }

    [Fact]
    public async Task Technology_refresh_and_drift_never_change_decision_text()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("No Newtonsoft", "We must not use Newtonsoft.Json anywhere. All serialization uses System.Text.Json.");
        await host.AcceptAsync(adrId);
        await host.UploadAsync("src/Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>", adrId, "application/xml");
        var before = await host.CallAsync("adr_get", new { adrId });
        var hashBefore = before.GetProperty("data").GetProperty("adr").GetProperty("contentHash").GetString();

        var refresh = await host.CallAsync("technology_refresh", new { adrId });
        Assert.True(refresh.GetProperty("ok").GetBoolean(), refresh.ToString());
        var drift = await host.CallAsync("adr_detect_drift", new { adrId });
        Assert.True(drift.GetProperty("ok").GetBoolean(), drift.ToString());
        Assert.Equal("Drifted", drift.GetProperty("data").GetProperty("overallStatus").GetString());

        var after = await host.CallAsync("adr_get", new { adrId });
        var a = after.GetProperty("data").GetProperty("adr");
        Assert.Equal(hashBefore, a.GetProperty("contentHash").GetString());
        Assert.Equal("We must not use Newtonsoft.Json anywhere. All serialization uses System.Text.Json.", a.GetProperty("decision").GetString());
        Assert.Equal("Drifted", a.GetProperty("summary").GetProperty("driftStatus").GetString());
        Assert.True(a.GetProperty("summary").GetProperty("observedRevision").GetInt32() >= 2);
    }

    [Fact]
    public void Proposal_for_system_managed_field_is_rejected()
    {
        Assert.Throws<ValidationException>(() => HumanFieldPatch.ForField("detectedTechnologies", "[]"));
        Assert.Throws<ValidationException>(() => HumanFieldPatch.ForField("driftStatus", "Compliant"));
        Assert.True(HumanOwnedFields.IsHumanOwned("decision"));
        Assert.False(HumanOwnedFields.IsHumanOwned("driftStatus"));
    }
}
