using System.Text;
using System.Text.Json;
using AdrMcp.Application.Abstractions;
using AdrMcp.Application.Options;
using AdrMcp.Domain.Errors;
using AdrMcp.Domain.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure.Tenancy;

/// <summary>
/// Whether App Service Authentication runs in front of this app. The platform injects the read-only WEBSITE_AUTH_ENABLED
/// variable when it does; nothing a caller sends (headers, tool arguments) can influence it.
/// </summary>
public sealed record PlatformAuthentication(bool Enabled)
{
    public const string EnabledSetting = "WEBSITE_AUTH_ENABLED";

    public static PlatformAuthentication FromConfiguration(IConfiguration configuration)
        => new(string.Equals(configuration[EnabledSetting], "true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Resolves the caller from transport headers. Two modes:
///  - EntraBuiltIn: App Service Authentication (built-in MCP auth) validates the token at the edge and injects X-MS-CLIENT-PRINCIPAL;
///    inbound copies of X-MS-* headers are stripped by the platform, so the header is trustworthy only in this mode.
///  - FunctionKey: single-tenant deployment or local development; the caller proved possession of the mcp_extension system key
///    at the edge and is bound to a configured tenant/workspace/roles. X-MS-CLIENT-PRINCIPAL is IGNORED in this mode (spoofable).
/// Tool arguments are never consulted.
/// </summary>
public sealed class HeaderTenantResolver : ITenantResolver
{
    private readonly AuthOptions _o;
    private readonly TenantDirectory _tenants;
    private readonly PlatformAuthentication _platform;
    private readonly ILogger<HeaderTenantResolver> _logger;

    public HeaderTenantResolver(IOptions<AuthOptions> o, TenantDirectory tenants, PlatformAuthentication platform, ILogger<HeaderTenantResolver> logger)
    {
        _o = o.Value; _tenants = tenants; _platform = platform; _logger = logger;
        if (IsEntra && !_platform.Enabled)
            _logger.LogError("Auth:Mode is EntraBuiltIn but App Service Authentication is not enabled ({Setting} is not true); every request will be rejected.", PlatformAuthentication.EnabledSetting);
    }

    private bool IsEntra => string.Equals(_o.Mode, "EntraBuiltIn", StringComparison.OrdinalIgnoreCase);

    public Task<TenantContext> ResolveAsync(IReadOnlyDictionary<string, string> headers, string? sessionId, string correlationId, CancellationToken ct)
    {
        if (IsEntra) return Task.FromResult(ResolveEntra(headers, sessionId, correlationId));
        if (string.Equals(_o.Mode, "FunctionKey", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(ResolveStatic(sessionId, correlationId));
        throw new DomainException(ErrorCodes.Unauthorized, $"Unsupported Auth:Mode '{_o.Mode}'.");
    }

    private TenantContext ResolveStatic(string? sessionId, string correlationId)
    {
        var roles = _o.StaticRoles.Select(r => Enum.TryParse<Role>(r, true, out var role) ? role : (Role?)null).Where(r => r is not null).Select(r => r!.Value).ToHashSet();
        if (roles.Count == 0) { roles.Add(Role.Reader); _logger.LogWarning("Auth:StaticRoles is empty; the function-key principal is Reader only."); }
        var workspaces = _tenants.WorkspacesFor(_o.StaticTenantId, _o.StaticWorkspaceId);
        return new TenantContext(_o.StaticTenantId, _o.StaticPrincipalId, _o.StaticPrincipalId, roles, workspaces, _o.StaticWorkspaceId, "FunctionKey", correlationId, sessionId);
    }

    private TenantContext ResolveEntra(IReadOnlyDictionary<string, string> headers, string? sessionId, string correlationId)
    {
        // The header is trustworthy only while App Service Authentication sits in front of the app and strips inbound copies.
        // If it has been turned off (portal change, drifted deployment), the header is caller-controlled: fail closed.
        if (!_platform.Enabled) throw new DomainException(ErrorCodes.Unauthorized, "Entra mode requires App Service Authentication, which is not enabled on this app.");
        var encoded = Get(headers, "X-MS-CLIENT-PRINCIPAL");
        if (string.IsNullOrEmpty(encoded)) throw new DomainException(ErrorCodes.Unauthorized, "No authenticated principal. Is built-in authentication enabled and required?");
        ClientPrincipal principal;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            principal = JsonSerializer.Deserialize<ClientPrincipal>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new FormatException("empty principal");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            _logger.LogWarning("Malformed X-MS-CLIENT-PRINCIPAL header ({Correlation})", correlationId);
            throw new DomainException(ErrorCodes.Unauthorized, "Malformed client principal.");
        }
        string? Claim(params string[] types) => principal.Claims?.FirstOrDefault(c => types.Contains(c.Typ, StringComparer.OrdinalIgnoreCase))?.Val;
        var tid = Claim("tid", "http://schemas.microsoft.com/identity/claims/tenantid");
        var oid = Claim("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier") ?? Claim("sub");
        var appId = Claim("azp", "appid");
        if (tid is null || oid is null) throw new DomainException(ErrorCodes.Unauthorized, "Principal is missing tenant or object identifier claims.");
        var display = Claim("preferred_username", "upn", "name") ?? oid;

        if (!_tenants.TryResolveTenant(tid, out var tenantId)) throw new DomainException(ErrorCodes.Forbidden, "This Entra tenant is not enrolled.");
        var roleClaims = principal.Claims?.Where(c => c.Typ is "roles" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role").Select(c => c.Val) ?? Array.Empty<string>();
        var roles = new HashSet<Role>();
        foreach (var rc in roleClaims) if (_o.AppRoleMap.TryGetValue(rc, out var mappedRole) && Enum.TryParse<Role>(mappedRole, true, out var role)) roles.Add(role);
        var grantsDefault = !string.Equals(_o.DefaultRoleWhenNoAppRoles, "None", StringComparison.OrdinalIgnoreCase);
        if (roles.Count == 0 && grantsDefault && Enum.TryParse<Role>(_o.DefaultRoleWhenNoAppRoles, true, out var def)) roles.Add(def);
        var workspaces = _tenants.WorkspacesFor(tenantId, _o.DefaultWorkspaceId);
        var defaultWs = workspaces.Contains(_o.DefaultWorkspaceId) || workspaces.Contains("*") ? _o.DefaultWorkspaceId : workspaces.First();
        return new TenantContext(tenantId, appId is not null && oid == appId ? $"app:{appId}" : $"user:{oid}", display, roles, workspaces, defaultWs, "EntraBuiltIn", correlationId, sessionId);
    }

    private static string? Get(IReadOnlyDictionary<string, string> headers, string name)
        => headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed class ClientPrincipal
    {
        public string? Auth_typ { get; set; }
        public string? Name_typ { get; set; }
        public string? Role_typ { get; set; }
        public List<ClaimEntry>? Claims { get; set; }
    }
    private sealed class ClaimEntry { public string Typ { get; set; } = ""; public string Val { get; set; } = ""; }
}

public sealed class RoleAuthorizationService : IAuthorizationService
{
    public void Require(TenantContext ctx, Permission permission)
    {
        if (ctx.Roles.Count == 0)
            throw new ForbiddenException("No ADR role is assigned to this principal. Assign an app role (Adr.Reader, Adr.Author, Adr.Approver or Adr.Admin) on the server's app registration.");
        if (!RolePermissions.Has(ctx.Roles, permission))
            throw new ForbiddenException($"Permission '{permission}' requires a role you do not have ({string.Join(", ", ctx.Roles)}).");
    }

    public string ResolveWorkspace(TenantContext ctx, string? requested)
    {
        var ws = string.IsNullOrWhiteSpace(requested) ? ctx.DefaultWorkspaceId : requested.Trim();
        if (string.IsNullOrWhiteSpace(ws)) throw new ValidationException("workspaceId is required.");
        if (ws.Length > 64 || !ws.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) throw new ValidationException("workspaceId contains invalid characters.");
        if (!ctx.CanAccessWorkspace(ws)) throw new ForbiddenException($"Workspace '{ws}' is not accessible to this principal.", ErrorCodes.TenantMismatch);
        return ws;
    }
}
