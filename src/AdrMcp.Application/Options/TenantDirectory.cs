using System.Diagnostics.CodeAnalysis;

namespace AdrMcp.Application.Options;

/// <summary>
/// One view of tenant enrollment, mapping and workspaces, built once from <see cref="AuthOptions"/> and shared by tenant
/// resolution and scheduled maintenance. It merges the dictionary form (<c>Auth:TenantMap</c>, <c>Auth:TenantWorkspaces</c>;
/// fine in JSON and for key-safe names) with the array form <c>Auth:Tenants</c>, the only form Linux app settings can express
/// for Entra tenant IDs: setting names allow letters, digits and '_' only, so a GUID can never be a key there.
/// ADR tenant IDs are partition keys, so they compare ordinally; Entra tenant IDs (GUIDs) are compared in canonical form.
/// </summary>
public sealed class TenantDirectory
{
    private readonly AuthOptions _o;
    private readonly Dictionary<string, string> _enrolled = new(StringComparer.Ordinal);               // canonical tid -> ADR tenant id
    private readonly Dictionary<string, HashSet<string>> _workspaces = new(StringComparer.Ordinal);   // ADR tenant id -> workspaces

    public TenantDirectory(AuthOptions options)
    {
        _o = options;
        foreach (var (tenantId, workspaces) in options.TenantWorkspaces) AddWorkspaces(Canonical(tenantId), workspaces);
        // Enrollment and mapping describe Entra principals; the FunctionKey binding keeps using Auth:TenantWorkspaces only.
        if (!string.Equals(options.Mode, "EntraBuiltIn", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var (tid, tenantId) in options.TenantMap) Enroll(tid, tenantId, Array.Empty<string>());
        foreach (var t in options.Tenants.Where(t => !string.IsNullOrWhiteSpace(t.Id))) Enroll(t.Id, t.MapTo, t.Workspaces);
    }

    /// <summary>True when any tenant is enrolled; every other Entra tenant is then refused.</summary>
    public bool RequiresEnrollment => _enrolled.Count > 0;

    /// <summary>Maps the caller's Entra tenant (tid claim) to its ADR tenant ID; false when enrollment is required and it is not enrolled.
    /// Without MapTo the ADR tenant ID is the canonical tid (lower-case GUID, as Entra issues it), the key maintenance also uses.</summary>
    public bool TryResolveTenant(string entraTenantId, [NotNullWhen(true)] out string? tenantId)
    {
        var tid = Canonical(entraTenantId);
        if (_enrolled.TryGetValue(tid, out tenantId)) return true;
        tenantId = RequiresEnrollment ? null : tid;
        return tenantId is not null;
    }

    /// <summary>Workspaces the tenant may use ("*" allows any), or only <paramref name="fallback"/> when none are configured.</summary>
    public HashSet<string> WorkspacesFor(string tenantId, string fallback)
        => new(_workspaces.TryGetValue(tenantId, out var ws) && ws.Count > 0 ? ws : new[] { fallback }, StringComparer.OrdinalIgnoreCase);

    /// <summary>Concrete tenant/workspace pairs for scheduled maintenance, built from the same IDs resolution produces;
    /// "*" names no workspace, so it is skipped.</summary>
    public IEnumerable<(string TenantId, string WorkspaceId)> KnownScopes()
    {
        var binding = string.Equals(_o.Mode, "FunctionKey", StringComparison.OrdinalIgnoreCase)
            ? new[] { (_o.StaticTenantId, _o.StaticWorkspaceId) }
            : Array.Empty<(string, string)>();
        var configured = _workspaces.Keys.Concat(_enrolled.Values).Distinct(StringComparer.Ordinal)
            .SelectMany(tenantId => WorkspacesFor(tenantId, _o.DefaultWorkspaceId).Where(ws => ws != "*").Select(ws => (tenantId, ws)));
        return binding.Concat(configured).Distinct();
    }

    /// <summary>Canonical form of an Entra tenant ID (lower-case hyphenated GUID); other values are only trimmed.</summary>
    public static string Canonical(string id) => Guid.TryParse(id, out var g) ? g.ToString("D") : id.Trim();

    private void Enroll(string tid, string? mapTo, IEnumerable<string> workspaces)
    {
        var tenantId = string.IsNullOrWhiteSpace(mapTo) ? Canonical(tid) : mapTo.Trim();
        _enrolled[Canonical(tid)] = tenantId;
        AddWorkspaces(tenantId, workspaces);
    }

    private void AddWorkspaces(string tenantId, IEnumerable<string> workspaces)
    {
        if (!_workspaces.TryGetValue(tenantId, out var set)) _workspaces[tenantId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ws in workspaces.Where(w => !string.IsNullOrWhiteSpace(w))) set.Add(ws.Trim());
    }
}
