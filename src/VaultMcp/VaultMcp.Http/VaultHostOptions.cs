using System.Text.RegularExpressions;

namespace VaultMcp.Http;

/// <summary>Who may call the MCP endpoint. Settings use <c>Vault__Auth__Mode</c> (the <c>__</c> stands for <c>:</c>).</summary>
public enum VaultAccessMode
{
    /// <summary>Default. Every call to the MCP endpoint is refused with 401 until a sign-in layer is configured. Fails closed.</summary>
    Locked,

    /// <summary>No sign-in: anyone who can reach the address can read the allow-listed folders. For a machine you control only; refused inside Azure Container Apps.</summary>
    Anonymous,

    /// <summary>Microsoft Entra ID sign-in (ADR-0010 phase 2 step 3, the ADR-0008 pattern): a bearer token for the vault server's own app registration, from an allowed client, carrying the scope and the app role. See <see cref="VaultEntraOptions"/>.</summary>
    Entra,
}

/// <summary>
/// The rules for accepting a bearer token in <see cref="VaultAccessMode.Entra"/> mode, read from <c>Vault:Auth:Entra:*</c>.
/// The vault server has its own app registration: its client ID is the token audience, its own delegated scope is required, and
/// one app role decides who may read. Every value is checked at start; a bad one stops the host instead of weakening a check.
/// </summary>
public sealed record VaultEntraOptions(
    string TenantId,
    string ClientId,
    IReadOnlySet<string> AllowedClientIds,
    string RequiredRole,
    string Scope,
    string ResourceUri,
    string AuthorityHost)
{
    public const string SectionKey = "Vault:Auth:Entra";
    public const string TenantIdKey = SectionKey + ":TenantId";
    public const string ClientIdKey = SectionKey + ":ClientId";
    /// <summary>Client (application) IDs of the MCP clients allowed to call, the token's <c>azp</c>: a comma-separated list, or indexed children.</summary>
    public const string AllowedClientIdsKey = SectionKey + ":AllowedClientIds";
    public const string RequiredRoleKey = SectionKey + ":RequiredRole";
    public const string ScopeKey = SectionKey + ":Scope";
    /// <summary>The canonical MCP URL: the RFC 8707 resource and the <c>resource</c> of the protected resource metadata. Derived from the app's stable address (<see cref="VaultHostOptions.ContainerAppsMarker"/> plus <see cref="ContainerAppsDnsSuffixMarker"/>) when unset.</summary>
    public const string ResourceUriKey = SectionKey + ":ResourceUri";
    public const string AuthorityHostKey = SectionKey + ":AuthorityHost";

    public const string DefaultRole = "Vault.Reader";
    public const string DefaultScope = "user_impersonation";
    public const string DefaultAuthorityHost = "login.microsoftonline.com";
    public const string McpPath = "/mcp";

    /// <summary>
    /// Set by Azure Container Apps on every replica: the environment's DNS suffix. The app's stable address is
    /// <c>&lt;CONTAINER_APP_NAME&gt;.&lt;CONTAINER_APP_ENV_DNS_SUFFIX&gt;</c> (Microsoft Learn, "Built-in environment variables"); it is used for the
    /// resource URI when none is configured. <c>CONTAINER_APP_HOSTNAME</c> is deliberately not used: it names one revision, and a token's
    /// resource must stay the same across revisions.
    /// </summary>
    public const string ContainerAppsDnsSuffixMarker = "CONTAINER_APP_ENV_DNS_SUFFIX";

    /// <summary><c>https://&lt;authority host&gt;/&lt;tenant&gt;/v2.0</c>, compared with the token's <c>iss</c> by exact string match; also the OpenID Connect authority.</summary>
    public string Issuer => $"https://{AuthorityHost}/{TenantId}/v2.0";

    /// <summary>The scope clients request from Entra: <c>api://&lt;client id&gt;/&lt;scope&gt;</c>.</summary>
    public string ClientScope => $"api://{ClientId}/{Scope}";

    /// <summary>Canonical form of a GUID (lower-case, hyphenated); anything else is only trimmed.</summary>
    public static string Canonical(string id) => Guid.TryParse(id, out var g) ? g.ToString("D") : id.Trim();

    private static bool IsRealGuid(string? value) => Guid.TryParse(value?.Trim(), out var g) && g != Guid.Empty;
    private static readonly Regex ScopeOrRoleValue = new(@"^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$", RegexOptions.CultureInvariant);

    /// <summary>Reads and checks the section. Messages name the setting and the rule, never the configured value.</summary>
    public static VaultEntraOptions From(IConfiguration configuration, Func<string, string?> environment)
    {
        var failures = new List<string>();

        var tenantId = configuration[TenantIdKey];
        if (!IsRealGuid(tenantId)) failures.Add($"{TenantIdKey} must be the Entra tenant (directory) ID, a GUID.");

        var clientId = configuration[ClientIdKey];
        if (!IsRealGuid(clientId)) failures.Add($"{ClientIdKey} must be the vault server app registration's client ID, a GUID.");

        var clients = ReadList(configuration, AllowedClientIdsKey);
        if (clients.Count == 0) failures.Add($"{AllowedClientIdsKey} must list at least one client application ID (GUID); an empty list would accept every client.");
        for (var i = 0; i < clients.Count; i++)
            if (!IsRealGuid(clients[i])) failures.Add($"{AllowedClientIdsKey} entry {i} must be a GUID.");

        var role = (configuration[RequiredRoleKey] ?? DefaultRole).Trim();
        if (!ScopeOrRoleValue.IsMatch(role)) failures.Add($"{RequiredRoleKey} must be an app role value of letters, digits, '.', '_', ':' or '-'.");

        var scope = (configuration[ScopeKey] ?? DefaultScope).Trim();
        if (!ScopeOrRoleValue.IsMatch(scope)) failures.Add($"{ScopeKey} must be a scope value of letters, digits, '.', '_', ':' or '-'.");
        else if (string.Equals(scope, role, StringComparison.OrdinalIgnoreCase)) failures.Add($"{ScopeKey} equals {RequiredRoleKey}; Entra keeps roles and scopes in one collection, so their values must differ.");

        var host = (configuration[AuthorityHostKey] ?? DefaultAuthorityHost).Trim().ToLowerInvariant();
        if (!HostName.IsMatch(host)) failures.Add($"{AuthorityHostKey} must be a host name only (for example {DefaultAuthorityHost}), with no scheme, path or port.");

        var resource = configuration[ResourceUriKey]?.Trim();
        if (string.IsNullOrEmpty(resource))
        {
            var appName = environment(VaultHostOptions.ContainerAppsMarker)?.Trim();
            var dnsSuffix = environment(ContainerAppsDnsSuffixMarker)?.Trim();
            var platformHost = string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(dnsSuffix) ? null : $"{appName}.{dnsSuffix}";
            if (platformHost is not null && HostName.IsMatch(platformHost)) resource = $"https://{platformHost.ToLowerInvariant()}{McpPath}";
            else failures.Add($"{ResourceUriKey} is required when the platform does not set {VaultHostOptions.ContainerAppsMarker} and {ContainerAppsDnsSuffixMarker}: the MCP endpoint's https URL, for example https://<host>{McpPath}.");
        }
        else
        {
            var ok = Uri.TryCreate(resource, UriKind.Absolute, out var r)
                && string.IsNullOrEmpty(r.UserInfo) && string.IsNullOrEmpty(r.Fragment) && string.IsNullOrEmpty(r.Query)
                && (r.Scheme == Uri.UriSchemeHttps || (r.Scheme == Uri.UriSchemeHttp && r.IsLoopback));
            if (!ok) failures.Add($"{ResourceUriKey} must be the MCP endpoint's https URL (http only for loopback), with no query, fragment or credentials.");
            else resource = r!.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        return new VaultEntraOptions(
            Canonical(tenantId!),
            Canonical(clientId!),
            clients.Select(Canonical).ToHashSet(StringComparer.Ordinal),
            role,
            scope,
            resource!,
            host);
    }

    /// <summary>A list setting given either as one comma- or space-separated value or as indexed children (<c>Key__0</c>, <c>Key__1</c>).</summary>
    private static List<string> ReadList(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var children = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (children.Count > 0) return children;
        return (section.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}

/// <summary>Host settings. The allow-list and everything about the notes stay in <c>adr-sidekick.md</c> inside the vault.</summary>
public sealed record VaultHostOptions(string Path, VaultAccessMode Mode, VaultEntraOptions? Entra = null)
{
    public const string PathKey = "Vault:Path";
    public const string ModeKey = "Vault:Auth:Mode";

    /// <summary>Set by Azure Container Apps on every replica. Anonymous mode is refused when it is present.</summary>
    public const string ContainerAppsMarker = "CONTAINER_APP_NAME";

    /// <summary>Reads and checks the settings; a bad value stops the host with a plain message instead of a weaker default.</summary>
    public static VaultHostOptions From(IConfiguration configuration, Func<string, string?> environment)
    {
        var path = configuration[PathKey] ?? environment("VAULT_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Set {PathKey} (or VAULT_PATH) to the folder that holds the vault copy.");
        }

        // Names only. Enum.TryParse would also accept a number such as "3", which is no declared mode and would slip
        // past the Locked check in Program.cs; a value that is not one of the three names stops the host instead.
        var modeText = configuration[ModeKey]?.Trim();
        var mode = modeText switch
        {
            null or "" => VaultAccessMode.Locked,
            _ when modeText.Equals(nameof(VaultAccessMode.Locked), StringComparison.OrdinalIgnoreCase) => VaultAccessMode.Locked,
            _ when modeText.Equals(nameof(VaultAccessMode.Anonymous), StringComparison.OrdinalIgnoreCase) => VaultAccessMode.Anonymous,
            _ when modeText.Equals(nameof(VaultAccessMode.Entra), StringComparison.OrdinalIgnoreCase) => VaultAccessMode.Entra,
            _ => throw new InvalidOperationException($"{ModeKey} must be Locked, Anonymous or Entra; '{modeText}' is none of them."),
        };

        if (mode == VaultAccessMode.Anonymous && !string.IsNullOrEmpty(environment(ContainerAppsMarker)))
        {
            throw new InvalidOperationException($"{ModeKey}=Anonymous is refused inside Azure Container Apps ({ContainerAppsMarker} is set): the endpoint would be open to anyone who can reach it.");
        }

        var entra = mode == VaultAccessMode.Entra ? VaultEntraOptions.From(configuration, environment) : null;
        return new VaultHostOptions(path, mode, entra);
    }
}
