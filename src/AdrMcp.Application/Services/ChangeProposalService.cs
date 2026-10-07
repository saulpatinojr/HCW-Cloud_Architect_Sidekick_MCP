using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Proposals;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed record ProposedFieldChange(string Field, string ProposedValue, string Reason, IReadOnlyList<EvidenceItem>? Evidence);

/// <summary>Propose → review → approve → apply workflow for human-owned fields.</summary>
public sealed class ChangeProposalService
{
    private readonly IProposalRepository _proposals;
    private readonly AdrService _adrs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly AdrOptions _options;

    public ChangeProposalService(IProposalRepository proposals, AdrService adrs, IAuthorizationService authz, IClock clock, IOptions<AdrOptions> options)
    {
        _proposals = proposals; _adrs = adrs; _authz = authz; _clock = clock; _options = options.Value;
    }

    public async Task<ChangeProposal> ProposeAsync(TenantContext ctx, string workspaceId, string adrId, IReadOnlyList<ProposedFieldChange> changes, string proposerType, Confidence? confidence, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrPropose);
        if (changes.Count == 0) throw new ValidationException("At least one field change is required.");
        if (changes.Count > 15) throw new ValidationException("At most 15 field changes per proposal.");
        var adr = await _adrs.GetAsync(ctx, workspaceId, adrId, ct);
        if (adr.Status == AdrStatus.Archived) throw new InvalidTransitionException("Archived", "propose");

        var fieldChanges = new List<FieldChange>();
        foreach (var c in changes)
        {
            if (!HumanOwnedFields.IsHumanOwned(c.Field))
                throw new ValidationException($"'{c.Field}' is not a human-owned field. System-managed fields are refreshed automatically and cannot be proposed.");
            _ = HumanFieldPatch.ForField(c.Field, c.ProposedValue); // validates parse-ability now, not at approval time
            if (string.IsNullOrWhiteSpace(c.Reason)) throw new ValidationException($"A reason is required for field '{c.Field}'.");
            var current = HumanFieldPatch.CurrentValueOf(adr.Human, c.Field);
            fieldChanges.Add(new FieldChange(c.Field.ToLowerInvariant(), current is null ? null : Hashing.Sha256Hex(current), c.ProposedValue, c.Reason, c.Evidence ?? Array.Empty<EvidenceItem>()));
        }

        var now = _clock.UtcNow;
        var proposal = new ChangeProposal
        {
            Id = Ids.New("prop"),
            TenantId = ctx.TenantId,
            WorkspaceId = workspaceId,
            AdrId = adrId,
            BaseHumanRevision = adr.HumanRevision,
            Changes = fieldChanges,
            ProposedBy = ctx.PrincipalId,
            ProposerType = proposerType,
            ProposedAt = now,
            ExpiresAt = now.AddDays(_options.ProposalExpiryDays),
            Confidence = confidence
        };
        return await _proposals.CreateAsync(proposal, ct);
    }

    public async Task<(ChangeProposal Proposal, Adr Adr)> ApproveAsync(TenantContext ctx, string workspaceId, string proposalId, string? note, string? expectedAdrEtag, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrApproveChange);
        var proposal = await LoadAsync(ctx, workspaceId, proposalId, ct);
        if (proposal.Status != ProposalStatus.Proposed) throw new DomainException(ErrorCodes.Conflict, $"Proposal is {proposal.Status} and cannot be approved.");
        if (proposal.ExpiresAt < _clock.UtcNow)
        {
            await _proposals.ReplaceAsync(proposal with { Status = ProposalStatus.Expired }, ct);
            throw new DomainException(ErrorCodes.Conflict, "Proposal has expired. Re-propose against the current ADR revision.");
        }
        if (proposal.ProposedBy == ctx.PrincipalId && proposal.ProposerType == Provenance.ActorTypeHuman && !ctx.Roles.Contains(Role.Admin))
            throw new ForbiddenException("Four-eyes rule: a proposer cannot approve their own proposal.");

        var adr = await _adrs.GetAsync(ctx, workspaceId, proposal.AdrId, ct);
        if (expectedAdrEtag is not null && adr.ETag is not null && adr.ETag != expectedAdrEtag)
            throw new ConcurrencyException("ADR etag mismatch; reload before approving.");

        // Stale-proposal protection: each targeted field must be unchanged since the proposal was made.
        foreach (var change in proposal.Changes)
        {
            var current = HumanFieldPatch.CurrentValueOf(adr.Human, change.Field);
            var currentHash = current is null ? null : Hashing.Sha256Hex(current);
            if (currentHash != change.CurrentValueHash)
                throw new DomainException(ErrorCodes.Conflict, $"Field '{change.Field}' changed since the proposal was created (ADR is at h{adr.HumanRevision}, proposal based on h{proposal.BaseHumanRevision}). Re-propose.");
        }

        if (proposal.Changes.Any(c => c.Field == HumanOwnedFields.Status)) _authz.Require(ctx, Permission.AdrTransitionStatus);
        if (proposal.Changes.Any(c => c.Field is HumanOwnedFields.Supersedes or "supersededby")) _authz.Require(ctx, Permission.AdrSupersede);

        var content = adr.Human;
        foreach (var change in proposal.Changes) content = HumanFieldPatch.ForField(change.Field, change.ProposedValue).ApplyTo(content);

        var now = _clock.UtcNow;
        var provenance = Provenance.Human(ctx.PrincipalId, now, ctx.CorrelationId, $"adr_approve_change:{proposal.Id}");
        var updated = await _adrs.ApplyHumanChangeAsync(adr, content, provenance, $"Approved proposal {proposal.Id}: {note}", ct);
        var applied = await _proposals.ReplaceAsync(proposal with { Status = ProposalStatus.Applied, DecidedBy = ctx.PrincipalId, DecidedAt = now, DecisionNote = note }, ct);
        return (applied, updated);
    }

    public async Task<ChangeProposal> RejectAsync(TenantContext ctx, string workspaceId, string proposalId, string? note, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrApproveChange);
        var proposal = await LoadAsync(ctx, workspaceId, proposalId, ct);
        if (proposal.Status != ProposalStatus.Proposed) throw new DomainException(ErrorCodes.Conflict, $"Proposal is {proposal.Status} and cannot be rejected.");
        return await _proposals.ReplaceAsync(proposal with { Status = ProposalStatus.Rejected, DecidedBy = ctx.PrincipalId, DecidedAt = _clock.UtcNow, DecisionNote = note }, ct);
    }

    public Task<IReadOnlyList<ChangeProposal>> ListAsync(TenantContext ctx, string workspaceId, string adrId, ProposalStatus? status, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrRead);
        return _proposals.ListForAdrAsync(ctx.TenantId, workspaceId, adrId, status, ct);
    }

    private async Task<ChangeProposal> LoadAsync(TenantContext ctx, string workspaceId, string proposalId, CancellationToken ct)
        => await _proposals.GetAsync(ctx.TenantId, workspaceId, proposalId, ct) ?? throw new NotFoundException(ErrorCodes.NotFound, $"Proposal '{proposalId}' not found.");
}
