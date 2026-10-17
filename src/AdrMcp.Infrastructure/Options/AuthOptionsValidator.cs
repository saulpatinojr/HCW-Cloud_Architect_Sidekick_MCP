using AdrMcp.Application.Options;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure.Options;

/// <summary>
/// Fails startup on tenant enrollments that cannot mean what the operator intended: a missing or non-GUID tenant ID (a typo would
/// silently enroll nothing while still refusing every other tenant), or a tenant configured twice with possibly different mappings.
/// </summary>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(options.TenantMap.Keys, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.Tenants.Count; i++)
        {
            var id = options.Tenants[i].Id?.Trim() ?? "";
            if (!Guid.TryParse(id, out _)) failures.Add($"Auth:Tenants:{i}:Id must be an Entra tenant ID (GUID).");
            else if (!seen.Add(id)) failures.Add($"Entra tenant {id} is configured more than once (Auth:Tenants or Auth:TenantMap).");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
