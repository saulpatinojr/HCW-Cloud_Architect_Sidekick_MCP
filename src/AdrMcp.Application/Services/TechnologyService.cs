using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed record InlineFile(string Path, string Content);

public sealed record TechnologyRefreshResult(Adr Adr, int Added, int Updated, int Removed, IReadOnlyList<DetectedTechnology> Technologies);

/// <summary>Deterministic technology detection and system-managed refresh. Never writes human-owned fields.</summary>
public sealed class TechnologyService
{
    private readonly IEnumerable<ITechnologyDetector> _detectors;
    private readonly IEvidenceSetProvider _evidence;
    private readonly AdrService _adrs;
    private readonly IAuthorizationService _authz;
    private readonly IClock _clock;
    private readonly AdrOptions _options;
    private readonly JobOptions _jobOptions;
    private readonly ILogger<TechnologyService> _logger;

    public TechnologyService(IEnumerable<ITechnologyDetector> detectors, IEvidenceSetProvider evidence, AdrService adrs, IAuthorizationService authz, IClock clock, IOptions<AdrOptions> options, IOptions<JobOptions> jobOptions, ILogger<TechnologyService> logger)
    {
        _detectors = detectors; _evidence = evidence; _adrs = adrs; _authz = authz; _clock = clock; _options = options.Value; _jobOptions = jobOptions.Value; _logger = logger;
    }

    public IReadOnlyList<string> DetectorNames => _detectors.Select(d => $"{d.Name}@{d.Version}").ToList();

    /// <summary>Detects technologies from inline content (no persistence) or from the workspace evidence set.</summary>
    public async Task<IReadOnlyList<DetectedTechnology>> DetectAsync(TenantContext ctx, string workspaceId, string? adrId, IReadOnlyList<InlineFile>? inline, CancellationToken ct)
    {
        _authz.Require(ctx, Permission.AnalysisRun);
        var now = _clock.UtcNow;
        EvidenceSet set;
        if (inline is { Count: > 0 })
        {
            if (inline.Count > 50) throw new Domain.Errors.ValidationException("At most 50 inline files per call.");
            if (inline.Any(f => f.Content.Length > 512 * 1024)) throw new Domain.Errors.ValidationException("Inline file content must be <= 512 KB; upload larger files.");
            set = new EvidenceSet(inline.Select(f => new EvidenceFile("inline", SafeRelativePath(f.Path), "text/plain", f.Content, Common.Hashing.Sha256Hex(f.Content), adrId)).ToList());
        }
        else
        {
            set = await _evidence.LoadForWorkspaceAsync(ctx.TenantId, workspaceId, adrId, _jobOptions.MaxEvidenceFilesPerScan, _jobOptions.MaxBytesPerEvidenceFile, ct);
        }
        return Detect(set, now);
    }

    /// <summary>Normalizes a client-supplied relative path: forward slashes, no traversal segments, bounded length.</summary>
    public static string SafeRelativePath(string path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p is not ("." or "..")).Select(FileValidation.SanitizeFileName);
        var joined = string.Join('/', parts);
        if (joined.Length == 0) throw new Domain.Errors.ValidationException("path is required.");
        return joined.Length > 400 ? joined[^400..] : joined;
    }

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceSet set, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology>();
        foreach (var file in set.Files)
        {
            foreach (var detector in _detectors.Where(d => d.CanHandle(file.Path)))
            {
                try { results.AddRange(detector.Detect(file, now)); }
                catch (Exception ex) { _logger.LogWarning(ex, "Detector {Detector} failed on {Path}", detector.Name, file.Path); }
            }
        }
        return Merge(Array.Empty<DetectedTechnology>(), results, now).Technologies;
    }

    /// <summary>Refreshes observed technologies for an ADR from the workspace evidence set. Preserves first-seen timestamps and marks disappeared items Removed.</summary>
    public async Task<TechnologyRefreshResult> RefreshAsync(TenantContext? ctx, string tenantId, string workspaceId, string adrId, string correlationId, CancellationToken ct)
    {
        if (ctx is not null) _authz.Require(ctx, Permission.AnalysisRun);
        var now = _clock.UtcNow;
        var set = await _evidence.LoadForWorkspaceAsync(tenantId, workspaceId, adrId, _jobOptions.MaxEvidenceFilesPerScan, _jobOptions.MaxBytesPerEvidenceFile, ct);
        var detected = Detect(set, now);
        MergeResult merged = default;
        var provenance = Provenance.System("technology_refresh", _options.AnalyzerVersion, now, correlationId, $"{set.Files.Count} evidence files");
        var adr = await _adrs.ApplyObservedUpdateAsync(tenantId, workspaceId, adrId, o =>
        {
            merged = Merge(o.DetectedTechnologies, detected, now);
            return o with { DetectedTechnologies = merged.Technologies };
        }, provenance, $"Technology refresh: +{merged.Added} ~{merged.Updated} -{merged.Removed}", ct);
        return new TechnologyRefreshResult(adr, merged.Added, merged.Updated, merged.Removed, adr.Observed.DetectedTechnologies);
    }

    private record struct MergeResult(IReadOnlyList<DetectedTechnology> Technologies, int Added, int Updated, int Removed);

    private static MergeResult Merge(IReadOnlyList<DetectedTechnology> existing, IReadOnlyList<DetectedTechnology> detected, DateTimeOffset now)
    {
        var byKey = existing.ToDictionary(t => t.Key, t => t);
        var seen = new HashSet<string>();
        int added = 0, updated = 0, removed = 0;
        var output = new List<DetectedTechnology>();
        foreach (var group in detected.GroupBy(t => t.Key))
        {
            // Prefer the highest-confidence observation per (name, file).
            var best = group.OrderByDescending(t => t.Confidence.Score).First();
            seen.Add(best.Key);
            if (byKey.TryGetValue(best.Key, out var prior))
            {
                var changed = prior.DetectedVersion != best.DetectedVersion || prior.Status == TechnologyStatus.Removed;
                if (changed) updated++;
                output.Add(best with { FirstObservedAt = prior.FirstObservedAt, LastObservedAt = now });
            }
            else { added++; output.Add(best with { FirstObservedAt = now, LastObservedAt = now }); }
        }
        foreach (var prior in existing.Where(e => !seen.Contains(e.Key)))
        {
            if (prior.Status != TechnologyStatus.Removed) removed++;
            output.Add(prior with { Status = TechnologyStatus.Removed, Confidence = prior.Status == TechnologyStatus.Removed ? prior.Confidence : Confidence.Observed(prior.Confidence.Score, "No longer present in evidence") });
        }
        return new MergeResult(output.OrderBy(t => t.NormalizedName).ThenBy(t => t.SourceFile).ToList(), added, updated, removed);
    }
}
