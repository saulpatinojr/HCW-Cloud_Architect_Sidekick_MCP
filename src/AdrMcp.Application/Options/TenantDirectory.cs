using System.Diagnostics.CodeAnalysis;

namespace AdrMcp.Application.Options;

/// <summary>
/// One view of tenant enrollment, mapping and workspaces, built once from <see cref="AuthOptions"/> and shared by tenant
/// resolution and scheduled maintenance. It merges the dictionary form (<c>Auth:TenantMap</c>, <c>Auth:TenantWorkspaces</c>;
/// fine in JSON and for key-safe names) with the array form <c>Auth:Tenants</c>, the only form Linux app settings can express
/// for Entra tenant IDs: setting names allow letters, digits and '_' only, so a GUID can never be a key there.
/// </summary>
public sealed class TenantDirectory
{
    private readonly AuthOptions _o;
    private readonly HashSet<string> _enrolled = new(StringComparer.OrdinalIgnoreCase);                 // Entra tids
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);           // Entra tid -> ADR tenant id
    private readonly Dictionary<string, HashSet<string>> _workspaces = new(StringComparer.OrdinalIgnoreCase); // ADR tenant id -> workspaces

    public TenantDirectory(AuthOptions options)
    {
        _o = options;
        foreach (var (tid, tenantId) in options.TenantMap) { _enrolled.Add(tid); _map[tid] = tenantId; }
        foreach (var (tenantId, workspaces) in options.TenantWorkspaces) AddWorkspaces(tenantId, workspaces);
        foreach (var t in options.Tenants.Where(t => !string.IsNullOrWhiteSpace(t.Id)))
        {
            var tid = Canonical(t.Id);
            _enrolled.Add(tid);
            if (!string.IsNullOrWhiteSpace(t.MapTo)) _map[tid] = t.MapTo.Trim();
            AddWorkspaces(string.IsNullOrWhiteSpace(t.MapTo) ? tid : t.MapTo.Trim(), t.Workspaces);
        }
    }

    /// <summary>True when any tenant is enrolled; every other Entra tenant is then refused.</summary>
    public bool RequiresEnrollment => _enrolled.Count > 0;

    /// <summary>Maps the caller's Entra tenant (tid claim) to its ADR tenant ID; false when enrollment is required and it is not enrolled.
    /// An enrolled tenant without MapTo is stored under its canonical tid (lower-case GUID), the same key scheduled maintenance uses,
    /// however the operator spelled it.</summary>
    public bool TryResolveTenant(string entraTenantId, [NotNullWhen(true)] out string? tenantId)
    {
        if (_map.TryGetValue(entraTenantId, out tenantId)) return true;
        if (!RequiresEnrollment) { tenantId = entraTenantId; return true; }   // unchanged: the tid exactly as issued
        tenantId = _enrolled.Contains(entraTenantId) ? Canonical(entraTenantId) : null;
        return tenantId is not null;
    }

    /// <summary>Workspaces the tenant may use ("*" allows any), or only <paramref name="fallback"/> when none are configured.</summary>
    public HashSet<string> WorkspacesFor(string tenantId, string fallback)
        => new(_workspaces.TryGetValue(tenantId, out var ws) && ws.Count > 0 ? ws : new[] { fallback }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Concrete tenant/workspace pairs for scheduled maintenance; "*" names no workspace, so it is skipped.</summary>
    public IEnumerable<(string TenantId, string WorkspaceId)> KnownScopes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(_o.Mode, "FunctionKey", StringComparison.OrdinalIgnoreCase) && seen.Add($"{_o.StaticTenantId}|{_o.StaticWorkspaceId}"))
            yield return (_o.StaticTenantId, _o.StaticWorkspaceId);
        var tenants = _workspaces.Keys.Concat(_map.Values).Concat(_enrolled.Where(tid => !_map.ContainsKey(tid)));
        foreach (var tenantId in tenants.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var ws in WorkspacesFor(tenantId, _o.DefaultWorkspaceId).Where(w => w != "*"))
                if (seen.Add($"{tenantId}|{ws}")) yield return (tenantId, ws);
    }

    private static string Canonical(string tid) => Guid.TryParse(tid, out var g) ? g.ToString("D") : tid.Trim();

    private void AddWorkspaces(string tenantId, IEnumerable<string> workspaces)
    {
        if (!_workspaces.TryGetValue(tenantId, out var set)) _workspaces[tenantId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ws in workspaces.Where(w => !string.IsNullOrWhiteSpace(w))) set.Add(ws.Trim());
    }
}
