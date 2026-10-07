using System.Text.Json;
using AdrMcp.Domain.Errors;
using AdrMcp.UnitTests.Support;

namespace AdrMcp.UnitTests.Application;

public class ProposalWorkflowTests
{
    [Fact]
    public async Task Propose_approve_apply_creates_new_human_revision_with_provenance()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("Queue choice", "Use Service Bus.");
        var prop = await host.CallAsync("adr_propose_change", new { adrId, changes = new[] { new { field = "decision", proposedValue = "Use Azure Service Bus Premium.", reason = "Evidence shows premium features in use." } }, proposerType = "agent", confidence = 0.9 });
        Assert.True(prop.GetProperty("ok").GetBoolean(), prop.ToString());
        var proposalId = prop.GetProperty("data").GetProperty("proposal").GetProperty("id").GetString();
        // Agent confidence is capped at the inferred ceiling.
        Assert.True(prop.GetProperty("data").GetProperty("proposal").GetProperty("confidence").GetProperty("score").GetDouble() <= 0.6);

        var before = await host.CallAsync("adr_get", new { adrId });
        Assert.Equal("Use Service Bus.", before.GetProperty("data").GetProperty("adr").GetProperty("decision").GetString());

        // Approver is a different principal than the agent proposer: use a header-less call (static principal) — proposer type agent bypasses four-eyes by design.
        var approve = await host.CallAsync("adr_approve_change", new { proposalId, note = "Looks right" });
        Assert.True(approve.GetProperty("ok").GetBoolean(), approve.ToString());
        Assert.Equal("Applied", approve.GetProperty("data").GetProperty("proposal").GetProperty("status").GetString());

        var after = await host.CallAsync("adr_get", new { adrId });
        var adr = after.GetProperty("data").GetProperty("adr");
        Assert.Equal("Use Azure Service Bus Premium.", adr.GetProperty("decision").GetString());
        Assert.Equal(2, adr.GetProperty("summary").GetProperty("humanRevision").GetInt32());
        Assert.Equal("human", adr.GetProperty("provenance").GetProperty("actorType").GetString());

        var history = await host.CallAsync("adr_history", new { adrId });
        Assert.True(history.GetProperty("data").GetProperty("versions").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Human_proposer_cannot_approve_own_proposal()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("X", "Y");
        var prop = await host.CallAsync("adr_propose_change", new { adrId, changes = new[] { new { field = "title", proposedValue = "Z", reason = "r" } }, proposerType = "human" });
        var proposalId = prop.GetProperty("data").GetProperty("proposal").GetProperty("id").GetString();
        var approve = await host.CallAsync("adr_approve_change", new { proposalId });
        Assert.False(approve.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.Forbidden, approve.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_proposal_is_rejected_when_field_changed_in_between()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("X", "Original");
        var prop = await host.CallAsync("adr_propose_change", new { adrId, changes = new[] { new { field = "decision", proposedValue = "Proposed", reason = "r" } }, proposerType = "agent" });
        var proposalId = prop.GetProperty("data").GetProperty("proposal").GetProperty("id").GetString();
        var upd = await host.CallAsync("adr_update", new { adrId, fields = new { decision = "Changed by human meanwhile" } });
        Assert.True(upd.GetProperty("ok").GetBoolean(), upd.ToString());
        var approve = await host.CallAsync("adr_approve_change", new { proposalId });
        Assert.False(approve.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.Conflict, approve.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Re-propose", approve.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Reject_marks_proposal_rejected_and_leaves_adr_untouched()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("X", "Original");
        var prop = await host.CallAsync("adr_propose_change", new { adrId, changes = new[] { new { field = "decision", proposedValue = "Nope", reason = "r" } }, proposerType = "agent" });
        var proposalId = prop.GetProperty("data").GetProperty("proposal").GetProperty("id").GetString();
        var rej = await host.CallAsync("adr_reject_change", new { proposalId, note = "no" });
        Assert.Equal("Rejected", rej.GetProperty("data").GetProperty("proposal").GetProperty("status").GetString());
        var adr = await host.CallAsync("adr_get", new { adrId });
        Assert.Equal("Original", adr.GetProperty("data").GetProperty("adr").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Proposal_targeting_system_managed_field_fails_validation()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("X", "Y");
        var prop = await host.CallAsync("adr_propose_change", new { adrId, changes = new[] { new { field = "detectedTechnologies", proposedValue = "[]", reason = "r" } } });
        Assert.False(prop.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.ValidationFailed, prop.GetProperty("error").GetProperty("code").GetString());
    }
}

public class EtagConcurrencyTests
{
    [Fact]
    public async Task Update_with_stale_etag_returns_ETAG_MISMATCH()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("X", "Y");
        var first = await host.CallAsync("adr_get", new { adrId });
        var staleEtag = first.GetProperty("data").GetProperty("adr").GetProperty("summary").GetProperty("etag").GetString();
        var ok = await host.CallAsync("adr_update", new { adrId, fields = new { humanNotes = "first" }, expectedEtag = staleEtag });
        Assert.True(ok.GetProperty("ok").GetBoolean(), ok.ToString());
        var conflict = await host.CallAsync("adr_update", new { adrId, fields = new { humanNotes = "second" }, expectedEtag = staleEtag });
        Assert.False(conflict.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.EtagMismatch, conflict.GetProperty("error").GetProperty("code").GetString());
        Assert.True(conflict.GetProperty("error").GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Accepted_decision_content_is_immutable()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("X", "Y");
        await host.AcceptAsync(adrId);
        var r = await host.CallAsync("adr_update", new { adrId, fields = new { decision = "changed" } });
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.Conflict, r.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_status_transition_is_rejected()
    {
        var host = new TestHost();
        var adrId = await host.CreateAdrAsync("X", "Y");
        var r = await host.CallAsync("adr_update", new { adrId, fields = new { status = "Accepted" } });
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.InvalidTransition, r.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Idempotency_key_replays_the_original_result_and_rejects_different_payload()
    {
        var host = new TestHost();
        var a = await host.CallAsync("adr_create", new { fields = new { title = "Idem", decision = "D" }, idempotencyKey = "k1" });
        var b = await host.CallAsync("adr_create", new { fields = new { title = "Idem", decision = "D" }, idempotencyKey = "k1" });
        Assert.Equal(a.GetProperty("data").GetProperty("adr").GetProperty("summary").GetProperty("id").GetString(), b.GetProperty("data").GetProperty("adr").GetProperty("summary").GetProperty("id").GetString());
        Assert.True(b.GetProperty("meta").GetProperty("idempotentReplay").GetBoolean());
        var list = await host.CallAsync("adr_list", new { });
        Assert.Equal(1, list.GetProperty("data").GetProperty("items").GetArrayLength());
        var c = await host.CallAsync("adr_create", new { fields = new { title = "Different", decision = "D" }, idempotencyKey = "k1" });
        Assert.Equal(ErrorCodes.IdempotencyConflict, c.GetProperty("error").GetProperty("code").GetString());
    }
}
