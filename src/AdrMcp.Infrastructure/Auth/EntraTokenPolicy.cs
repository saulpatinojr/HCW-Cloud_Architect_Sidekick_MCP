using System.Text.RegularExpressions;
using AdrMcp.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AdrMcp.Infrastructure.Auth;

/// <summary>
/// The resolved, immutable rules for accepting a bearer token (ADR-0008). Built once from <see cref="EntraTokenOptions"/> after
/// <see cref="EntraTokenOptionsValidator"/> has accepted them, so the validator and the HTTP helpers never re-parse settings.
/// </summary>
public sealed partial class EntraTokenPolicy
{
    /// <summary>The path the Functions MCP extension serves; the canonical resource is the host plus this path.</summary>
    public const string McpEndpointPath = "/runtime/webhooks/mcp";
    public const string PlatformHostNameSetting = "WEBSITE_HOSTNAME";

    private EntraTokenPolicy() { }

    public string TenantId { get; private init; } = "";
    public string ClientId { get; private init; } = "";
    /// <summary><c>https://&lt;authority host&gt;/&lt;tenant&gt;/v2.0</c>, compared with the token's <c>iss</c> by exact string match.</summary>
    public string Issuer { get; private init; } = "";
    public Uri JwksUri { get; private init; } = new("https://invalid.example/");
    public IReadOnlySet<string> Audiences { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> AllowedClientIds { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> RequiredScopes { get; private init; } = new HashSet<string>();
    public bool AllowApplicationTokens { get; private init; }
    public TimeSpan ClockSkew { get; private init; }
    public TimeSpan JwksCacheTtl { get; private init; }
    /// <summary>Canonical MCP URL, the RFC 8707 <c>resource</c> and the PRM <c>resource</c>.</summary>
    public string ResourceUri { get; private init; } = "";

    /// <summary>The scope clients request from Entra: <c>api://&lt;client id&gt;/&lt;scope&gt;</c> (the form App Service puts in its metadata).</summary>
    public string ClientScope => $"api://{ClientId}/{RequiredScopes.OrderBy(s => s, StringComparer.Ordinal).First()}";

    /// <summary>Builds the policy. The options must already have passed <see cref="EntraTokenOptionsValidator"/>.</summary>
    public static EntraTokenPolicy From(EntraTokenOptions o, string? platformHostName)
    {
        var tenant = Canonical(o.TenantId);
        var audiences = new HashSet<string>(StringComparer.Ordinal) { Canonical(o.ClientId) };
        foreach (var a in o.Audiences.Where(a => !string.IsNullOrWhiteSpace(a))) audiences.Add(Guid.TryParse(a, out var g) ? g.ToString("D") : a.Trim());
        var scopes = o.RequiredScopes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        if (scopes.Count == 0) scopes.Add(EntraTokenOptions.DefaultScope);
        var host = string.IsNullOrWhiteSpace(o.AuthorityHost) ? EntraTokenOptions.DefaultAuthorityHost : o.AuthorityHost.Trim().ToLowerInvariant();
        var jwks = string.IsNullOrWhiteSpace(o.JwksUrl) ? new Uri($"https://{host}/{tenant}/discovery/v2.0/keys") : new Uri(o.JwksUrl.Trim());
        var resource = string.IsNullOrWhiteSpace(o.ResourceUri)
            ? $"https://{platformHostName!.Trim().ToLowerInvariant()}{McpEndpointPath}"
            : new Uri(o.ResourceUri.Trim()).GetLeftPart(UriPartial.Path);
        return new EntraTokenPolicy
        {
            TenantId = tenant,
            ClientId = Canonical(o.ClientId),
            Issuer = $"https://{host}/{tenant}/v2.0",
            JwksUri = jwks,
            Audiences = audiences,
            AllowedClientIds = o.AllowedClientIds.Where(c => !string.IsNullOrWhiteSpace(c)).Select(Canonical).ToHashSet(StringComparer.Ordinal),
            RequiredScopes = scopes,
            AllowApplicationTokens = o.AllowApplicationTokens,
            ClockSkew = TimeSpan.FromSeconds(o.ClockSkewSeconds),
            JwksCacheTtl = TimeSpan.FromMinutes(o.JwksCacheMinutes),
            ResourceUri = resource,
        };
    }

    /// <summary>Canonical form of a GUID (lower-case, hyphenated); anything else is only trimmed.</summary>
    public static string Canonical(string id) => Guid.TryParse(id, out var g) ? g.ToString("D") : id.Trim();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$")]
    internal static partial Regex HostNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,128}$")]
    internal static partial Regex ScopeValuePattern();

    internal static bool IsLoopback(Uri uri) => uri.IsLoopback;
}

/// <summary>
/// Fails host start on settings that cannot mean what the operator intended, instead of weakening a check: a mistyped tenant or
/// client ID, an empty allow-list, a scope that is also an app role value, a key address that is not https, a tenant that is not
/// enrolled. Does nothing while <c>Auth:Entra:ValidateBearerToken</c> is off. Never echoes a configured value.
/// </summary>
public sealed class EntraTokenOptionsValidator : IValidateOptions<AuthOptions>
{
    private readonly string? _platformHostName;

    public EntraTokenOptionsValidator(IConfiguration configuration) => _platformHostName = configuration[EntraTokenPolicy.PlatformHostNameSetting];

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var e = options.Entra;
        if (!e.ValidateBearerToken) return ValidateOptionsResult.Success;
        var failures = new List<string>();

