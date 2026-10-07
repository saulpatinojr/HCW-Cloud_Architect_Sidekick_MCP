using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using AdrMcp.Application.Services;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Jobs;
using AdrMcp.Domain.Tenancy;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Functions.Timers;

/// <summary>
/// Scheduled maintenance: purge expired quarantine uploads and enqueue a technology/drift refresh for Accepted ADRs so observed
/// metadata stays current. Only system-managed fields are ever touched by the resulting jobs.
/// </summary>
public sealed class MaintenanceTimer
{
    private readonly FileUploadService _uploads;
    private readonly AnalysisJobService _jobs;
    private readonly IAdrRepository _adrs;
    private readonly AuthOptions _auth;
    private readonly ILogger<MaintenanceTimer> _logger;

    public MaintenanceTimer(FileUploadService uploads, AnalysisJobService jobs, IAdrRepository adrs, IOptions<AuthOptions> auth, ILogger<MaintenanceTimer> logger)
    {
        _uploads = uploads; _jobs = jobs; _adrs = adrs; _auth = auth.Value; _logger = logger;
    }

    // Every 6 hours. Known tenants/workspaces come from configuration (Auth:TenantWorkspaces + static binding).
    [Function(nameof(MaintenanceTimer))]
    public async Task Run([TimerTrigger("0 0 */6 * * *")] TimerInfo timer, CancellationToken ct)
    {
        foreach (var (tenantId, workspaceId) in KnownScopes())
        {
            try
            {
                var cleaned = await _uploads.CleanupExpiredAsync(tenantId, workspaceId, ct);
                var system = new TenantContext(tenantId, "system:maintenance", "Maintenance timer", new HashSet<Role> { Role.Admin }, new HashSet<string> { "*" }, workspaceId, "System", Guid.NewGuid().ToString("N"));
                var accepted = await _adrs.ListByStatusesAsync(tenantId, workspaceId, new[] { AdrStatus.Accepted }, ct);
                foreach (var adr in accepted)
                    await _jobs.EnqueueAsync(system, workspaceId, JobType.TechnologyRefresh, new Dictionary<string, string> { ["adrId"] = adr.Id }, $"scheduled-refresh:{adr.Id}", ct);
                _logger.LogInformation("Maintenance for {Tenant}/{Workspace}: {Cleaned} expired uploads removed, {Count} refresh jobs queued", tenantId, workspaceId, cleaned, accepted.Count);
            }
            catch (Exception ex) { _logger.LogError(ex, "Maintenance failed for {Tenant}/{Workspace}", tenantId, workspaceId); }
        }
    }

    private IEnumerable<(string TenantId, string WorkspaceId)> KnownScopes()
    {
        var seen = new HashSet<string>();
        if (string.Equals(_auth.Mode, "FunctionKey", StringComparison.OrdinalIgnoreCase) && seen.Add($"{_auth.StaticTenantId}|{_auth.StaticWorkspaceId}"))
            yield return (_auth.StaticTenantId, _auth.StaticWorkspaceId);
        foreach (var kv in _auth.TenantWorkspaces)
            foreach (var ws in kv.Value.Where(w => w != "*"))
                if (seen.Add($"{kv.Key}|{ws}")) yield return (kv.Key, ws);
    }
}
