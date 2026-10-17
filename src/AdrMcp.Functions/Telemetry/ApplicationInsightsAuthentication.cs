using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Azure.Identity;

namespace AdrMcp.Functions.Telemetry;

/// <summary>
/// The Functions host honours APPLICATIONINSIGHTS_AUTHENTICATION_STRING, but the worker's in-process Application Insights SDK
/// does not: it needs a credential in code, otherwise its telemetry is rejected once local authentication is disabled on the
/// Application Insights resource (infra/modules/monitoring.bicep). This reads the same app setting so host and worker use one
/// identity: "Authorization=AAD" (system-assigned) or "Authorization=AAD;ClientId=&lt;id&gt;" (user-assigned).
/// </summary>
public static class ApplicationInsightsAuthentication
{
    public static bool TryCreateCredential(string? authenticationString, [NotNullWhen(true)] out TokenCredential? credential)
    {
        credential = null;
        if (string.IsNullOrWhiteSpace(authenticationString)) return false;

        string? authorization = null, clientId = null;
        foreach (var part in authenticationString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = value;
            else if (key.Equals("ClientId", StringComparison.OrdinalIgnoreCase)) clientId = value;
        }

        if (!string.Equals(authorization, "AAD", StringComparison.OrdinalIgnoreCase)) return false;
        credential = string.IsNullOrEmpty(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
        return true;
    }
}
