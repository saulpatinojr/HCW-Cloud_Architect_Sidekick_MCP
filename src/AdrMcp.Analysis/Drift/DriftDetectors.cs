using System.Text.RegularExpressions;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;
using AdrMcp.Domain.Findings;

namespace AdrMcp.Analysis.Drift;

internal static class DriftHelpers
{
    public static readonly string[] ManifestNames = { "package.json", "requirements.txt", "pyproject.toml", "go.mod", "gemfile", "pom.xml", "build.gradle", "build.gradle.kts", "cargo.toml", "composer.json", "directory.packages.props" };
    public static bool IsManifest(string path) { var f = path.Replace('\\', '/').Split('/').Last().ToLowerInvariant(); return f.EndsWith(".csproj") || f.EndsWith(".fsproj") || f.EndsWith(".vbproj") || ManifestNames.Contains(f) || f.StartsWith("requirements"); }
    public static bool IsSource(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".go", StringComparison.OrdinalIgnoreCase);
    public static bool IsIac(string path) => path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tf", StringComparison.OrdinalIgnoreCase);
    public static bool IsConfig(string path) { var f = path.Replace('\\', '/').Split('/').Last().ToLowerInvariant(); return f.StartsWith("appsettings") || f.EndsWith(".env.sample") || f.EndsWith(".yaml") || f.EndsWith(".yml") || f.EndsWith(".json") || f.EndsWith(".bicep") || f.EndsWith(".tf") || f.EndsWith(".toml") || f.EndsWith(".properties") || f.EndsWith(".config"); }

