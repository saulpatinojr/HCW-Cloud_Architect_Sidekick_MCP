using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdrMcp.UnitTests.Infrastructure;

/// <summary>
/// Guards the Azure naming convention of ADR-0009 (docs/adr/ADR-0009-azure-naming-convention.md).
///
/// What this PROVES: the real infra/main.bicep, evaluated offline by <c>bicep snapshot</c> for every region in infra/region-codes.json,
/// produces names that (a) follow <c>&lt;abbreviation&gt;-&lt;workload&gt;[-&lt;component&gt;]-&lt;environment&gt;-&lt;region code&gt;-&lt;nn&gt;</c>, (b) obey
/// the length and character rules of each resource type (Microsoft Learn, "Naming rules and restrictions for Azure resources"),
/// (c) carry the documented tags, and (d) stop the deployment with a readable message when a name part is wrong.
///
/// What this does NOT prove: that a global name (function app, Cosmos DB account, Key Vault, storage account) is free in Azure, that an
/// Azure Policy in your tenant accepts the names or tags, that the region supports Flex Consumption, or that a deployment succeeds.
/// Only a real <c>azd provision --preview</c> (what-if) or a deployment answers those.
///
/// The Bicep tests need the Bicep CLI (<c>az bicep install</c>). They are skipped without it, unless ADR_TEST_REQUIRE_BICEP=true, which CI
/// sets: then a missing CLI fails the run, so the check can never pass by being skipped.
/// </summary>
public sealed class ResourceNamingTests : IClassFixture<BicepSnapshotFixture>
{
    // Abbreviations exactly as published at
    // https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations (read 2026-10-26).
    // Note: the page lists "cosmos" for a Cosmos DB database and "cosno" for a Cosmos DB for NoSQL account, and "asp" for an App Service plan.
    private static readonly Dictionary<string, string> PublishedAbbreviations = new()
    {
        ["resourcesResourceGroups"] = "rg",
        ["managedIdentityUserAssignedIdentities"] = "id",
        ["operationalInsightsWorkspaces"] = "log",
        ["insightsComponents"] = "appi",
        ["storageStorageAccounts"] = "st",
        ["documentDBDatabaseAccounts"] = "cosno",
        ["keyVaultVaults"] = "kv",
        ["webSitesFunctions"] = "func",
        ["webServerFarms"] = "asp",
        ["networkVirtualNetworks"] = "vnet",
        ["networkVirtualNetworksSubnets"] = "snet",
        ["networkPrivateEndpoints"] = "pep",
    };

    private static readonly string Root = FindRoot();
    private readonly BicepSnapshotFixture _bicep;

