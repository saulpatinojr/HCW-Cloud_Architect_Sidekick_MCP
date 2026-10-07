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
    /// <summary>"EntraBuiltIn" (trust X-MS-CLIENT-PRINCIPAL injected by App Service Authentication) or "FunctionKey" (single-tenant, static binding).</summary>
    public string Mode { get; set; } = "FunctionKey";
    public string StaticTenantId { get; set; } = "local-tenant";
    public string StaticWorkspaceId { get; set; } = "default";
    public string StaticPrincipalId { get; set; } = "function-key-client";
    /// <summary>Roles for the static principal. Empty means Reader only (least privilege). NOTE: configuration arrays append to defaults, so keep defaults empty.</summary>
    public string[] StaticRoles { get; set; } = Array.Empty<string>();
    /// <summary>Entra app role value -> ADR role. Defaults map Adr.Reader/Adr.Author/Adr.Approver/Adr.Admin.</summary>
    public Dictionary<string, string> AppRoleMap { get; set; } = new()
    {
        ["Adr.Reader"] = "Reader", ["Adr.Author"] = "Author", ["Adr.Approver"] = "Approver", ["Adr.Admin"] = "Admin"
    };
    /// <summary>Entra tenant id (tid claim) -> ADR tenant id. Empty map means use tid directly.</summary>
    public Dictionary<string, string> TenantMap { get; set; } = new();
    /// <summary>ADR tenant id -> workspaces the tenant may use. "*" allows any. Workspace membership by group is a later-phase feature.</summary>
    public Dictionary<string, string[]> TenantWorkspaces { get; set; } = new();
    public string DefaultWorkspaceId { get; set; } = "default";
    public string DefaultRoleWhenNoAppRoles { get; set; } = "Reader";
}