    public static IEnumerable<(int Line, string Text)> MatchingLines(string text, Regex rx)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) if (rx.IsMatch(lines[i])) yield return (i + 1, lines[i].Trim());
    }

    public static string Excerpt(string line) => line.Length <= 200 ? line : line[..200] + "…";

    /// <summary>Builds a word-boundary regex for a technology name and its common aliases (dots, hyphens, scoped packages).</summary>
    public static Regex TechnologyRegex(string subject)
    {
        var n = subject.Trim().ToLowerInvariant();
        var variants = new HashSet<string> { n };
        variants.Add(n.Replace(".", "")); variants.Add(n.Replace("-", "")); variants.Add(n.Replace(" ", "")); variants.Add(n.Replace(" ", "."));
        if (n.Contains(' ')) variants.Add(n.Split(' ')[0]);
        var alts = variants.Where(v => v.Length >= 3).Select(Regex.Escape).OrderByDescending(v => v.Length);
        return new Regex($@"(?<![A-Za-z0-9])(?:{string.Join("|", alts)})(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>Detector 1: a prohibited dependency/package appears in manifests or source imports.</summary>
public sealed class ProhibitedDependencyDriftDetector : IDriftDetector
{
    public string Name => "prohibited-dependency"; public string Version => "1.0.0";
    public bool Supports(DecisionClaim claim) => claim.Kind == ClaimKind.ProhibitedTechnology;

    public DriftEvaluation Evaluate(DecisionClaim claim, EvidenceSet evidence)
    {
        var rx = DriftHelpers.TechnologyRegex(claim.Subject);
        var manifests = evidence.Where(DriftHelpers.IsManifest).ToList();
        var sources = evidence.Where(DriftHelpers.IsSource).ToList();
        var hits = new List<EvidenceItem>();
        foreach (var f in manifests.Concat(sources))
        {
            var importOnly = DriftHelpers.IsSource(f.Path);
            foreach (var (line, text) in DriftHelpers.MatchingLines(f.Text, rx))
            {
                if (importOnly && !Regex.IsMatch(text, @"^\s*(using|import|require|from|#include|@import)\b")) continue;
                hits.Add(new EvidenceItem(f.EvidenceId, f.Path, DriftHelpers.Excerpt(text), f.Sha256, line, importOnly ? "import" : "manifest"));
                if (hits.Count >= 20) break;
            }
        }
        if (hits.Count > 0)
            return new DriftEvaluation(DriftClaimResult.Drifted, Confidence.Observed(0.9, "Exact token match in dependency manifests/imports."), $"'{claim.Subject}' is prohibited but referenced in {hits.Select(h => h.SourcePath).Distinct().Count()} file(s).", hits);
        if (manifests.Count == 0 && sources.Count == 0)
            return new DriftEvaluation(DriftClaimResult.Uncertain, Confidence.Uncertain("No dependency manifests or source files in evidence."), "No manifests or source files are available to verify the prohibition.", Array.Empty<EvidenceItem>());
        return new DriftEvaluation(DriftClaimResult.Compliant, Confidence.Observed(0.8, $"No reference in {manifests.Count} manifest(s) and {sources.Count} source file(s)."), $"No reference to '{claim.Subject}' found.", manifests.Take(3).Select(m => new EvidenceItem(m.EvidenceId, m.Path, null, m.Sha256, null, "manifest-scanned")).ToList());
    }
}

/// <summary>Detector 2: infrastructure requirements expressed over Bicep/Terraform resources.</summary>
public sealed class InfrastructureRequirementDriftDetector : IDriftDetector
{
    public string Name => "infrastructure-requirement"; public string Version => "1.0.0";
    public bool Supports(DecisionClaim claim) => claim.Kind == ClaimKind.InfrastructureRequirement && Map(claim) is not null;

    private sealed record Check(string[] BicepTypes, string[] TerraformTypes, Regex BicepSatisfied, Regex TerraformSatisfied, string Description, bool WorkspaceLevel = false);

    private static Check? Map(DecisionClaim claim)
    {
        var res = (claim.Subject ?? "").ToLowerInvariant();
        var req = (claim.Object ?? "").ToLowerInvariant();
        string[] bicepTypes, tfTypes;
        if (res.Contains("storage")) { bicepTypes = new[] { "microsoft.storage/storageaccounts" }; tfTypes = new[] { "azurerm_storage_account" }; }
        else if (res.Contains("cosmos") || res.Contains("database")) { bicepTypes = new[] { "microsoft.documentdb/databaseaccounts", "microsoft.sql/servers", "microsoft.dbforpostgresql/flexibleservers" }; tfTypes = new[] { "azurerm_cosmosdb_account", "azurerm_mssql_server", "azurerm_postgresql_flexible_server" }; }
        else if (res.Contains("key vault")) { bicepTypes = new[] { "microsoft.keyvault/vaults" }; tfTypes = new[] { "azurerm_key_vault" }; }
        else if (res.Contains("function") || res.Contains("app service") || res.Contains("web app")) { bicepTypes = new[] { "microsoft.web/sites" }; tfTypes = new[] { "azurerm_linux_function_app", "azurerm_windows_function_app", "azurerm_linux_web_app", "azurerm_function_app" }; }
        else if (res.Contains("service bus")) { bicepTypes = new[] { "microsoft.servicebus/namespaces" }; tfTypes = new[] { "azurerm_servicebus_namespace" }; }
        else if (res.Contains("resource")) { bicepTypes = new[] { "*" }; tfTypes = new[] { "*" }; }
        else return null;

        Regex B(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (req.Contains("private endpoint") || req.Contains("no public") || req.Contains("public network access disabled"))
            return new Check(bicepTypes, tfTypes, B(@"publicNetworkAccess\s*:\s*'Disabled'"), B(@"public_network_access_enabled\s*=\s*false"), "public network access disabled (private endpoint)", WorkspaceLevel: true);
        if (req.Contains("tls 1.2") || req.Contains("tls1.2") || req.Contains("minimum tls"))
            return new Check(bicepTypes, tfTypes, B(@"minimumTlsVersion\s*:\s*'TLS1_2'|minTlsVersion\s*:\s*'1\.2'|minimalTlsVersion\s*:\s*'1\.2'"), B(@"min_tls_version\s*=\s*""(?:TLS1_2|1\.2)"""), "minimum TLS 1.2");
        if (req.Contains("https only"))
            return new Check(bicepTypes, tfTypes, B(@"supportsHttpsTrafficOnly\s*:\s*true|httpsOnly\s*:\s*true"), B(@"(?:enable_https_traffic_only|https_traffic_only_enabled|https_only)\s*=\s*true"), "HTTPS only");
        if (req.Contains("shared key"))
            return new Check(bicepTypes, tfTypes, B(@"allowSharedKeyAccess\s*:\s*false|disableLocalAuth\s*:\s*true"), B(@"shared_access_key_enabled\s*=\s*false|local_authentication_disabled\s*=\s*true"), "shared key / local auth disabled");
        if (req.Contains("managed identit"))
            return new Check(bicepTypes, tfTypes, B(@"identity\s*:\s*\{[^}]*type\s*:\s*'(?:SystemAssigned|UserAssigned|SystemAssigned,\s*UserAssigned)'"), B(@"identity\s*\{[^}]*type\s*=\s*""(?:SystemAssigned|UserAssigned|SystemAssigned,\s*UserAssigned)"""), "managed identity assigned");
        if (req.Contains("zone") && req.Contains("redundan"))
            return new Check(bicepTypes, tfTypes, B(@"name\s*:\s*'[A-Za-z_]*ZRS'|zoneRedundant\s*:\s*true|isZoneRedundant\s*:\s*true"), B(@"account_replication_type\s*=\s*""[A-Z]*ZRS""|zone_redundant\s*=\s*true|zone_redundancy_enabled\s*=\s*true"), "zone redundancy");
        if (req.Contains("geo") && req.Contains("redundan"))
            return new Check(bicepTypes, tfTypes, B(@"name\s*:\s*'[A-Za-z_]*(?:GRS|GZRS|RAGRS|RAGZRS)'|enableMultipleWriteLocations\s*:\s*true|locations\s*:\s*\[[^\]]*locationName[^\]]*locationName"), B(@"account_replication_type\s*=\s*""(?:GRS|GZRS|RAGRS|RAGZRS)"""), "geo redundancy");
        if (req.Contains("soft delete"))
            return new Check(bicepTypes, tfTypes, B(@"deleteRetentionPolicy\s*:\s*\{[^}]*enabled\s*:\s*true|enableSoftDelete\s*:\s*true"), B(@"delete_retention_policy\s*\{|soft_delete_retention_days\s*=\s*\d+"), "soft delete");
        if (req.Contains("purge protection"))
            return new Check(bicepTypes, tfTypes, B(@"enablePurgeProtection\s*:\s*true"), B(@"purge_protection_enabled\s*=\s*true"), "purge protection");
        if (req.Contains("customer-managed") || req.Contains("customer managed") || req == "cmk")
            return new Check(bicepTypes, tfTypes, B(@"keySource\s*:\s*'Microsoft\.Keyvault'|keyVaultKeyUri"), B(@"customer_managed_key\s*\{|key_vault_key_id"), "customer-managed keys");
        if (req.Contains("diagnostic"))
            return new Check(bicepTypes, tfTypes, B(@"Microsoft\.Insights/diagnosticSettings@"), B(@"azurerm_monitor_diagnostic_setting"), "diagnostic settings", WorkspaceLevel: true);
        if (req.Contains("rbac"))
            return new Check(bicepTypes, tfTypes, B(@"enableRbacAuthorization\s*:\s*true"), B(@"(?:enable_rbac_authorization|rbac_authorization_enabled)\s*=\s*true"), "RBAC authorization");
        if (req.Contains("infrastructure encryption"))
            return new Check(bicepTypes, tfTypes, B(@"requireInfrastructureEncryption\s*:\s*true"), B(@"infrastructure_encryption_enabled\s*=\s*true"), "infrastructure encryption");
        if (req.Contains("tag"))
            return new Check(bicepTypes, tfTypes, B(@"\btags\s*:\s*"), B(@"\btags\s*=\s*\{"), "tags present");
        return null;
    }

    public DriftEvaluation Evaluate(DecisionClaim claim, EvidenceSet evidence)
    {
        var check = Map(claim)!;
        var iac = evidence.Where(DriftHelpers.IsIac).ToList();
        if (iac.Count == 0) return new DriftEvaluation(DriftClaimResult.Uncertain, Confidence.Uncertain("No Bicep/Terraform in evidence."), "No infrastructure-as-code files are available.", Array.Empty<EvidenceItem>());

        var compliant = new List<EvidenceItem>(); var violations = new List<EvidenceItem>();
        foreach (var f in iac)
        {
            var isBicep = f.Path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase);
            foreach (var block in isBicep ? BicepBlocks(f.Text) : TerraformBlocks(f.Text))
            {
                var types = isBicep ? check.BicepTypes : check.TerraformTypes;
                if (!(types.Contains("*") || types.Any(t => block.Type.Equals(t, StringComparison.OrdinalIgnoreCase)))) continue;
                var satisfied = (isBicep ? check.BicepSatisfied : check.TerraformSatisfied).IsMatch(block.Body) || (check.WorkspaceLevel && (isBicep ? check.BicepSatisfied : check.TerraformSatisfied).IsMatch(f.Text));
                var item = new EvidenceItem(f.EvidenceId, f.Path, DriftHelpers.Excerpt(block.Header), f.Sha256, block.Line, satisfied ? "compliant-resource" : "violating-resource");
                (satisfied ? compliant : violations).Add(item);
            }
        }
        if (violations.Count > 0)
            return new DriftEvaluation(DriftClaimResult.Drifted, Confidence.Observed(0.85, "Resource declaration lacks the required property."), $"{violations.Count} resource(s) do not satisfy '{check.Description}'; {compliant.Count} do.", violations.Take(10).Concat(compliant.Take(3)).ToList());
        if (compliant.Count > 0)
            return new DriftEvaluation(DriftClaimResult.Compliant, Confidence.Observed(0.85, "All matching resources satisfy the requirement."), $"All {compliant.Count} matching resource(s) satisfy '{check.Description}'.", compliant.Take(10).ToList());
        return new DriftEvaluation(DriftClaimResult.NotApplicable, Confidence.Observed(0.7, "No resources of the targeted type in evidence."), $"No {string.Join("/", check.BicepTypes)} resources found in {iac.Count} IaC file(s).", Array.Empty<EvidenceItem>());
    }

    internal sealed record Block(string Type, string Header, string Body, int Line);

    internal static IEnumerable<Block> BicepBlocks(string text)
    {
        var rx = new Regex(@"^\s*resource\s+\w+\s+'(?<type>[A-Za-z.]+/[A-Za-z./]+)@[^']+'\s*(?:=\s*)?(?:if\s*\([^)]*\)\s*)?\{", RegexOptions.Multiline);
        foreach (Match m in rx.Matches(text))
        {
            var start = m.Index + m.Length - 1;
            var end = MatchBrace(text, start);
            yield return new Block(m.Groups["type"].Value, m.Value.Trim(), text[start..Math.Min(text.Length, end + 1)], text[..m.Index].Count(c => c == '\n') + 1);
        }
    }

    internal static IEnumerable<Block> TerraformBlocks(string text)
    {
        var rx = new Regex(@"^\s*resource\s+""(?<type>[a-z0-9_]+)""\s+""[^""]+""\s*\{", RegexOptions.Multiline);
        foreach (Match m in rx.Matches(text))
        {
            var start = m.Index + m.Length - 1;
            var end = MatchBrace(text, start);
            yield return new Block(m.Groups["type"].Value, m.Value.Trim(), text[start..Math.Min(text.Length, end + 1)], text[..m.Index].Count(c => c == '\n') + 1);
        }
    }

    private static int MatchBrace(string text, int openIndex)
    {
        int depth = 0; bool inStr = false; char strCh = '\0';
        for (int i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (inStr) { if (c == strCh && text[i - 1] != '\\') inStr = false; continue; }
            if (c is '\'' or '"') { inStr = true; strCh = c; continue; }
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i; }
        }
        return text.Length - 1;
    }
}

/// <summary>Detector 3: service-to-database ownership. Another service referencing the database in its configuration is drift.</summary>
public sealed class DatabaseOwnershipDriftDetector : IDriftDetector
{
    public string Name => "database-ownership"; public string Version => "1.0.0";
    public bool Supports(DecisionClaim claim) => claim.Kind == ClaimKind.DatabaseOwnership && !string.IsNullOrWhiteSpace(claim.Object);

    public DriftEvaluation Evaluate(DecisionClaim claim, EvidenceSet evidence)
    {
        var db = claim.Object!.Trim();
        var owner = Normalize(claim.Subject);
        var rx = new Regex($@"(?i)(?:Database|Initial Catalog|dbname|DATABASE|database|containerId|databaseId|db)\s*[=:]\s*[""']?{Regex.Escape(db)}\b|/{Regex.Escape(db)}(?:\?|\b)|\b{Regex.Escape(db)}(?:Db|DB|Database)?\b.{{0,40}}(?:connection|ConnectionString|CONNECTION)", RegexOptions.CultureInvariant);
        var configs = evidence.Where(p => DriftHelpers.IsConfig(p) || DriftHelpers.IsSource(p)).ToList();
        if (configs.Count == 0) return new DriftEvaluation(DriftClaimResult.Uncertain, Confidence.Uncertain("No configuration or source files."), "No configuration files available.", Array.Empty<EvidenceItem>());

        var ownerRefs = new List<EvidenceItem>(); var foreignRefs = new List<EvidenceItem>(); var unknownRefs = new List<EvidenceItem>();
        foreach (var f in configs)
        {
            var service = ServiceFromPath(f.Path);
            foreach (var (line, text) in DriftHelpers.MatchingLines(f.Text, rx))
            {
                var item = new EvidenceItem(f.EvidenceId, f.Path, DriftHelpers.Excerpt(text), f.Sha256, line, service is null ? "unattributed" : service.Equals(owner, StringComparison.OrdinalIgnoreCase) || Normalize(service).Contains(owner) || owner.Contains(Normalize(service)) ? "owner" : "foreign");
                (item.Kind == "owner" ? ownerRefs : item.Kind == "foreign" ? foreignRefs : unknownRefs).Add(item);
            }
        }
        if (foreignRefs.Count > 0)
            return new DriftEvaluation(DriftClaimResult.Drifted, Confidence.Observed(0.8, "Database referenced from a path attributed to another service."), $"Database '{db}' is referenced by {foreignRefs.Select(r => ServiceFromPath(r.SourcePath)).Distinct().Count()} service(s) other than '{claim.Subject}'.", foreignRefs.Take(10).Concat(ownerRefs.Take(2)).ToList());
        if (ownerRefs.Count > 0 && unknownRefs.Count == 0)
            return new DriftEvaluation(DriftClaimResult.Compliant, Confidence.Observed(0.8, "Only the owning service references the database."), $"Only '{claim.Subject}' references database '{db}'.", ownerRefs.Take(5).ToList());
        if (unknownRefs.Count > 0)
            return new DriftEvaluation(DriftClaimResult.Uncertain, Confidence.Uncertain("References exist in files whose owning service cannot be determined from the path."), $"Database '{db}' is referenced in {unknownRefs.Count} file(s) without a recognisable service directory (src/<Service>/, services/<Service>/, apps/<Service>/).", unknownRefs.Take(5).ToList());
        return new DriftEvaluation(DriftClaimResult.Uncertain, Confidence.Uncertain("No references to the database found."), $"No configuration references database '{db}'.", Array.Empty<EvidenceItem>());
    }

    public static string? ServiceFromPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].ToLowerInvariant() is "src" or "services" or "apps" or "service" or "microservices") return parts[i + 1];
        return parts.Length >= 2 ? parts[0] : null;
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "").Replace("service", "").Replace("svc", "").Replace("api", "");
}
