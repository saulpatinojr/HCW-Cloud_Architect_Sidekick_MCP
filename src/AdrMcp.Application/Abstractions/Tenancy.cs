using AdrMcp.Domain.Tenancy;

namespace AdrMcp.Application.Abstractions;

/// <summary>Resolves the caller from transport metadata (headers) and server configuration. Tool arguments are never consulted.</summary>
public interface ITenantResolver
{
    Task<TenantContext> ResolveAsync(IReadOnlyDictionary<string, string> headers, string? sessionId, string correlationId, CancellationToken ct);
}

public interface IAuthorizationService
{
    void Require(TenantContext ctx, Permission permission);
    /// <summary>Resolves the effective workspace: the argument if allowed, else the principal's default. Throws Forbidden on mismatch.</summary>
    string ResolveWorkspace(TenantContext ctx, string? requestedWorkspaceId);
}
