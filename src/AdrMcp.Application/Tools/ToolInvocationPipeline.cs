using System.Diagnostics;
using System.Text.Json;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Options;
using AdrMcp.Application.Services;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Application.Tools;

/// <summary>Per-call context handed to a tool handler.</summary>
public sealed class ToolCall<TReq>
{
    public required TenantContext Tenant { get; init; }
    public required string WorkspaceId { get; init; }
    public required TReq Request { get; init; }
    public required ToolDefinition Definition { get; init; }
    public string CorrelationId => Tenant.CorrelationId;
    public List<string> Warnings { get; } = new();
    public HashSet<string> TouchedAdrIds { get; } = new();
    public HashSet<string> TouchedFindingIds { get; } = new();
    public HashSet<string> TouchedEvidenceIds { get; } = new();
    public List<string> KeywordSources { get; } = new();
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? AuditSummary { get; set; }
    public void Touch(string adrId) => TouchedAdrIds.Add(adrId);
}

/// <summary>
/// Cross-cutting behaviour for every MCP tool: correlation id, tenant resolution from transport headers, authorization,
/// argument binding + validation, idempotency, structured errors, audit, decision memory, redacted logging.
/// </summary>
public sealed class ToolInvocationPipeline
{
    private readonly ITenantResolver _tenants;
    private readonly IAuthorizationService _authz;
    private readonly IIdempotencyStore _idempotency;
    private readonly AuditService _audit;
    private readonly DecisionMemoryService _memory;
    private readonly IdempotencyOptions _idemOptions;
    private readonly ILogger<ToolInvocationPipeline> _logger;

    public ToolInvocationPipeline(ITenantResolver tenants, IAuthorizationService authz, IIdempotencyStore idempotency, AuditService audit, DecisionMemoryService memory, IOptions<IdempotencyOptions> idemOptions, ILogger<ToolInvocationPipeline> logger)
    {
        _tenants = tenants; _authz = authz; _idempotency = idempotency; _audit = audit; _memory = memory; _idemOptions = idemOptions.Value; _logger = logger;
    }

