using AdrMcp.Application.Options;
using AdrMcp.Infrastructure.Tenancy;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure.Options;

/// <summary>
/// Fails startup on tenant configuration that cannot mean what the operator intended:
///  - an Entra tenant ID that is not a GUID (a typo enrolls nothing while still refusing every other tenant);
///  - the same Entra tenant twice, in any GUID spelling (the last entry would silently win);
///  - two Entra tenants resolving to one ADR tenant (for example a copied Auth__Tenants block): they would share a partition
///    and merge their workspace lists;
///  - ADR tenant or workspace IDs outside the identifier rule, since they become partition keys and blob path segments.
/// </summary>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();
        var tids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // ADR tenant id -> first entry using it

        void Enroll(string? tid, string? mapTo, string idLabel, string entry)
        {
            if (!Guid.TryParse(tid?.Trim(), out var g)) { failures.Add($"{idLabel} must be an Entra tenant ID (GUID)."); return; }
            var canonical = g.ToString("D");
            if (!tids.Add(canonical)) { failures.Add($"Entra tenant {canonical} is configured more than once (Auth:Tenants or Auth:TenantMap)."); return; }
            var target = string.IsNullOrWhiteSpace(mapTo) ? canonical : mapTo.Trim();
            if (!Identifiers.IsValid(target)) failures.Add($"{entry}: the ADR tenant ID must be 1-64 letters, digits, '-', '_' or '.'.");
            else if (targets.TryGetValue(target, out var first)) failures.Add($"{entry} and {first} both resolve to ADR tenant '{target}'; each Entra tenant needs its own.");
            else targets[target] = entry;
        }

        void Workspaces(IEnumerable<string> workspaces, string entry)
            => failures.AddRange(workspaces.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim())
                .Where(ws => ws != "*" && !Identifiers.IsValid(ws))
                .Select(ws => $"{entry}: workspace '{ws}' must be '*' or 1-64 letters, digits, '-', '_' or '.'."));

        foreach (var (tid, mapped) in options.TenantMap) Enroll(tid, mapped, $"Auth:TenantMap key '{tid}'", $"Auth:TenantMap:{tid}");
        for (var i = 0; i < options.Tenants.Count; i++)
        {
            Enroll(options.Tenants[i].Id, options.Tenants[i].MapTo, $"Auth:Tenants:{i}:Id", $"Auth:Tenants:{i}");
            Workspaces(options.Tenants[i].Workspaces, $"Auth:Tenants:{i}");
        }
        foreach (var (tenantId, workspaces) in options.TenantWorkspaces)
        {
            if (!Identifiers.IsValid(tenantId.Trim())) failures.Add($"Auth:TenantWorkspaces key '{tenantId}' must be 1-64 letters, digits, '-', '_' or '.'.");
            Workspaces(workspaces, $"Auth:TenantWorkspaces:{tenantId}");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
