namespace AdrMcp.Application.Options;

public sealed class AdrOptions
{
    public const string Section = "Adr";

    public string AnalyzerVersion { get; set; } = "1.0.0";
    public int MaxListPageSize { get; set; } = 50;
    public int MaxSearchTerms { get; set; } = 8;
    public int MaxAdrFieldLength { get; set; } = 20_000;
    public int MaxMarkdownLength { get; set; } = 200_000;
    public int ProposalExpiryDays { get; set; } = 30;
    public int HistoryPageSize { get; set; } = 20;
    public string PrincipalHashSalt { get; set; } = "change-me-via-keyvault";
}

public sealed class UploadOptions
{
    public const string Section = "Uploads";

    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
    public long MaxTextScanBytes { get; set; } = 10L * 1024 * 1024;
    public int UploadUrlTtlMinutes { get; set; } = 15;
    public int QuarantineRetentionHours { get; set; } = 24;
    public bool BlockOnCriticalSafetyFinding { get; set; } = true;
    public bool RequireDefenderScanResult { get; set; } = false;
    public bool RetainOriginalOnReject { get; set; } = false;
    public string[] AllowedExtensions { get; set; } =
    {
        ".md", ".txt", ".adoc", ".json", ".yaml", ".yml", ".toml", ".xml", ".csproj", ".fsproj", ".vbproj", ".sln", ".props", ".targets",
        ".bicep", ".bicepparam", ".tf", ".tfvars", ".hcl", ".dockerfile", ".cs", ".ts", ".js", ".py", ".java", ".go", ".rb", ".php", ".rs",
        ".cshtml", ".razor", ".sql", ".ps1", ".sh", ".env.sample", ".lock", ".config", ".ini", ".properties", ".gradle", ".kts", ".pom",
        ".csv", ".png", ".jpg", ".jpeg", ".svg", ".pdf", ".zip", ".drawio", ".puml", ".mmd"
    };
    public string[] AllowedMediaTypePrefixes { get; set; } = { "text/", "application/json", "application/xml", "application/x-yaml", "application/yaml", "application/toml", "application/zip", "application/pdf", "image/png", "image/jpeg", "image/svg+xml", "application/octet-stream" };
    public string[] ExtensionlessAllowedNames { get; set; } = { "Dockerfile", "Makefile", "Jenkinsfile", "Chart.yaml", "values.yaml", "requirements.txt", "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "go.mod", "go.sum", "Gemfile", "Gemfile.lock", "Pipfile", "pyproject.toml", "poetry.lock" };
    public int ArchiveMaxEntries { get; set; } = 2000;
    public long ArchiveMaxUncompressedBytes { get; set; } = 200L * 1024 * 1024;
    public int ArchiveMaxCompressionRatio { get; set; } = 100;
}

public sealed class JobOptions
{
    public const string Section = "Jobs";
    public int ResultRetentionDays { get; set; } = 30;
    public int MaxAttempts { get; set; } = 3;
    public int MaxEvidenceFilesPerScan { get; set; } = 500;
    public int MaxBytesPerEvidenceFile { get; set; } = 2 * 1024 * 1024;
}

public sealed class MemoryOptions
{
    public const string Section = "Memory";
    public int RetentionDays { get; set; } = 180;
    public int MaxKeywordsPerRecord { get; set; } = 32;
    public double RecencyHalfLifeDays { get; set; } = 30;
}

public sealed class AuditOptions
{
    public const string Section = "Audit";
    public int RetentionDays { get; set; } = 365;
}

public sealed class IdempotencyOptions
{
    public const string Section = "Idempotency";
    public int TtlHours { get; set; } = 24;
}

/// <summary>Authentication mode and tenant binding. See docs/04-security-threat-model.md.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";
    /// <summary>"EntraBuiltIn" (App Service Authentication at the edge; the principal comes from X-MS-CLIENT-PRINCIPAL, or from the verified bearer token when <see cref="EntraTokenOptions.ValidateBearerToken"/> is on) or "FunctionKey" (single-tenant, static binding).</summary>
    public string Mode { get; set; } = "FunctionKey";
    public string StaticTenantId { get; set; } = "local-tenant";
    public string StaticWorkspaceId { get; set; } = "default";
    public string StaticPrincipalId { get; set; } = "function-key-client";
    /// <summary>Roles for the static principal. Empty means Reader only (least privilege). NOTE: configuration arrays append to defaults, so keep defaults empty.</summary>
    public string[] StaticRoles { get; set; } = Array.Empty<string>();
    /// <summary>
    /// The one app role an application (a pipeline signing in with client credentials, no user) may hold: read-only by owner decision
    /// (docs/12, A4). <c>scripts/entra/app-roles.json</c> declares it with <c>allowedMemberTypes: ["Application"]</c>; the four user roles are users only.
    /// </summary>
    public const string ApplicationReaderRole = "Adr.Reader.Application";
    /// <summary>Entra app role value -> ADR role. Defaults map Adr.Reader/Adr.Author/Adr.Approver/Adr.Admin for people and <see cref="ApplicationReaderRole"/> for pipelines.</summary>
    public Dictionary<string, string> AppRoleMap { get; set; } = new()
    {
        ["Adr.Reader"] = "Reader", ["Adr.Author"] = "Author", ["Adr.Approver"] = "Approver", ["Adr.Admin"] = "Admin", [ApplicationReaderRole] = "Reader"
    };
    /// <summary>Entra tenant id (tid claim) -> ADR tenant id. Any entry here or in <see cref="Tenants"/> enrolls tenants: others are refused.
    /// Keys are GUIDs, which Linux app setting names cannot hold; use <see cref="Tenants"/> in Azure.</summary>
    public Dictionary<string, string> TenantMap { get; set; } = new();
    /// <summary>ADR tenant id -> workspaces the tenant may use. "*" allows any. Workspace membership by group is a later-phase feature.
    /// For an Entra tenant whose ADR tenant id is its GUID, use <see cref="Tenants"/> (Linux app setting names cannot hold a GUID).</summary>
    public Dictionary<string, string[]> TenantWorkspaces { get; set; } = new();
    /// <summary>Enrolled Entra tenants in a form Linux app settings can express: Auth__Tenants__0__Id=&lt;tid&gt;, optional
    /// Auth__Tenants__0__MapTo and Auth__Tenants__0__Workspaces__0. Merged with the dictionaries by <see cref="TenantDirectory"/>.</summary>
    public List<TenantEnrollment> Tenants { get; set; } = new();
    public string DefaultWorkspaceId { get; set; } = "default";
    /// <summary>Role granted to an Entra principal that carries no mapped app role. "None" (default) grants nothing, so every caller
    /// needs an assigned app role (least privilege); set "Reader" to let any authenticated caller in the tenant read.</summary>
    public string DefaultRoleWhenNoAppRoles { get; set; } = "None";
    /// <summary>In-process validation of the bearer token (ADR-0008). Off by default; see <see cref="EntraTokenOptions"/>.</summary>
    public EntraTokenOptions Entra { get; set; } = new();
}

