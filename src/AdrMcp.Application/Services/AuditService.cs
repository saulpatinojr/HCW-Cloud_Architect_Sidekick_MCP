using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Audit;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Services;

public sealed class AuditService
{
    private readonly IAuditRepository _repo;
    private readonly IClock _clock;
    private readonly AdrOptions _adrOptions;
    private readonly AuditOptions _auditOptions;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAuditRepository repo, IClock clock, IOptions<AdrOptions> adrOptions, IOptions<AuditOptions> auditOptions, ILogger<AuditService> logger)
    {
        _repo = repo; _clock = clock; _adrOptions = adrOptions.Value; _auditOptions = auditOptions.Value; _logger = logger;
    }

    public async Task RecordAsync(TenantContext ctx, string workspaceId, string toolName, string outcome, string? errorCode, string? targetType, string? targetId, string? summary, long durationMs, CancellationToken ct)
    {
        var evt = new AuditEvent
        {
            Id = Ids.New("aud"),
            TenantId = ctx.TenantId,
            WorkspaceId = workspaceId,
            Timestamp = _clock.UtcNow,
            CorrelationId = ctx.CorrelationId,
            ToolName = toolName,
            PrincipalHash = Hashing.PrincipalHash(ctx.PrincipalId, _adrOptions.PrincipalHashSalt),
            Outcome = outcome,
            ErrorCode = errorCode,
            TargetType = targetType,
            TargetId = targetId,
            Summary = SensitiveDataRedactor.Redact(summary),
            DurationMs = durationMs,
            Ttl = (int)TimeSpan.FromDays(_auditOptions.RetentionDays).TotalSeconds
        };
        try { await _repo.AppendAsync(evt, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Audit append failed for {Tool} {CorrelationId}", toolName, ctx.CorrelationId); }
    }
}
