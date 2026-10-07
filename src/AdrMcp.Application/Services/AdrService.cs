using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.History;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed record CreateAdrCommand(
    string? Markdown,
    HumanFieldPatch? Fields,
    string? RepositoryId,
    string? RequestedId);

public sealed record AdrSearchHit(Adr Adr, double Score, IReadOnlyList<string> MatchedTerms);

/// <summary>
/// Core ADR use-cases. Human-owned content is only ever written through <see cref="ApplyHumanChangeAsync"/>,
/// which requires a human provenance and a permission check. Analyzers use <see cref="ApplyObservedUpdateAsync"/>.
/// </summary>
public sealed class AdrService
{
    private readonly IAdrRepository _adrs;
    private readonly IAdrHistoryRepository _history;
    private readonly IEvidenceBlobStore _blobs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly AdrOptions _options;
    private readonly ILogger<AdrService> _logger;

    public AdrService(IAdrRepository adrs, IAdrHistoryRepository history, IEvidenceBlobStore blobs, IAuthorizationService authz, IClock clock, IOptions<AdrOptions> options, ILogger<AdrService> logger)
    {
        _adrs = adrs; _history = history; _blobs = blobs; _authz = authz; _clock = clock; _options = options.Value; _logger = logger;
    }

    public static string HashHuman(HumanOwnedContent c) => Hashing.CanonicalHash(c);

    public async Task<Adr> CreateAsync(TenantContext ctx, string workspaceId, CreateAdrCommand cmd, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrCreate);
        HumanOwnedContent content;
        if (!string.IsNullOrWhiteSpace(cmd.Markdown))
        {
            if (cmd.Markdown.Length > _options.MaxMarkdownLength) throw new ValidationException($"markdown exceeds {_options.MaxMarkdownLength} characters.");
            content = AdrMarkdown.Parse(cmd.Markdown);
            if (cmd.Fields is not null) content = cmd.Fields.ApplyTo(content);
        }
        else if (cmd.Fields is not null)
        {
            if (string.IsNullOrWhiteSpace(cmd.Fields.Title)) throw new ValidationException("title is required when markdown is not supplied.");
            content = cmd.Fields.ApplyTo(new HumanOwnedContent { Title = cmd.Fields.Title });
        }
        else throw new ValidationException("Either markdown or fields must be supplied.");

        ValidateContent(content);
        if (content.Status is not (AdrStatus.Draft or AdrStatus.Proposed))
            throw new ValidationException("A new ADR may only be created in Draft or Proposed status. Use adr_update to transition.");

        var now = _clock.UtcNow;
        var number = await _adrs.NextAdrNumberAsync(ctx.TenantId, workspaceId, ct);
        var id = cmd.RequestedId is not null && Ids.IsSafeId(cmd.RequestedId) ? cmd.RequestedId : Ids.Adr(number);
        var provenance = Provenance.Human(ctx.PrincipalId, now, ctx.CorrelationId, "adr_create");
        var adr = new Adr
        {
            Id = id,
            TenantId = ctx.TenantId,
            WorkspaceId = workspaceId,
            RepositoryId = cmd.RepositoryId,
            AdrNumber = number,
            Human = content,
            CreatedAt = now,
            UpdatedAt = now,
            Provenance = provenance,
            ContentHash = HashHuman(content)
        };

        // Persist the original markdown (or a rendered equivalent) as the immutable source of record.
        var sourcePath = $"{ctx.TenantId}/{workspaceId}/{id}/h1.md";
        await _blobs.WriteTextAsync(EvidenceContainer.AdrSource, sourcePath, cmd.Markdown ?? AdrMarkdown.Render(adr), "text/markdown",
            new Dictionary<string, string> { ["adrId"] = id, ["humanRevision"] = "1", ["contentHash"] = adr.ContentHash }, ct);
        adr.SourceMarkdownBlobPath = sourcePath;