    public ResourceNamingTests(BicepSnapshotFixture bicep) => _bicep = bicep;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "AdrMcp.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AdrMcp.sln not found above the test assembly.");
    }

    private static JsonNode Json(string relative) => JsonNode.Parse(File.ReadAllText(Path.Join(Root, relative)))!;

    public static IEnumerable<object[]> Regions()
        => Json("infra/region-codes.json").AsObject().Select(p => new object[] { p.Key, p.Value!.GetValue<string>() });

    // ---- Checks that need no Bicep CLI -------------------------------------------------------------------------------------

    [Fact]
    public void Abbreviations_are_the_ones_Microsoft_publishes_and_nothing_else_is_defined()
    {
        var actual = Json("infra/abbreviations.json").AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
        Assert.Equal(PublishedAbbreviations.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
    }

    [Fact]
    public void Region_codes_are_short_lower_case_and_unique()
    {
        var map = Json("infra/region-codes.json").AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
        Assert.NotEmpty(map);
        Assert.All(map, p =>
        {
            Assert.Matches("^[a-z]+[0-9]?$", p.Key);                // the CLI name of the region, for example southcentralus
            Assert.Matches("^[a-z]{2,4}[0-9]?$", p.Value);          // the code, for example scus or wus3
            Assert.True(p.Value.Length <= 4, $"{p.Key}: the code {p.Value} is longer than 4 characters; the Key Vault name limit (24) was sized for 4.");
        });
        Assert.Equal(map.Count, map.Values.Distinct().Count());
    }

    [Fact]
    public void The_deployment_parameters_file_passes_the_naming_parts_to_the_template()
    {
        var parameters = Json("infra/main.parameters.json")["parameters"]!;
        Assert.Equal("${ADR_WORKLOAD_NAME=adr}", parameters["workloadName"]!["value"]!.GetValue<string>());
        Assert.Equal("${ADR_ENVIRONMENT_SHORT=dev}", parameters["environmentShort"]!["value"]!.GetValue<string>());
        Assert.Equal("${ADR_NAME_SUFFIX}", parameters["nameSuffix"]!["value"]!.GetValue<string>());

        var bicep = File.ReadAllText(Path.Join(Root, "infra/main.bicep"));
        Assert.Contains("param workloadName string = 'adr'", bicep);
        Assert.Contains("param environmentShort string = 'dev'", bicep);
        Assert.Contains("param nameSuffix string = ''", bicep);
    }

    [Fact]
    public void No_template_builds_a_name_from_a_hash()
    {
        // The old names ended in toLower(uniqueString(...)). One suffix parameter now solves a collision (ADR-0009), so a hash must not come back.
        var offenders = Directory.EnumerateFiles(Path.Join(Root, "infra"), "*.bicep", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("uniqueString(", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(Root, f))
            .ToList();
        Assert.Empty(offenders);
    }

    // ---- Checks that evaluate the real template with the Bicep CLI -----------------------------------------------------------

    [BicepTheory]
    [MemberData(nameof(Regions))]
    public async Task Every_name_follows_the_pattern_and_the_Azure_rules_in_every_supported_region(string region, string code)
    {
        var snapshot = await _bicep.Get($"region:{region}");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var problems = NamingChecks.Verify(snapshot.Resources, code, suffix: "");
        Assert.True(problems.Count == 0, $"{region}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [BicepFact]
    public async Task The_azd_environment_name_is_a_tag_only_and_is_not_part_of_any_name()
    {
        var snapshot = await _bicep.Get("region:southcentralus");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var names = NamingChecks.AllNames(snapshot.Resources);
        Assert.DoesNotContain(names, n => n.Contains(BicepSnapshotFixture.AzdEnvironmentName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(BicepSnapshotFixture.AzdEnvironmentName, snapshot.Resources.First(r => r.Type == "Microsoft.Resources/resourceGroups").Tags["azd-env-name"]);
    }

    [BicepFact]
    public async Task The_default_names_in_South_Central_US_are_the_documented_ones()
    {
        var snapshot = await _bicep.Get("region:southcentralus");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var byType = snapshot.Resources.Where(r => !r.Name.Contains('/') && !r.Name.StartsWith('[')).GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Select(r => r.Name).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { "rg-adr-dev-scus-01" }, byType["Microsoft.Resources/resourceGroups"]);
        Assert.Equal(new[] { "func-adr-dev-scus-01" }, byType["Microsoft.Web/sites"]);
        Assert.Equal(new[] { "asp-adr-dev-scus-01" }, byType["Microsoft.Web/serverfarms"]);
        Assert.Equal(new[] { "cosno-adr-dev-scus-01" }, byType["Microsoft.DocumentDB/databaseAccounts"]);
        Assert.Equal(new[] { "kv-adr-dev-scus-01" }, byType["Microsoft.KeyVault/vaults"]);
        Assert.Equal(new[] { "stadrdevscus01" }, byType["Microsoft.Storage/storageAccounts"]);
        Assert.Equal(new[] { "log-adr-dev-scus-01" }, byType["Microsoft.OperationalInsights/workspaces"]);
        Assert.Equal(new[] { "appi-adr-dev-scus-01" }, byType["Microsoft.Insights/components"]);
        Assert.Equal(new[] { "id-adr-dev-scus-01" }, byType["Microsoft.ManagedIdentity/userAssignedIdentities"]);
        Assert.Equal(new[] { "vnet-adr-dev-scus-01" }, byType["Microsoft.Network/virtualNetworks"]);
        Assert.Equal(new[] { "pep-adr-blob-dev-scus-01", "pep-adr-cosmos-dev-scus-01", "pep-adr-kv-dev-scus-01", "pep-adr-queue-dev-scus-01" }, byType["Microsoft.Network/privateEndpoints"]);
        Assert.Equal(new[] { "snet-adr-app-dev-scus-01", "snet-adr-pep-dev-scus-01" }, NamingChecks.SubnetNames(snapshot.Resources).OrderBy(n => n).ToArray());
    }

    [BicepFact]
    public async Task The_suffix_goes_on_the_four_global_names_and_nowhere_else()
    {
        var snapshot = await _bicep.Get("suffix");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var problems = NamingChecks.Verify(snapshot.Resources, "scus", suffix: "b");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var globalTypes = new[] { "Microsoft.Web/sites", "Microsoft.DocumentDB/databaseAccounts", "Microsoft.KeyVault/vaults", "Microsoft.Storage/storageAccounts" };
        foreach (var r in snapshot.Resources.Where(r => !r.Name.Contains('/') && !r.Name.StartsWith('[')))
        {
            var hasSuffix = r.Name.EndsWith("01b", StringComparison.Ordinal);
            Assert.True(hasSuffix == globalTypes.Contains(r.Type, StringComparer.OrdinalIgnoreCase), $"{r.Type} {r.Name}: the suffix must be on the four global names only.");
        }
    }

    [BicepFact]
    public async Task A_resource_group_override_is_used_as_given_and_changes_no_other_name()
    {
        var snapshot = await _bicep.Get("group-override");
        Assert.True(snapshot.Succeeded, snapshot.Output);
        Assert.Contains(snapshot.Resources, r => r.Type == "Microsoft.Resources/resourceGroups" && r.Name == "rg-custom");
        Assert.Contains(snapshot.Resources, r => r.Type == "Microsoft.Web/sites" && r.Name == "func-adr-dev-scus-01");
    }

    [BicepFact]
    public async Task A_region_given_with_capitals_and_spaces_gets_the_same_names()
    {
        var snapshot = await _bicep.Get("spaced-region");
        Assert.True(snapshot.Succeeded, snapshot.Output);
        Assert.Contains(snapshot.Resources, r => r.Type == "Microsoft.Web/sites" && r.Name == "func-adr-dev-scus-01");
    }

    [BicepTheory]
    [InlineData("location", "has no short code")]
    [InlineData("workloadName", "lower-case letters and digits only")]
    [InlineData("environmentShort", "lower-case letters and digits only")]
    [InlineData("nameSuffix", "lower-case letters and digits only")]
    public async Task A_name_part_that_Azure_would_reject_stops_the_deployment_before_anything_is_created(string parameter, string expectedMessage)
    {
        var snapshot = await _bicep.Get($"reject:{parameter}");

        Assert.False(snapshot.Succeeded, "The template accepted a name part that Azure rejects.");
        Assert.Contains(expectedMessage, snapshot.Output);
    }

    [BicepFact]
    public async Task Name_parts_that_make_the_Key_Vault_name_longer_than_24_characters_stop_the_deployment()
    {
        // kv-abcdefgh-dev-scus-01abcd is 27 characters. The Key Vault limit (24) is the tightest of the four global names:
        // the storage account (24) and function app (32 for the host ID) names fit whenever the vault name fits.
        var snapshot = await _bicep.Get("reject:keyvault-length");

        Assert.False(snapshot.Succeeded);
        Assert.Contains("longer than the 24 characters", snapshot.Output);
    }

    [BicepTheory]
    [InlineData("westcentralus", "wcus")]
    [InlineData("northcentralus", "ncus")]
    public async Task Longer_name_parts_that_still_fit_pass_every_limit_in_the_longest_region_codes(string region, string code)
    {
        // workloadName 6 + environmentShort 4 + code 4 + suffix 1 = 15 characters of variable text; a 24-character Key Vault name leaves room for 16.
        var snapshot = await _bicep.Get($"long:{region}");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var problems = NamingChecks.Verify(snapshot.Resources, code, suffix: "b", workload: "adrmcp", environment: "prod");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}

/// <summary>The naming rules, written once, applied to a snapshot of what the template would create.</summary>
internal static class NamingChecks
{
    private const string Learn = "https://learn.microsoft.com/azure/azure-resource-manager/management/resource-name-rules";

    // Length and character rules per resource type, from Microsoft Learn (Learn above). Pattern is the full-name rule.
    private sealed record Rule(int Min, int Max, string Pattern, string Source);

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Resources/resourceGroups"] = new(1, 90, @"^[A-Za-z0-9_().-]*[A-Za-z0-9_()-]$", "1-90; letters, digits, underscores, parentheses, hyphens, periods; cannot end with a period"),
        ["Microsoft.ManagedIdentity/userAssignedIdentities"] = new(3, 128, @"^[A-Za-z0-9][A-Za-z0-9_-]*$", "3-128; letters, digits, hyphens, underscores; start with a letter or digit"),
        ["Microsoft.OperationalInsights/workspaces"] = new(4, 63, @"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$", "4-63; letters, digits, hyphens; start and end with a letter or digit"),
        ["Microsoft.Insights/components"] = new(1, 260, @"^[^%&\\?/]*[^%&\\?/ .]$", "1-260; none of % & \\ ? /; cannot end with a space or period"),
        ["Microsoft.Network/virtualNetworks"] = new(2, 64, @"^[A-Za-z0-9]([A-Za-z0-9_.-]*[A-Za-z0-9_])$", "2-64; letters, digits, underscores, periods, hyphens; start with a letter or digit, end with a letter, digit or underscore"),
        ["Microsoft.Network/virtualNetworks/subnets"] = new(1, 80, @"^[A-Za-z0-9]([A-Za-z0-9_.-]*[A-Za-z0-9_])?$", "1-80; same characters as a virtual network"),
        ["Microsoft.Network/privateEndpoints"] = new(2, 64, @"^[A-Za-z0-9]([A-Za-z0-9_.-]*[A-Za-z0-9_])$", "2-64; same characters as a virtual network"),
        ["Microsoft.Network/privateDnsZones/virtualNetworkLinks"] = new(1, 80, @"^[A-Za-z0-9]([A-Za-z0-9_.-]*[A-Za-z0-9_])?$", "1-80; same characters as a subnet"),
        ["Microsoft.Storage/storageAccounts"] = new(3, 24, @"^[a-z0-9]+$", "3-24; lower-case letters and digits only; global"),
        ["Microsoft.DocumentDB/databaseAccounts"] = new(3, 44, @"^[a-z0-9][a-z0-9-]*$", "3-44; lower-case letters, digits, hyphens; start with a lower-case letter or digit; global"),
        ["Microsoft.KeyVault/vaults"] = new(3, 24, @"^[A-Za-z]([A-Za-z0-9]|-(?!-))*[A-Za-z0-9]$", "3-24; letters, digits, hyphens; start with a letter, end with a letter or digit; no two hyphens in a row; global"),
        ["Microsoft.Web/serverfarms"] = new(1, 60, @"^[A-Za-z0-9-]+$", "1-60; letters, digits, hyphens"),
        ["Microsoft.Web/sites"] = new(2, 32, @"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])$", "2-60; letters, digits, hyphens, not at either end; global. Kept to 32 because Azure Functions truncates the name to 32 characters for the host ID (the Learn page above)"),
    };

    // abbreviation, optional components (a name takes exactly one when the list is not empty), whether the optional suffix applies, hyphenated.
    private sealed record Shape(string Abbreviation, string[] Components, bool TakesSuffix, bool Hyphenated = true);

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Resources/resourceGroups"] = new("rg", [], false),
        ["Microsoft.ManagedIdentity/userAssignedIdentities"] = new("id", [], false),
        ["Microsoft.OperationalInsights/workspaces"] = new("log", [], false),
        ["Microsoft.Insights/components"] = new("appi", [], false),
        ["Microsoft.Network/virtualNetworks"] = new("vnet", [], false),
        ["Microsoft.Network/virtualNetworks/subnets"] = new("snet", ["app", "pep"], false),
        ["Microsoft.Network/privateEndpoints"] = new("pep", ["cosmos", "kv", "blob", "queue"], false),
        ["Microsoft.Web/serverfarms"] = new("asp", [], false),
        ["Microsoft.Web/sites"] = new("func", [], true),
        ["Microsoft.DocumentDB/databaseAccounts"] = new("cosno", [], true),
        ["Microsoft.KeyVault/vaults"] = new("kv", [], true),
        ["Microsoft.Storage/storageAccounts"] = new("st", [], true, Hyphenated: false),
    };

    // Resource types whose names the template does not build from the convention: Azure fixes them, they are children of a checked
    // resource, or they are GUIDs. A type in neither list fails the test, so a new resource type forces a decision here.
    private static readonly string[] NotNamedByConvention =
    [
        "Microsoft.Authorization/roleAssignments",
        "Microsoft.DocumentDB/databaseAccounts/",
        "Microsoft.Storage/storageAccounts/",
        "Microsoft.Web/sites/config",
        "Microsoft.Network/privateEndpoints/privateDnsZoneGroups",
        "Microsoft.Network/privateDnsZones",              // name is the fixed privatelink.* DNS name; its link is checked below
        "Microsoft.Security/defenderForStorageSettings",
    ];

    private static readonly string[] Components = ["api", "data", "security", "monitoring", "network"];
    private static readonly string[] TaggedTypes = [.. Shapes.Keys.Where(t => t != "Microsoft.Network/virtualNetworks/subnets"), "Microsoft.Network/privateDnsZones"];

    internal sealed record Resource(string Type, string Name, IReadOnlyDictionary<string, string> Tags, JsonNode Node);

    internal static IEnumerable<string> AllNames(IEnumerable<Resource> resources) => resources.Select(r => r.Name).Concat(SubnetNames(resources));

    internal static IEnumerable<string> SubnetNames(IEnumerable<Resource> resources)
        => resources.Where(r => r.Type.Equals("Microsoft.Network/virtualNetworks", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Node["properties"]?["subnets"]?.AsArray().Select(s => s!["name"]!.GetValue<string>()) ?? []);

    internal static List<string> Verify(IReadOnlyList<Resource> resources, string regionCode, string suffix, string workload = "adr", string environment = "dev")
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Check(string type, string name, string where)
        {
            if (Rules.TryGetValue(type, out var rule))
            {
                if (name.Length < rule.Min || name.Length > rule.Max) problems.Add($"{where}: '{name}' is {name.Length} characters; rule {rule.Min}-{rule.Max} ({rule.Source}).");
                else if (!Regex.IsMatch(name, rule.Pattern)) problems.Add($"{where}: '{name}' breaks the character rule ({rule.Source}). See {Learn}");
            }
            if (Shapes.TryGetValue(type, out var shape))
            {
                var component = shape.Components.Length == 0 ? "" : $"(?:{string.Join("|", shape.Components.Select(Regex.Escape))})-";
                var suffixPart = shape.TakesSuffix ? Regex.Escape(suffix) : "";
                var pattern = shape.Hyphenated
                    ? $"^{shape.Abbreviation}-{Regex.Escape(workload)}-{component}{Regex.Escape(environment)}-{Regex.Escape(regionCode)}-01{suffixPart}$"
                    : $"^{shape.Abbreviation}{Regex.Escape(workload)}{Regex.Escape(environment)}{Regex.Escape(regionCode)}01{suffixPart}$";
                if (!Regex.IsMatch(name, pattern)) problems.Add($"{where}: '{name}' does not match the pattern {pattern}");
            }
        }

        foreach (var r in resources)
        {
            seen.Add(r.Type);
            var topLevel = !r.Name.Contains('/') && !r.Name.StartsWith('[');
            var isDnsZone = r.Type.Equals("Microsoft.Network/privateDnsZones", StringComparison.OrdinalIgnoreCase);

            if (Shapes.ContainsKey(r.Type) && topLevel)
            {
                Check(r.Type, r.Name, r.Type);
            }
            else if (r.Type.Equals("Microsoft.Network/privateDnsZones/virtualNetworkLinks", StringComparison.OrdinalIgnoreCase))
            {
                // Microsoft publishes no abbreviation for the link, so it takes the name of the virtual network it links to (ADR-0009).
                var link = LastSegment(r.Name);
                Check("Microsoft.Network/privateDnsZones/virtualNetworkLinks", link, r.Type);
                Check("Microsoft.Network/virtualNetworks", link, r.Type);   // the same pattern as the virtual network
            }
            else if (!NotNamedByConvention.Any(prefix => r.Type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{r.Type} '{r.Name}': no naming rule for this resource type. Add it to Shapes and Rules in NamingChecks (and to ADR-0009), or to NotNamedByConvention if Azure fixes the name.");
            }

            if (TaggedTypes.Contains(r.Type, StringComparer.OrdinalIgnoreCase) && (topLevel || isDnsZone)) CheckTags(r, problems, workload, environment);
        }

        foreach (var subnet in SubnetNames(resources)) Check("Microsoft.Network/virtualNetworks/subnets", subnet, "subnet");

        foreach (var needed in new[] { "Microsoft.Resources/resourceGroups", "Microsoft.Web/sites", "Microsoft.KeyVault/vaults", "Microsoft.Storage/storageAccounts", "Microsoft.DocumentDB/databaseAccounts", "Microsoft.Network/privateEndpoints" })
            if (!seen.Contains(needed)) problems.Add($"The snapshot has no {needed}; the check did not see what it should.");

        return problems;
    }

    private static void CheckTags(Resource r, List<string> problems, string workload, string environment)
    {
        var expected = new Dictionary<string, string> { ["workload"] = workload, ["application"] = "adr-mcp", ["environment"] = environment };
        foreach (var (key, value) in expected)
            if (!r.Tags.TryGetValue(key, out var actual) || actual != value) problems.Add($"{r.Type} '{r.Name}': tag {key} should be '{value}', found '{actual ?? "(missing)"}'.");
        if (!r.Tags.ContainsKey("azd-env-name")) problems.Add($"{r.Type} '{r.Name}': tag azd-env-name is missing (azd needs it).");

        var isGroup = r.Type.Equals("Microsoft.Resources/resourceGroups", StringComparison.OrdinalIgnoreCase);
        if (isGroup && r.Tags.ContainsKey("component")) problems.Add($"{r.Type} '{r.Name}': the resource group holds every component, so it has no component tag.");
        if (!isGroup && (!r.Tags.TryGetValue("component", out var c) || !Components.Contains(c))) problems.Add($"{r.Type} '{r.Name}': tag component must be one of {string.Join(", ", Components)}.");

        foreach (var (key, value) in r.Tags)
        {
            if (key.Length is 0 or > 512 || key.IndexOfAny(['<', '>', '%', '&', '\\', '?', '/']) >= 0) problems.Add($"{r.Type} '{r.Name}': tag name '{key}' is not allowed by Azure.");
            if (value.Length > 256) problems.Add($"{r.Type} '{r.Name}': tag value for '{key}' is longer than 256 characters.");
        }
    }

    // The last name part: "zone/link" gives "link"; an unresolved expression ending in 'vnet-x')] gives vnet-x.
    private static string LastSegment(string name)
    {
        var expression = Regex.Match(name, @"'([^']+)'\)\]$");
        return expression.Success ? expression.Groups[1].Value : name[(name.LastIndexOf('/') + 1)..];
    }
}

/// <summary>
/// Runs <c>bicep snapshot</c> on a copy of infra/ with a generated .bicepparam file per scenario. The snapshot lists the resources the template
/// would create, with their names and tags, without contacting Azure; it uses a made-up subscription id, so nothing real is read or written.
/// All scenarios start together on first use, a few at a time, because each Bicep run takes a few seconds.
/// </summary>
public sealed class BicepSnapshotFixture : IDisposable
{
    internal const string AzdEnvironmentName = "azd-test-env";   // deliberately unlike dev: it must appear in a tag and in no name
    private const string FakeSubscriptionId = "00000000-0000-0000-0000-000000000000";
    private readonly Lazy<string> _workDirectory;
    private readonly Lazy<IReadOnlyDictionary<string, Task<BicepSnapshot>>> _runs;
    private readonly SemaphoreSlim _limit = new(Math.Clamp(Environment.ProcessorCount, 2, 6));

    public BicepSnapshotFixture()
    {
        _workDirectory = new(CopyInfra);
        _runs = new(StartAll);
    }

    internal Task<BicepSnapshot> Get(string scenario) => _runs.Value[scenario];

    /// <summary>ADR_BICEP_PATH (a full path to the Bicep executable) wins when set; otherwise PATH, then ~/.azure/bin, where `az bicep install` puts it.</summary>
    internal static string? FindBicep()
    {
        var configured = Environment.GetEnvironmentVariable("ADR_BICEP_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;

        var names = OperatingSystem.IsWindows() ? new[] { "bicep.exe" } : new[] { "bicep" };
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azure", "bin"));   // where `az bicep install` puts it
        return directories.SelectMany(d => names.Select(n => Path.Join(d, n))).FirstOrDefault(File.Exists);
    }

    internal static bool Required => string.Equals(Environment.GetEnvironmentVariable("ADR_TEST_REQUIRE_BICEP"), "true", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, Task<BicepSnapshot>> StartAll()
    {
        static string Quote(string value) => $"'{value}'";
        static Dictionary<string, string> Parameters(string location, params (string Name, string Value)[] more)
        {
            var p = new Dictionary<string, string> { ["environmentName"] = Quote(AzdEnvironmentName), ["location"] = Quote(location) };
            foreach (var (name, value) in more) p[name] = value;
            return p;
        }

        var scenarios = new Dictionary<string, Dictionary<string, string>>();
        var regions = JsonNode.Parse(File.ReadAllText(Path.Join(_workDirectory.Value, "infra", "region-codes.json")))!.AsObject().Select(p => p.Key).ToList();
        foreach (var region in regions) scenarios[$"region:{region}"] = Parameters(region, ("enablePrivateNetworking", "true"));

        scenarios["suffix"] = Parameters("southcentralus", ("enablePrivateNetworking", "true"), ("nameSuffix", Quote("b")));
        scenarios["group-override"] = Parameters("southcentralus", ("resourceGroupName", Quote("rg-custom")));
        scenarios["spaced-region"] = Parameters("South Central US");
        foreach (var region in new[] { "westcentralus", "northcentralus" })   // the longest region codes (4 characters)
            scenarios[$"long:{region}"] = Parameters(region, ("enablePrivateNetworking", "true"), ("workloadName", Quote("adrmcp")), ("environmentShort", Quote("prod")), ("nameSuffix", Quote("b")));
        scenarios["reject:location"] = Parameters("brazilsouth");
        scenarios["reject:workloadName"] = Parameters("southcentralus", ("workloadName", Quote("ADR")));
        scenarios["reject:environmentShort"] = Parameters("southcentralus", ("environmentShort", Quote("de_v")));
        scenarios["reject:nameSuffix"] = Parameters("southcentralus", ("nameSuffix", Quote("b-")));
        scenarios["reject:keyvault-length"] = Parameters("southcentralus", ("workloadName", Quote("abcdefgh")), ("nameSuffix", Quote("abcd")));   // kv-abcdefgh-dev-scus-01abcd = 27 characters

        return scenarios.ToDictionary(s => s.Key, s => Task.Run(() => RunAsync(s.Value)));
    }

    private static string CopyInfra()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "AdrMcp.sln"))) root = root.Parent;
        var source = Path.Join(root!.FullName, "infra");
        var work = Directory.CreateTempSubdirectory("adr-bicep-").FullName;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Join(work, "infra", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return work;
    }

    private async Task<BicepSnapshot> RunAsync(Dictionary<string, string> parameters)
    {
        var bicep = FindBicep() ?? throw new InvalidOperationException("The Bicep CLI was not found (run `az bicep install`).");
        var work = _workDirectory.Value;
        var id = Guid.NewGuid().ToString("N");
        var paramFile = Path.Join(work, $"p-{id}.bicepparam");
        await File.WriteAllTextAsync(paramFile, "using './infra/main.bicep'" + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, parameters.Select(p => $"param {p.Key} = {p.Value}")) + Environment.NewLine);

        var location = parameters["location"].Trim('\'').Replace(" ", "").ToLowerInvariant();
        var start = new ProcessStartInfo(bicep) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "snapshot", paramFile, "--mode", "overwrite", "--subscription-id", FakeSubscriptionId, "--location", location }) start.ArgumentList.Add(a);

        await _limit.WaitAsync();
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout + await stderr;

            var snapshotFile = Path.Join(work, $"p-{id}.snapshot.json");
            if (process.ExitCode != 0 || !File.Exists(snapshotFile)) return new BicepSnapshot(false, output, []);

            var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotFile))!;
            var resources = document["predictedResources"]!.AsArray().Select(n => new NamingChecks.Resource(
                n!["type"]!.GetValue<string>(),
                n["name"]!.GetValue<string>(),
                (n["tags"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()) ?? new Dictionary<string, string>(),
                n)).ToList();
            return new BicepSnapshot(true, output, resources);
        }
        finally { _limit.Release(); }
    }

    public void Dispose()
    {
        if (_workDirectory.IsValueCreated) try { Directory.Delete(_workDirectory.Value, recursive: true); } catch (IOException) { /* temp folder; the OS cleans it */ }
    }
}

internal sealed record BicepSnapshot(bool Succeeded, string Output, IReadOnlyList<NamingChecks.Resource> Resources);

/// <summary>Like <see cref="FactAttribute"/>, but skipped when the Bicep CLI is missing, unless ADR_TEST_REQUIRE_BICEP=true (set in CI): then it runs and fails.</summary>
public sealed class BicepFactAttribute : FactAttribute
{
    public BicepFactAttribute()
    {
        if (!BicepSnapshotFixture.Required && BicepSnapshotFixture.FindBicep() is null) Skip = "The Bicep CLI is not installed (run `az bicep install`).";
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="BicepFactAttribute"/>.</summary>
public sealed class BicepTheoryAttribute : TheoryAttribute
{
    public BicepTheoryAttribute()
    {
        if (!BicepSnapshotFixture.Required && BicepSnapshotFixture.FindBicep() is null) Skip = "The Bicep CLI is not installed (run `az bicep install`).";
    }
}