        if (!string.Equals(options.Mode, "EntraBuiltIn", StringComparison.OrdinalIgnoreCase))
            failures.Add("Auth:Entra:ValidateBearerToken requires Auth:Mode=EntraBuiltIn.");
        if (!IsRealGuid(e.TenantId)) failures.Add("Auth:Entra:TenantId must be the Entra tenant (directory) ID, a GUID.");
        if (!IsRealGuid(e.ClientId)) failures.Add("Auth:Entra:ClientId must be the server app registration's client ID, a GUID.");

        var clients = e.AllowedClientIds.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (clients.Count == 0) failures.Add("Auth:Entra:AllowedClientIds must list at least one client application ID (GUID); an empty list would accept every client.");
        for (var i = 0; i < clients.Count; i++)
            if (!IsRealGuid(clients[i])) failures.Add($"Auth:Entra:AllowedClientIds entry {i} must be a GUID.");

        for (var i = 0; i < e.Audiences.Length; i++)
        {
            var a = e.Audiences[i]?.Trim() ?? "";
            if (a.Length == 0 || a.Contains('*') || a.Any(char.IsWhiteSpace) || !(IsRealGuid(a) || (Uri.TryCreate(a, UriKind.Absolute, out var u) && (u.Scheme is "api" or "https") && string.IsNullOrEmpty(u.Fragment))))
                failures.Add($"Auth:Entra:Audiences entry {i} must be a client ID (GUID), an api:// URI or an https URI, without wildcards.");
        }

        var roleValues = new HashSet<string>(options.AppRoleMap.Keys, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < e.RequiredScopes.Length; i++)
        {
            var s = e.RequiredScopes[i]?.Trim() ?? "";
            if (!EntraTokenPolicy.ScopeValuePattern().IsMatch(s)) failures.Add($"Auth:Entra:RequiredScopes entry {i} must be a scope value of letters, digits, '.', '_', ':' or '-'.");
            else if (roleValues.Contains(s)) failures.Add($"Auth:Entra:RequiredScopes entry {i} is also an app role value; Entra keeps roles and scopes in one collection, so their values must differ.");
        }
        if (roleValues.Contains(EntraTokenOptions.DefaultScope) && e.RequiredScopes.Length == 0)
            failures.Add($"The default scope '{EntraTokenOptions.DefaultScope}' is also an app role value in Auth:AppRoleMap; their values must differ.");

        var host = e.AuthorityHost?.Trim() ?? "";
        if (!EntraTokenPolicy.HostNamePattern().IsMatch(host)) failures.Add("Auth:Entra:AuthorityHost must be a host name only (for example login.microsoftonline.com), with no scheme, path or port.");

        if (!string.IsNullOrWhiteSpace(e.JwksUrl))
        {
            var ok = Uri.TryCreate(e.JwksUrl.Trim(), UriKind.Absolute, out var jwks)
                && string.IsNullOrEmpty(jwks.UserInfo) && string.IsNullOrEmpty(jwks.Fragment)
                && (jwks.Scheme == Uri.UriSchemeHttps || (jwks.Scheme == Uri.UriSchemeHttp && jwks.IsLoopback));
            if (!ok) failures.Add("Auth:Entra:JwksUrl must be an https address (http is accepted only for a loopback address in local tests), without credentials or a fragment.");
        }

        if (e.ClockSkewSeconds is < 0 or > 300) failures.Add("Auth:Entra:ClockSkewSeconds must be between 0 and 300.");
        if (e.JwksCacheMinutes is < 5 or > 1440) failures.Add("Auth:Entra:JwksCacheMinutes must be between 5 and 1440.");

        if (!string.IsNullOrWhiteSpace(e.ResourceUri))
        {
            var ok = Uri.TryCreate(e.ResourceUri.Trim(), UriKind.Absolute, out var r)
                && string.IsNullOrEmpty(r.UserInfo) && string.IsNullOrEmpty(r.Fragment) && string.IsNullOrEmpty(r.Query)
                && (r.Scheme == Uri.UriSchemeHttps || (r.Scheme == Uri.UriSchemeHttp && r.IsLoopback));
            if (!ok) failures.Add("Auth:Entra:ResourceUri must be the MCP endpoint's https URL (http only for loopback), with no query, fragment or credentials.");
        }
        else if (string.IsNullOrWhiteSpace(_platformHostName) || !EntraTokenPolicy.HostNamePattern().IsMatch(_platformHostName.Trim()))
        {
            failures.Add($"Auth:Entra:ResourceUri is required when the platform does not set {EntraTokenPolicy.PlatformHostNameSetting}.");
        }

        // The tenant the tokens come from must be one the app serves, or every tool call would pass the token check and then be refused.
        var enrolled = options.Tenants.Where(t => !string.IsNullOrWhiteSpace(t.Id)).Select(t => EntraTokenPolicy.Canonical(t.Id))
            .Concat(options.TenantMap.Keys.Select(EntraTokenPolicy.Canonical)).ToList();
        if (enrolled.Count > 0 && IsRealGuid(e.TenantId) && !enrolled.Contains(EntraTokenPolicy.Canonical(e.TenantId), StringComparer.Ordinal))
            failures.Add("Auth:Entra:TenantId is not one of the enrolled tenants (Auth:Tenants or Auth:TenantMap).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsRealGuid(string? value) => Guid.TryParse(value?.Trim(), out var g) && g != Guid.Empty;
}