/// <summary>
/// Validates the <c>Authorization: Bearer</c> token inside the app, in addition to App Service Authentication at the edge
/// (ADR-0008, docs/11-entra-sign-in.md). Settings are <c>Auth__Entra__*</c>. Everything is off by default, so local runs and the
/// FunctionKey deployment behave exactly as before. When <see cref="ValidateBearerToken"/> is true the principal is built only from
/// the verified token and <c>X-MS-CLIENT-PRINCIPAL</c> is ignored. Arrays are bound from <c>__0</c>, <c>__1</c> settings, so their
/// defaults stay empty (configuration arrays append to defaults); the effective defaults are applied in code.
/// </summary>
public sealed class EntraTokenOptions
{
    public const string DefaultAuthorityHost = "login.microsoftonline.com";
    /// <summary>The delegated scope of the server app (a scope value, not an app role value: Entra keeps both in one collection).</summary>
    public const string DefaultScope = "user_impersonation";
    public const int DefaultClockSkewSeconds = 60;
    public const int DefaultJwksCacheMinutes = 60;

    /// <summary>Master switch. Requires <c>Auth:Mode=EntraBuiltIn</c>.</summary>
    public bool ValidateBearerToken { get; set; }
    /// <summary>Entra tenant (directory) ID whose tokens are accepted (GUID). Single tenant.</summary>
    public string TenantId { get; set; } = "";
    /// <summary>Client ID (GUID) of the server app registration; the <c>aud</c> of a v2 access token.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Extra accepted <c>aud</c> values besides <see cref="ClientId"/> (for example <c>api://client-id</c> for a v1 app). Empty by default.</summary>
    public string[] Audiences { get; set; } = Array.Empty<string>();
    /// <summary>Client application IDs (GUIDs) allowed as the token's <c>azp</c>; the same list given to App Service Authentication.</summary>
    public string[] AllowedClientIds { get; set; } = Array.Empty<string>();
    /// <summary>Scope values (short names) of which a delegated token must carry one. Empty means <see cref="DefaultScope"/>.</summary>
    public string[] RequiredScopes { get; set; } = Array.Empty<string>();
    /// <summary>Accept tokens that have no <c>scp</c> claim (client-credentials callers, such as the deploy pipeline) when they carry an app role. Off: delegated user tokens only. An application is Reader at most, whatever its roles map to (<see cref="EntraRoleMapper"/>).</summary>
    public bool AllowApplicationTokens { get; set; }
    /// <summary>Sign-in host name only; builds the issuer and the key address. Sovereign clouds use their own host.</summary>
    public string AuthorityHost { get; set; } = DefaultAuthorityHost;
    /// <summary>Optional key set address instead of the built one. https only, except a loopback address for local tests.</summary>
    public string JwksUrl { get; set; } = "";
    /// <summary>Allowed difference in seconds when checking <c>exp</c> and <c>nbf</c> (0 to 300).</summary>
    public int ClockSkewSeconds { get; set; } = DefaultClockSkewSeconds;
    /// <summary>How long fetched signing keys are used (5 to 1440). Once they expire and cannot be refreshed, calls get 503, never "accept".</summary>
    public int JwksCacheMinutes { get; set; } = DefaultJwksCacheMinutes;
    /// <summary>Canonical MCP URL (RFC 8707 resource). Empty means https://&lt;WEBSITE_HOSTNAME&gt;/runtime/webhooks/mcp, using the host name the platform injects.</summary>
    public string ResourceUri { get; set; } = "";
}

/// <summary>One enrolled Entra tenant. See <see cref="AuthOptions.Tenants"/>.</summary>
public sealed class TenantEnrollment
{
    /// <summary>Entra tenant ID (the tid claim).</summary>
    public string Id { get; set; } = "";
    /// <summary>ADR tenant ID to store this tenant's data under; empty means the tid itself.</summary>
    public string? MapTo { get; set; }
    /// <summary>Workspaces the tenant may use ("*" allows any); empty means <see cref="AuthOptions.DefaultWorkspaceId"/>.</summary>
    public string[] Workspaces { get; set; } = Array.Empty<string>();
}