    public async Task<string> ExecuteAsync<TReq>(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        IReadOnlyDictionary<string, string> headers,
        string? sessionId,
        Func<ToolCall<TReq>, CancellationToken, Task<object>> handler,
        CancellationToken ct) where TReq : class, IToolRequest
    {
        var sw = Stopwatch.StartNew();
        var definition = ToolCatalog.Get(toolName);
        var correlationId = ResolveCorrelationId(headers);
        TenantContext? tenant = null;
        string workspaceId = "-";
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId, ["Tool"] = toolName });
        try
        {
            tenant = await _tenants.ResolveAsync(headers, sessionId, correlationId, ct);
            _authz.Require(tenant, definition.Permission);

            var request = Bind<TReq>(arguments);
            workspaceId = _authz.ResolveWorkspace(tenant, request.WorkspaceId);
            var call = new ToolCall<TReq> { Tenant = tenant, WorkspaceId = workspaceId, Request = request, Definition = definition };
            var partitionKey = $"{tenant.TenantId}|{workspaceId}";

            // Idempotency (key-based) for mutating tools.
            string? idemKey = (request as IMutatingRequest)?.IdempotencyKey;
            string? requestHash = null;
            if (idemKey is not null && definition.Idempotency == IdempotencyMode.Key)
            {
                requestHash = Hashing.Sha256Hex(JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), AppJson.Canonical));
                var existing = await _idempotency.GetAsync(partitionKey, toolName, idemKey, ct);
                if (existing is not null)
                {
                    if (existing.RequestHash != requestHash)
                        throw new DomainException(ErrorCodes.IdempotencyConflict, "idempotencyKey was already used with a different payload.");
                    _logger.LogInformation("Idempotent replay for {Tool}", toolName);
                    await _audit.RecordAsync(tenant, workspaceId, toolName, "replayed", null, null, null, "idempotent replay", sw.ElapsedMilliseconds, ct);
                    return ReplayEnvelope(existing.ResponseJson, correlationId, toolName, sw.ElapsedMilliseconds);
                }
            }

            var data = await handler(call, ct);
            var meta = new ToolMeta(correlationId, toolName, sw.ElapsedMilliseconds, call.Warnings.Count == 0 ? null : call.Warnings);
            var json = JsonSerializer.Serialize(ToolEnvelope<object>.Success(data, meta), AppJson.Options);

            if (idemKey is not null && requestHash is not null)
                await _idempotency.PutAsync(partitionKey, toolName, new IdempotencyEntry(idemKey, requestHash, JsonSerializer.Serialize(data, AppJson.Options), DateTimeOffset.UtcNow), TimeSpan.FromHours(_idemOptions.TtlHours), ct);

            await _audit.RecordAsync(tenant, workspaceId, toolName, "success", null, call.TargetType, call.TargetId, call.AuditSummary, sw.ElapsedMilliseconds, ct);
            await _memory.RecordAsync(tenant, workspaceId, toolName, call.TouchedAdrIds, call.KeywordSources, call.TouchedEvidenceIds, call.TouchedFindingIds, call.AuditSummary, ct);
            _logger.LogInformation("Tool {Tool} succeeded in {Ms} ms", toolName, sw.ElapsedMilliseconds);
            return json;
        }
        catch (DomainException ex)
        {
            _logger.LogWarning("Tool {Tool} failed: {Code} {Message}", toolName, ex.Code, SensitiveDataRedactor.Redact(ex.Message));
            if (tenant is not null) await _audit.RecordAsync(tenant, workspaceId, toolName, "failed", ex.Code, null, null, ex.Message, sw.ElapsedMilliseconds, ct);
            return Error(ex.Code, ex.Message, ex.Retryable, correlationId, toolName, sw.ElapsedMilliseconds, ex.Details);
        }
        catch (OperationCanceledException)
        {
            return Error(ErrorCodes.Internal, "The request was cancelled.", true, correlationId, toolName, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} crashed", toolName);
            if (tenant is not null) await _audit.RecordAsync(tenant, workspaceId, toolName, "error", ErrorCodes.Internal, null, null, ex.GetType().Name, sw.ElapsedMilliseconds, ct);
            // Never leak exception details to the client.
            return Error(ErrorCodes.Internal, "An internal error occurred. Quote the correlationId when reporting it.", false, correlationId, toolName, sw.ElapsedMilliseconds, null);
        }
    }

    public static TReq Bind<TReq>(IReadOnlyDictionary<string, object?>? arguments) where TReq : class
    {
        try
        {
            var json = JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), AppJson.Options);
            if (json.Length > 2_000_000) throw new DomainException(ErrorCodes.PayloadTooLarge, "Tool arguments exceed 2 MB.");
            return JsonSerializer.Deserialize<TReq>(json, AppJson.Options) ?? throw new ValidationException("Arguments could not be bound.");
        }
        catch (JsonException ex)
        {
            throw new ValidationException($"Invalid arguments: {SensitiveDataRedactor.Redact(ex.Message)}");
        }
    }

    private static string ResolveCorrelationId(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var name in new[] { "x-correlation-id", "X-Correlation-Id", "x-request-id", "X-Request-Id" })
            if (headers.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) && v.Length <= 128 && v.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return v;
        if (headers.TryGetValue("traceparent", out var tp) && tp.Length >= 55) return tp.Substring(3, 32);
        return Guid.NewGuid().ToString("N");
    }

    private static string Error(string code, string message, bool retryable, string correlationId, string tool, long ms, IReadOnlyDictionary<string, string>? details)
        => JsonSerializer.Serialize(ToolEnvelope<object>.Failure(new ToolError(code, SensitiveDataRedactor.Redact(message), retryable, correlationId, details), new ToolMeta(correlationId, tool, ms)), AppJson.Options);

    private static string ReplayEnvelope(string dataJson, string correlationId, string tool, long ms)
    {
        var data = JsonSerializer.Deserialize<JsonElement>(dataJson);
        return JsonSerializer.Serialize(ToolEnvelope<JsonElement>.Success(data, new ToolMeta(correlationId, tool, ms, null, true)), AppJson.Options);
    }
}