        var created = await _adrs.CreateAsync(adr, ct);
        await _history.AppendAsync(Snapshot(created, "created", provenance, "ADR created"), ct);
        _logger.LogInformation("ADR {AdrId} created in {Workspace} (rev h{Rev})", created.Id, workspaceId, created.HumanRevision);
        return created;
    }

    public async Task<Adr> GetAsync(TenantContext ctx, string workspaceId, string adrId, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrRead);
        var adr = await _adrs.GetAsync(ctx.TenantId, workspaceId, adrId, ct);
        if (adr is null || adr.IsDeleted) throw new NotFoundException(ErrorCodes.AdrNotFound, $"ADR '{adrId}' was not found in workspace '{workspaceId}'.");
        return adr;
    }

    public Task<Page<Adr>> ListAsync(TenantContext ctx, AdrQuery query, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrRead);
        if (query.TenantId != ctx.TenantId) throw new ForbiddenException("Tenant mismatch.", ErrorCodes.TenantMismatch);
        return _adrs.ListAsync(query with { Take = Math.Clamp(query.Take, 1, _options.MaxListPageSize) }, ct);
    }

    public async Task<IReadOnlyList<AdrSearchHit>> SearchAsync(TenantContext ctx, string workspaceId, string query, AdrStatus? status, int take, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrRead);
        var terms = KeywordExtractor.Extract(query, _options.MaxSearchTerms);
        if (terms.Count == 0) throw new ValidationException("query must contain at least one searchable term.");
        var candidates = await _adrs.SearchAsync(ctx.TenantId, workspaceId, terms, status, Math.Clamp(take, 1, _options.MaxListPageSize) * 4, ct);
        return candidates
            .Select(a => Score(a, terms))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Adr.UpdatedAt)
            .Take(Math.Clamp(take, 1, _options.MaxListPageSize))
            .ToList();
    }

    public async Task<Adr> UpdateAsync(TenantContext ctx, string workspaceId, string adrId, HumanFieldPatch patch, string? expectedEtag, string? note, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrEdit);
        if (patch.IsEmpty) throw new ValidationException("No fields to update.");
        if (patch.ChangesStatus) _authz.Require(ctx, Permission.AdrTransitionStatus);
        if (patch.ChangesSupersession) _authz.Require(ctx, Permission.AdrSupersede);

        var adr = await GetAsync(ctx, workspaceId, adrId, ct);
        if (adr.Status is AdrStatus.Archived) throw new InvalidTransitionException(adr.Status.ToString(), "edit");
        if (expectedEtag is not null && adr.ETag is not null && expectedEtag != adr.ETag)
            throw new ConcurrencyException($"ADR '{adrId}' was modified by someone else (etag mismatch). Reload and retry.");

        // Accepted decisions are immutable except for status/supersession/approvers/notes.
        if (adr.Status == AdrStatus.Accepted && (patch.Decision is not null || patch.Context is not null || patch.Rationale is not null || patch.Alternatives is not null || patch.Consequences is not null || patch.Title is not null || patch.Claims is not null))
            throw new DomainException(ErrorCodes.Conflict, "An Accepted ADR's decision content is immutable. Create a new ADR and supersede this one.");

        var updated = patch.ApplyTo(adr.Human);
        ValidateContent(updated);
        var provenance = Provenance.Human(ctx.PrincipalId, _clock.UtcNow, ctx.CorrelationId, "adr_update");
        return await ApplyHumanChangeAsync(adr, updated, provenance, note ?? "adr_update", ct);
    }

    public async Task<(Adr Old, Adr New)> SupersedeAsync(TenantContext ctx, string workspaceId, string oldAdrId, string newAdrId, string? note, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrSupersede);
        if (oldAdrId == newAdrId) throw new ValidationException("An ADR cannot supersede itself.");
        var oldAdr = await GetAsync(ctx, workspaceId, oldAdrId, ct);
        var newAdr = await GetAsync(ctx, workspaceId, newAdrId, ct);
        if (newAdr.Status != AdrStatus.Accepted) throw new ValidationException("The superseding ADR must be Accepted.");
        AdrLifecycle.EnsureTransition(oldAdr.Status, AdrStatus.Superseded);
        var now = _clock.UtcNow;
        var prov = Provenance.Human(ctx.PrincipalId, now, ctx.CorrelationId, "adr_supersede");
        var updatedOld = await ApplyHumanChangeAsync(oldAdr, oldAdr.Human with { Status = AdrStatus.Superseded, SupersededBy = newAdrId }, prov, note ?? $"Superseded by {newAdrId}", ct);
        var updatedNew = newAdr.Human.Supersedes == oldAdrId ? newAdr : await ApplyHumanChangeAsync(newAdr, newAdr.Human with { Supersedes = oldAdrId }, prov, $"Supersedes {oldAdrId}", ct);
        return (updatedOld, updatedNew);
    }

    public async Task<Adr> ArchiveAsync(TenantContext ctx, string workspaceId, string adrId, string reason, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrArchive);
        var adr = await GetAsync(ctx, workspaceId, adrId, ct);
        AdrLifecycle.EnsureTransition(adr.Status, AdrStatus.Archived);
        var prov = Provenance.Human(ctx.PrincipalId, _clock.UtcNow, ctx.CorrelationId, "adr_archive");
        var notes = string.IsNullOrWhiteSpace(adr.Human.HumanNotes) ? $"Archived: {reason}" : $"{adr.Human.HumanNotes}\n\nArchived: {reason}";
        return await ApplyHumanChangeAsync(adr, adr.Human with { Status = AdrStatus.Archived, HumanNotes = notes }, prov, reason, ct);
    }

    public Task<IReadOnlyList<AdrVersionSnapshot>> HistoryAsync(TenantContext ctx, string workspaceId, string adrId, int take, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AdrRead);
        return _history.ListAsync(ctx.TenantId, workspaceId, adrId, Math.Clamp(take, 1, _options.HistoryPageSize * 5), ct);
    }

    public string ExportMarkdown(Adr adr) => AdrMarkdown.Render(adr);

    /// <summary>The only write path for human-owned content. Snapshots first, then replaces with If-Match.</summary>
    public async Task<Adr> ApplyHumanChangeAsync(Adr adr, HumanOwnedContent updated, Provenance provenance, string summary, CancellationToken ct)
    {
        if (provenance.ActorType != Provenance.ActorTypeHuman) throw new HumanOwnedFieldException("*");
        await _history.AppendAsync(Snapshot(adr, "before-human-change", adr.Provenance, summary), ct);
        adr.ApplyHumanChange(updated, provenance, HashHuman);
        var sourcePath = $"{adr.TenantId}/{adr.WorkspaceId}/{adr.Id}/h{adr.HumanRevision}.md";
        await _blobs.WriteTextAsync(EvidenceContainer.AdrSource, sourcePath, AdrMarkdown.Render(adr), "text/markdown",
            new Dictionary<string, string> { ["adrId"] = adr.Id, ["humanRevision"] = adr.HumanRevision.ToString(), ["contentHash"] = adr.ContentHash }, ct);
        adr.SourceMarkdownBlobPath = sourcePath;
        var saved = await _adrs.ReplaceAsync(adr, ct);
        await _history.AppendAsync(Snapshot(saved, "human-change", provenance, summary), ct);
        return saved;
    }

    /// <summary>
    /// Write path for analyzers. Retries on ETag conflict by re-reading and re-applying the mutation, because
    /// observed updates are merges that do not depend on a specific prior state. Human content is never touched.
    /// </summary>
    public async Task<Adr> ApplyObservedUpdateAsync(string tenantId, string workspaceId, string adrId, Func<ObservedState, ObservedState> mutate, Provenance provenance, string summary, CancellationToken ct, int maxAttempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            var adr = await _adrs.GetAsync(tenantId, workspaceId, adrId, ct) ?? throw new NotFoundException(ErrorCodes.AdrNotFound, $"ADR '{adrId}' not found.");
            var humanBefore = adr.ContentHash;
            await _history.AppendAsync(Snapshot(adr, "before-observed-change", adr.Observed.LastProvenance ?? adr.Provenance, summary), ct);
            adr.ApplyObservedUpdate(mutate(adr.Observed), provenance);
            if (adr.ContentHash != humanBefore || HashHuman(adr.Human) != humanBefore)
                throw new HumanOwnedFieldException("*"); // defence in depth: an observed mutation must not alter human content
            try
            {
                var saved = await _adrs.ReplaceAsync(adr, ct);
                await _history.AppendAsync(Snapshot(saved, "observed-change", provenance, summary), ct);
                return saved;
            }
            catch (ConcurrencyException) when (attempt < maxAttempts)
            {
                _logger.LogWarning("ETag conflict applying observed update to {AdrId}, retrying ({Attempt}/{Max})", adrId, attempt, maxAttempts);
            }
        }
    }

    private AdrVersionSnapshot Snapshot(Adr adr, string changeKind, Provenance provenance, string? summary) => new()
    {
        Id = $"{AdrVersionSnapshot.BuildId(adr.Id, adr.HumanRevision, adr.ObservedRevision)}:{changeKind}:{_clock.UtcNow.ToUnixTimeMilliseconds()}",
        TenantId = adr.TenantId,
        WorkspaceId = adr.WorkspaceId,
        AdrId = adr.Id,
        HumanRevision = adr.HumanRevision,
        ObservedRevision = adr.ObservedRevision,
        ChangeKind = changeKind,
        Human = adr.Human,
        Observed = adr.Observed,
        ContentHash = adr.ContentHash,
        Provenance = provenance,
        CapturedAt = _clock.UtcNow,
        Summary = summary
    };

    private void ValidateContent(HumanOwnedContent c)
    {
        new Guard()
            .Required(c.Title, "title").MaxLength(c.Title, 300, "title")
            .MaxLength(c.Context, _options.MaxAdrFieldLength, "context")
            .MaxLength(c.Decision, _options.MaxAdrFieldLength, "decision")
            .MaxLength(c.Rationale, _options.MaxAdrFieldLength, "rationale")
            .MaxLength(c.Consequences, _options.MaxAdrFieldLength, "consequences")
            .MaxItems(c.Alternatives, 50, "alternatives").MaxItems(c.Tags, 50, "tags").MaxItems(c.Owners, 50, "owners").MaxItems(c.Approvers, 50, "approvers").MaxItems(c.Claims, 200, "claims")
            .Throw();
    }

    private static AdrSearchHit Score(Adr adr, IReadOnlyList<string> terms)
    {
        var matched = new List<string>();
        double score = 0;
        var title = adr.Human.Title.ToLowerInvariant();
        var body = (adr.Human.Context + " " + adr.Human.Decision + " " + adr.Human.Rationale + " " + adr.Human.Consequences).ToLowerInvariant();
        var tags = adr.Human.Tags.Select(t => t.ToLowerInvariant()).ToHashSet();
        foreach (var t in terms)
        {
            double s = 0;
            if (title.Contains(t)) s += 3;
            if (tags.Contains(t)) s += 2.5;
            if (body.Contains(t)) s += 1;
            if (s > 0) { matched.Add(t); score += s; }
        }
        if (adr.Status == AdrStatus.Accepted) score *= 1.2;
        return new AdrSearchHit(adr, Math.Round(score, 3), matched);
    }
}
