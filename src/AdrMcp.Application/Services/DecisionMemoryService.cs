using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Memory;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

/// <summary>
/// Durable decision memory with explainable keyword scoring. The retrieval abstraction (IMemoryRepository + this scorer)
/// can be swapped for vector or hybrid retrieval without changing callers.
/// </summary>
public sealed class DecisionMemoryService
{
    private readonly IMemoryRepository _repo;
    private readonly IClock _clock;
    private readonly IAuthorizationService _authz;
    private readonly MemoryOptions _options;
    private readonly AdrOptions _adrOptions;
    private readonly ILogger<DecisionMemoryService> _logger;

    public DecisionMemoryService(IMemoryRepository repo, IClock clock, IAuthorizationService authz, IOptions<MemoryOptions> options, IOptions<AdrOptions> adrOptions, ILogger<DecisionMemoryService> logger)
    {
        _repo = repo; _clock = clock; _authz = authz; _options = options.Value; _adrOptions = adrOptions.Value; _logger = logger;
    }

    public async Task RecordAsync(TenantContext ctx, string workspaceId, string toolName, IEnumerable<string> adrIds, IEnumerable<string> keywordSources, IEnumerable<string>? evidenceIds, IEnumerable<string>? findingIds, string? summary, CancellationToken ct)
    {
        var keywords = KeywordExtractor.Extract(string.Join(' ', keywordSources), _options.MaxKeywordsPerRecord);
        var record = new MemoryRecord
        {
            Id = Ids.New("mem"),
            TenantId = ctx.TenantId,
            WorkspaceId = workspaceId,
            Timestamp = _clock.UtcNow,
            ToolName = toolName,
            PrincipalHash = Hashing.PrincipalHash(ctx.PrincipalId, _adrOptions.PrincipalHashSalt),
            SessionId = ctx.SessionId,
            AdrIds = adrIds.Distinct().Take(50).ToList(),
            Keywords = keywords,
            EvidenceIds = (evidenceIds ?? Array.Empty<string>()).Distinct().Take(50).ToList(),
            FindingIds = (findingIds ?? Array.Empty<string>()).Distinct().Take(50).ToList(),
            Summary = SensitiveDataRedactor.Redact(summary),
            Ttl = (int)TimeSpan.FromDays(_options.RetentionDays).TotalSeconds
        };
        try { await _repo.AppendAsync(record, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Memory append failed for {Tool}", toolName); }
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(TenantContext ctx, string workspaceId, string query, int take, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.MemoryRead);
        var terms = KeywordExtractor.Extract(query, 12);
        if (terms.Count == 0) return Array.Empty<MemoryHit>();
        var candidates = await _repo.FindCandidatesAsync(ctx.TenantId, workspaceId, terms, Math.Clamp(take, 1, 50) * 5, ct);
        var now = _clock.UtcNow;
        return candidates
            .Where(r => r.TenantId == ctx.TenantId && r.WorkspaceId == workspaceId) // belt and braces: never leak across partitions
            .Select(r => Score(r, terms, now))
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(Math.Clamp(take, 1, 50))
            .ToList();
    }

    public MemoryHit Score(MemoryRecord r, IReadOnlyList<string> terms, DateTimeOffset now)
    {
        var matched = terms.Where(t => r.Keywords.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0) return new MemoryHit(r, 0, matched, "no keyword overlap");
        var overlap = (double)matched.Count / terms.Count;
        var ageDays = Math.Max(0, (now - r.Timestamp).TotalDays);
        var recency = Math.Pow(0.5, ageDays / Math.Max(1, _options.RecencyHalfLifeDays));
        var feedbackBoost = string.IsNullOrEmpty(r.HumanFeedback) ? 1.0 : 1.15;
        var score = Math.Round((0.7 * overlap + 0.3 * recency) * feedbackBoost, 4);
        var explanation = $"matched {matched.Count}/{terms.Count} terms ({string.Join(", ", matched)}); recency factor {recency:0.00} (age {ageDays:0.#} days){(feedbackBoost > 1 ? "; human feedback boost" : "")}";
        return new MemoryHit(r, score, matched, explanation);
    }
}
