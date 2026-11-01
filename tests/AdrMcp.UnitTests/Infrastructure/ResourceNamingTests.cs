using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdrMcp.UnitTests.Infrastructure;

/// <summary>
/// Guards the Azure naming convention of ADR-0009 (docs/adr/ADR-0009-azure-naming-convention.md).
///
/// What this PROVES: the real entry templates, infra/main.bicep (the ADR server) and infra/vault/main.bicep (the vault server, ADR-0010),
/// evaluated offline by <c>bicep snapshot</c> for every region in infra/region-codes.json,
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
        ["appContainerApps"] = "ca",
        ["appManagedEnvironments"] = "cae",
        ["containerRegistryRegistries"] = "cr",
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
    public void The_vault_parameters_file_passes_the_naming_parts_and_the_azd_exists_flag_to_its_template()
    {
        // The vault server has its own azd project (src/VaultMcp/azure.yaml) with its own variable names, so the two environments never share a setting.
        var parameters = Json("infra/vault/main.parameters.json")["parameters"]!;
        Assert.Equal("${VAULT_WORKLOAD_NAME=obsidian}", parameters["workloadName"]!["value"]!.GetValue<string>());
        Assert.Equal("${VAULT_ENVIRONMENT_SHORT=dev}", parameters["environmentShort"]!["value"]!.GetValue<string>());
        Assert.Equal("${VAULT_NAME_SUFFIX}", parameters["nameSuffix"]!["value"]!.GetValue<string>());
        Assert.Equal("${SERVICE_VAULT_RESOURCE_EXISTS=false}", parameters["vaultExists"]!["value"]!.GetValue<string>());   // azd sets it; the deployed image is kept
        Assert.Equal("${VAULT_DEPLOYER_PRINCIPAL_ID}", parameters["deployerPrincipalId"]!["value"]!.GetValue<string>());   // opt-in, never azd's own AZURE_PRINCIPAL_ID

        var bicep = File.ReadAllText(Path.Join(Root, "infra/vault/main.bicep"));
        Assert.Contains("param workloadName string = 'obsidian'", bicep);
        Assert.Contains("param environmentShort string = 'dev'", bicep);
        Assert.Contains("param nameSuffix string = ''", bicep);

        var azure = File.ReadAllText(Path.Join(Root, "src/VaultMcp/azure.yaml"));
        Assert.Contains("path: ../../infra/vault", azure);
        Assert.Matches(@"(?m)^  vault:\s*$", azure);   // the service name is the azd-service-name tag value the template puts on the container app
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
    public async Task The_pipeline_identity_is_one_more_allowed_client_and_turns_application_tokens_on_only_with_the_in_app_check()
    {
        var snapshot = await _bicep.Get("entra-pipeline");
        Assert.True(snapshot.Succeeded, snapshot.Output);
        var site = snapshot.Resources.Single(r => r.Type == "Microsoft.Web/sites").Node;
        var settings = site["properties"]!["siteConfig"]!["appSettings"]!.AsArray().ToDictionary(s => s!["name"]!.GetValue<string>(), s => s!["value"]!.GetValue<string>());
        Assert.Equal("true", settings["Auth__Entra__AllowApplicationTokens"]);
        var allowed = settings.Where(kv => kv.Key.StartsWith("Auth__Entra__AllowedClientIds__", StringComparison.Ordinal)).Select(kv => kv.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "bbbbbbbb-cccc-dddd-eeee-ffffffffffff", "dddddddd-eeee-ffff-0000-111111111111" }, allowed);
        // App Service Authentication gets the same list, so the edge and the app cannot disagree.
        var auth = snapshot.Resources.Single(r => r.Type == "Microsoft.Web/sites/config" && r.Name.EndsWith("/authsettingsV2", StringComparison.Ordinal)).Node;
        var edgeAllowed = auth["properties"]!["identityProviders"]!["azureActiveDirectory"]!["validation"]!["defaultAuthorizationPolicy"]!["allowedApplications"]!.AsArray().Select(a => a!.GetValue<string>()).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        Assert.Equal(allowed, edgeAllowed);

        var refused = await _bicep.Get("reject:pipeline-without-entra");
        Assert.False(refused.Succeeded, "entraPipelineClientId without EntraBuiltIn must stop the deployment.");
        Assert.Contains("requires authMode EntraBuiltIn", refused.Output);
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

    [BicepFact]
    public async Task A_second_environment_with_its_own_short_name_gets_its_own_names()
    {
        var dev = await _bicep.Get("region:southcentralus");
        var prod = await _bicep.Get("second-environment");
        Assert.True(dev.Succeeded && prod.Succeeded, dev.Output + prod.Output);

        Assert.Contains(prod.Resources, r => r.Type == "Microsoft.Resources/resourceGroups" && r.Name == "rg-adr-prod-scus-01");
        var problems = NamingChecks.Verify(prod.Resources, "scus", suffix: "", environment: "prod");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        // Every name differs, except the privatelink.* DNS zones: Azure fixes those names, and each environment has them in its own resource group.
        static IEnumerable<string> Names(BicepSnapshot s) => s.Resources.Where(r => !r.Name.StartsWith('[') && !r.Type.StartsWith("Microsoft.Network/privateDnsZones", StringComparison.OrdinalIgnoreCase)).Select(r => r.Name);
        Assert.Empty(Names(dev).Intersect(Names(prod)));
    }

    // ---- The vault server's template, infra/vault/main.bicep (ADR-0010, workload 'obsidian') ----------------------------------------

    [BicepTheory]
    [MemberData(nameof(Regions))]
    public async Task Every_vault_name_follows_the_pattern_and_the_Azure_rules_in_every_supported_region(string region, string code)
    {
        var snapshot = await _bicep.Get($"vault:region:{region}");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var problems = NamingChecks.Verify(snapshot.Resources, code, suffix: "", workload: "obsidian", application: "vault-mcp", required: NamingChecks.VaultRequiredTypes);
        Assert.True(problems.Count == 0, $"{region}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [BicepFact]
    public async Task The_default_vault_names_in_South_Central_US_are_the_documented_ones()
    {
        var snapshot = await _bicep.Get("vault:region:southcentralus");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var byType = snapshot.Resources.Where(r => !r.Name.Contains('/') && !r.Name.StartsWith('[')).GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Select(r => r.Name).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { "rg-obsidian-dev-scus-01" }, byType["Microsoft.Resources/resourceGroups"]);
        Assert.Equal(new[] { "ca-obsidian-dev-scus-01" }, byType["Microsoft.App/containerApps"]);
        Assert.Equal(new[] { "cae-obsidian-dev-scus-01" }, byType["Microsoft.App/managedEnvironments"]);
        Assert.Equal(new[] { "crobsidiandevscus01" }, byType["Microsoft.ContainerRegistry/registries"]);
        Assert.Equal(new[] { "kv-obsidian-dev-scus-01" }, byType["Microsoft.KeyVault/vaults"]);
        Assert.Equal(new[] { "stobsidiandevscus01" }, byType["Microsoft.Storage/storageAccounts"]);
        Assert.Equal(new[] { "log-obsidian-dev-scus-01" }, byType["Microsoft.OperationalInsights/workspaces"]);
        Assert.Equal(new[] { "id-obsidian-dev-scus-01" }, byType["Microsoft.ManagedIdentity/userAssignedIdentities"]);
        Assert.DoesNotContain("Microsoft.Insights/components", byType.Keys);   // no Application Insights: the host sends nothing to it
        Assert.Contains(snapshot.Resources, r => r.Type == "Microsoft.Storage/storageAccounts/fileServices/shares" && r.Name == "stobsidiandevscus01/default/vault");
        Assert.Contains(snapshot.Resources, r => r.Type == "Microsoft.App/managedEnvironments/storages" && r.Name == "cae-obsidian-dev-scus-01/vault");
    }

    [Fact]
    public void The_environment_links_its_storages_one_at_a_time()
    {
        // A first real provision failed with ManagedEnvironmentStorageLockConflict: the environment accepts one storage change at a
        // time, and a copy loop deploys in parallel unless it is batched. The sync app's two links are batched, deployed only when
        // sync is on, and after the server app (the gap the lock needs); the server's one link is the only one in the environment module.
        var module = File.ReadAllText(Path.Join(Root, "infra", "vault", "modules", "containerapps-environment-sync-storage.bicep"));
        Assert.Matches(@"@batchSize\(1\)\s*\r?\nresource shares 'Microsoft\.App/managedEnvironments/storages@", module);
        var main = File.ReadAllText(Path.Join(Root, "infra", "vault", "main.bicep"));
        Assert.Matches(@"module syncStorage '\./modules/containerapps-environment-sync-storage\.bicep' = if \(syncEnabled\) \{\s*name: 'sync-storage'\s*scope: rg\s*dependsOn: \[ containerApp \]", main);
    }

    [BicepFact]
    public async Task The_vault_server_starts_locked_mounts_the_share_read_only_and_keeps_shared_key_access_off()
    {
        // Not a naming check, but the same snapshot shows the security posture the template promises (ADR-0010 phase 2, step 1b).
        var snapshot = await _bicep.Get("vault:region:southcentralus");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var app = snapshot.Resources.Single(r => r.Type == "Microsoft.App/containerApps" && r.Name == "ca-obsidian-dev-scus-01").Node;
        var container = app["properties"]!["template"]!["containers"]![0]!;
        var env = container["env"]?.AsArray().Select(e => e!["name"]!.GetValue<string>()) ?? [];
        Assert.DoesNotContain("Vault__Auth__Mode", env);   // unset means Locked: 401 on /mcp until VAULT_AUTH_MODE=Entra is set (step 3)
        Assert.Equal("/vault", container["volumeMounts"]![0]!["mountPath"]!.GetValue<string>());
        Assert.False(app["properties"]!["configuration"]!["ingress"]!["allowInsecure"]!.GetValue<bool>());
        Assert.Equal(8080, app["properties"]!["configuration"]!["ingress"]!["targetPort"]!.GetValue<int>());
        Assert.Equal("UserAssigned", app["identity"]!["type"]!.GetValue<string>());
        Assert.Equal("Single", app["properties"]!["configuration"]!["activeRevisionsMode"]!.GetValue<string>());

        var mounts = snapshot.Resources.Where(r => r.Type == "Microsoft.App/managedEnvironments/storages").ToDictionary(r => r.Name[(r.Name.LastIndexOf('/') + 1)..], r => r.Node["properties"]!["azureFile"]!);
        Assert.Equal(new[] { "vault" }, mounts.Keys);   // without sync, only the server's read-only link exists; no read-write definition is in the environment at all
        Assert.Equal("ReadOnly", mounts["vault"]["accessMode"]!.GetValue<string>());       // the server's mount
        Assert.All(mounts.Values, m => { Assert.Null(m["accountKey"]); Assert.NotNull(m["identity"]); });   // identity mounts by default
        Assert.Equal("vault", app["properties"]!["template"]!["volumes"]![0]!["storageName"]!.GetValue<string>());   // the server never gets the read-write one
        Assert.DoesNotContain(snapshot.Resources, r => r.Type == "Microsoft.App/containerApps" && r.Name.Contains("-sync-"));   // step 2 is off by default

        var storage = snapshot.Resources.Single(r => r.Type == "Microsoft.Storage/storageAccounts").Node["properties"]!;
        Assert.False(storage["allowSharedKeyAccess"]!.GetValue<bool>());
        Assert.True(storage["azureFilesIdentityBasedAuthentication"]!["smbOAuthSettings"]!["isSmbOAuthEnabled"]!.GetValue<bool>());
        Assert.False(storage["allowBlobPublicAccess"]!.GetValue<bool>());

        var registry = snapshot.Resources.Single(r => r.Type == "Microsoft.ContainerRegistry/registries").Node["properties"]!;
        Assert.False(registry["adminUserEnabled"]!.GetValue<bool>());

        // First provision: no image yet, so Microsoft's sample image runs on the same port under TCP probes (it has no /health).
        Assert.StartsWith("mcr.microsoft.com/", container["image"]!.GetValue<string>());
        var probes = container["probes"]!.AsArray();
        Assert.Equal(new[] { "Liveness", "Readiness", "Startup" }, probes.Select(p => p!["type"]!.GetValue<string>()).OrderBy(t => t, StringComparer.Ordinal));
        Assert.All(probes, p => Assert.Equal(8080, p!["tcpSocket"]!["port"]!.GetValue<int>()));
        Assert.All(probes, p => Assert.Null(p!["httpGet"]));
    }

    [BicepFact]
    public async Task A_later_vault_provision_keeps_the_deployed_image_and_the_key_fallback_turns_shared_key_access_on()
    {
        var snapshot = await _bicep.Get("vault:exists");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        // With vaultExists=true the image comes from the running app (an expression here, because nothing is deployed offline), never from the template.
        var container = snapshot.Resources.Single(r => r.Type == "Microsoft.App/containerApps").Node["properties"]!["template"]!["containers"]![0]!;
        var image = container["image"]!.GetValue<string>();
        Assert.StartsWith("[", image);
        Assert.Contains("containers[0].image", image);
        Assert.DoesNotContain("mcr.microsoft.com", image);

        foreach (var share in snapshot.Resources.Where(r => r.Type == "Microsoft.App/managedEnvironments/storages").Select(r => r.Node["properties"]!["azureFile"]!))
        {
            Assert.Null(share["identity"]);
            Assert.Contains("listKeys(", share["accountKey"]!.GetValue<string>());   // read inside the deployment, never an output
        }
        var storage = snapshot.Resources.Single(r => r.Type == "Microsoft.Storage/storageAccounts").Node["properties"]!;
        Assert.True(storage["allowSharedKeyAccess"]!.GetValue<bool>());
        Assert.False(storage["azureFilesIdentityBasedAuthentication"]!["smbOAuthSettings"]!["isSmbOAuthEnabled"]!.GetValue<bool>());
        Assert.DoesNotContain(snapshot.Resources, r => r.Type == "Microsoft.Authorization/roleAssignments" && r.Name.Contains("a235d3ee-5935-4cfb-8cc5-a3303ad5995e"));   // no SMB identity role without the identity mount
    }

    [BicepFact]
    public async Task The_sync_app_has_no_ingress_writes_the_shares_as_its_own_user_and_reads_secrets_from_Key_Vault_only()
    {
        // Step 2 (ADR-0010): the headless Obsidian Sync client as a second container app, deployed only when syncEnabled.
        var snapshot = await _bicep.Get("vault:sync");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var problems = NamingChecks.Verify(snapshot.Resources, "scus", suffix: "", workload: "obsidian", application: "vault-mcp", required: NamingChecks.VaultRequiredTypes);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal(new[] { "ca-obsidian-dev-scus-01", "ca-obsidian-sync-dev-scus-01" }, snapshot.Resources.Where(r => r.Type == "Microsoft.App/containerApps").Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));

        var sync = snapshot.Resources.Single(r => r.Type == "Microsoft.App/containerApps" && r.Name == "ca-obsidian-sync-dev-scus-01");
        Assert.Equal("sync", sync.Tags["azd-service-name"]);
        var configuration = sync.Node["properties"]!["configuration"]!;
        Assert.Null(configuration["ingress"]);   // nothing reaches it from outside
        var secrets = configuration["secrets"]!.AsArray();
        Assert.Equal(new[] { "obsidian-email", "obsidian-password", "obsidian-vault-password" }, secrets.Select(s => s!["name"]!.GetValue<string>()).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(secrets, s => { Assert.Null(s!["value"]); Assert.Contains("secrets/obsidian-", s["keyVaultUrl"]!.GetValue<string>()); Assert.NotNull(s["identity"]); });   // Key Vault references only

        var container = sync.Node["properties"]!["template"]!["containers"]![0]!;
        var env = container["env"]!.AsArray().ToDictionary(e => e!["name"]!.GetValue<string>(), e => e!);
        Assert.Equal("My Vault", env["OB_VAULT"]["value"]!.GetValue<string>());
        Assert.Equal("Personal,Journal", env["SYNC_EXCLUDE_SEED"]["value"]!.GetValue<string>());
        Assert.Equal("/state", env["HOME"]["value"]!.GetValue<string>());
        foreach (var secret in new[] { "OB_VAULT_PASSWORD", "OB_EMAIL", "OB_PASSWORD" }) { Assert.Null(env[secret]["value"]); Assert.StartsWith("obsidian-", env[secret]["secretRef"]!.GetValue<string>()); }

        var volumes = sync.Node["properties"]!["template"]!["volumes"]!.AsArray().ToDictionary(v => v!["name"]!.GetValue<string>(), v => v!);
        Assert.Equal("vault-rw", volumes["vault"]["storageName"]!.GetValue<string>());
        Assert.Equal("syncstate", volumes["state"]["storageName"]!.GetValue<string>());
        Assert.All(volumes.Values, v => Assert.Contains("uid=1001", v["mountOptions"]!.GetValue<string>()));   // the image's non-root user owns the mounts
        Assert.Equal(1, sync.Node["properties"]!["template"]!["scale"]!["maxReplicas"]!.GetValue<int>());   // one writer

        // The server still mounts the read-only definition in the same deployment.
        var server = snapshot.Resources.Single(r => r.Type == "Microsoft.App/containerApps" && r.Name == "ca-obsidian-dev-scus-01");
        Assert.Equal("vault", server.Node["properties"]!["template"]!["volumes"]![0]!["storageName"]!.GetValue<string>());
    }

    [BicepFact]
    public async Task Entra_mode_hands_the_vault_server_its_settings_and_the_address_it_must_name_in_its_challenges()
    {
        // ADR-0010 phase 2, step 3: the only change to the template is the settings on the server app; nothing else moves.
        var snapshot = await _bicep.Get("vault:entra");
        Assert.True(snapshot.Succeeded, snapshot.Output);
        var problems = NamingChecks.Verify(snapshot.Resources, "scus", suffix: "", workload: "obsidian", application: "vault-mcp", required: NamingChecks.VaultRequiredTypes);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var app = snapshot.Resources.Single(r => r.Type == "Microsoft.App/containerApps" && r.Name == "ca-obsidian-dev-scus-01").Node;
        var env = app["properties"]!["template"]!["containers"]![0]!["env"]!.AsArray().ToDictionary(e => e!["name"]!.GetValue<string>(), e => e!["value"]!.GetValue<string>());
        Assert.Equal("Entra", env["Vault__Auth__Mode"]);
        Assert.Equal("66666666-7777-8888-9999-aaaaaaaaaaaa", env["Vault__Auth__Entra__ClientId"]);
        Assert.Equal("bbbbbbbb-cccc-dddd-eeee-ffffffffffff,cccccccc-dddd-eeee-ffff-000000000000", env["Vault__Auth__Entra__AllowedClientIds"]);
        Assert.Equal("Vault.Reader", env["Vault__Auth__Entra__RequiredRole"]);
        Assert.Contains("tenant()", env["Vault__Auth__Entra__TenantId"]);   // the deploying tenant by default (an expression offline)
        // The canonical MCP URL is built from the app's own name and the environment's domain, known before the app exists
        // (an expression offline, because the domain is read from the environment resource).
        Assert.Matches(@"^\[format\('https://\{0\}\.\{1\}/mcp', 'ca-obsidian-dev-scus-01', .*\)\]$", env["Vault__Auth__Entra__ResourceUri"]);
        Assert.DoesNotContain(env.Keys, k => k.Contains("Secret", StringComparison.OrdinalIgnoreCase));   // ids only; no secret exists for this flow
        Assert.Empty(app["properties"]!["configuration"]!["secrets"]?.AsArray() ?? []);
    }

    [BicepFact]
    public async Task The_vault_suffix_goes_on_the_three_global_names_and_nowhere_else()
    {
        var snapshot = await _bicep.Get("vault:suffix");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var problems = NamingChecks.Verify(snapshot.Resources, "scus", suffix: "b", workload: "obsidian", application: "vault-mcp", required: NamingChecks.VaultRequiredTypes);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        var globalTypes = new[] { "Microsoft.ContainerRegistry/registries", "Microsoft.KeyVault/vaults", "Microsoft.Storage/storageAccounts" };
        foreach (var r in snapshot.Resources.Where(r => !r.Name.Contains('/') && !r.Name.StartsWith('[')))
        {
            var hasSuffix = r.Name.EndsWith("01b", StringComparison.Ordinal);
            Assert.True(hasSuffix == globalTypes.Contains(r.Type, StringComparer.OrdinalIgnoreCase), $"{r.Type} {r.Name}: the suffix must be on the three global names only.");
        }
    }

    [BicepFact]
    public async Task Longer_vault_name_parts_that_still_fit_pass_every_limit_in_the_longest_region_code()
    {
        var snapshot = await _bicep.Get("vault:long:westcentralus");
        Assert.True(snapshot.Succeeded, snapshot.Output);

        var problems = NamingChecks.Verify(snapshot.Resources, "wcus", suffix: "b", workload: "obsidian", environment: "prd", application: "vault-mcp", required: NamingChecks.VaultRequiredTypes);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Contains(snapshot.Resources, r => r.Type == "Microsoft.KeyVault/vaults" && r.Name.Length == 24);
    }

    [BicepTheory]
    [InlineData("location", "has no short code")]
    [InlineData("environmentName", "must end with")]
    [InlineData("keyvault-length", "longer than the 24 characters")]
    [InlineData("reserved-workload", "must start with \"obsidian\"")]
    [InlineData("custom-workload", "must start with \"obsidian\"")]
    [InlineData("sync-vault", "VAULT_SYNC_REMOTE_VAULT")]
    [InlineData("entra-client", "VAULT_ENTRA_CLIENT_ID")]
    [InlineData("entra-allowed", "VAULT_ENTRA_ALLOWED_CLIENT_IDS")]
    public async Task A_vault_name_part_that_Azure_would_reject_stops_the_deployment_before_anything_is_created(string parameter, string expectedMessage)
    {
        var snapshot = await _bicep.Get($"vault:reject:{parameter}");

        Assert.False(snapshot.Succeeded, "The vault template accepted a name part that Azure rejects.");
        Assert.Contains(expectedMessage, snapshot.Output);
    }

    [BicepFact]
    public async Task The_two_workloads_in_one_subscription_and_region_share_no_name()
    {
        var adr = await _bicep.Get("region:southcentralus");
        var vault = await _bicep.Get("vault:region:southcentralus");
        Assert.True(adr.Succeeded && vault.Succeeded, adr.Output + vault.Output);

        static IEnumerable<string> Names(BicepSnapshot s) => s.Resources.Where(r => !r.Name.StartsWith('[')).Select(r => r.Name);
        Assert.Empty(Names(adr).Intersect(Names(vault), StringComparer.OrdinalIgnoreCase));
    }

    [BicepTheory]
    [InlineData("location", "has no short code")]
    [InlineData("environmentName", "must end with")]
    [InlineData("workloadName", "lower-case letters and digits only")]
    [InlineData("environmentShort", "lower-case letters and digits only")]
    [InlineData("nameSuffix", "lower-case letters and digits only")]
    [InlineData("reserved-workload", "reserved for the Obsidian vault server")]
    [InlineData("reserved-workload-prefix", "reserved for the Obsidian vault server")]
    [InlineData("reserved-workload-prefix-of-vault", "reserved for the Obsidian vault server")]
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
        ["Microsoft.App/containerApps"] = new(2, 32, @"^[a-z]([a-z0-9-]*[a-z0-9])$", "2-32; lower-case letters, digits, hyphens; start with a letter, end with a letter or digit"),
        ["Microsoft.App/managedEnvironments"] = new(2, 32, @"^[a-z]([a-z0-9-]*[a-z0-9])$", "the Learn page lists no rule for the environment (read 2026-10-28); the container app rule (2-32) is applied as the stricter one"),
        ["Microsoft.ContainerRegistry/registries"] = new(5, 50, @"^[A-Za-z0-9]+$", "5-50; letters and digits only; global"),
    };

    // abbreviation, the components a name may carry (at most one; a name without one is fine too), whether the optional suffix applies, hyphenated.
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
        ["Microsoft.App/containerApps"] = new("ca", ["sync"], false),   // the vault server, and the headless Sync client next to it
        ["Microsoft.App/managedEnvironments"] = new("cae", [], false),
        ["Microsoft.ContainerRegistry/registries"] = new("cr", [], true, Hyphenated: false),
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
        "Microsoft.App/managedEnvironments/",            // the environment's storage definition: a data name (the share), ADR-0009 decision 8
        "Microsoft.Insights/diagnosticSettings",         // a setting on the environment, not a resource with a public name
    ];

    /// <summary>The resource types each entry template must produce; a snapshot that lacks one did not evaluate what the test expects.</summary>
    internal static readonly string[] AdrRequiredTypes = ["Microsoft.Resources/resourceGroups", "Microsoft.Web/sites", "Microsoft.KeyVault/vaults", "Microsoft.Storage/storageAccounts", "Microsoft.DocumentDB/databaseAccounts", "Microsoft.Network/privateEndpoints"];
    internal static readonly string[] VaultRequiredTypes = ["Microsoft.Resources/resourceGroups", "Microsoft.App/containerApps", "Microsoft.App/managedEnvironments", "Microsoft.ContainerRegistry/registries", "Microsoft.KeyVault/vaults", "Microsoft.Storage/storageAccounts", "Microsoft.Storage/storageAccounts/fileServices/shares"];

    private static readonly string[] Components = ["api", "data", "security", "monitoring", "network"];
    private static readonly string[] TaggedTypes = [.. Shapes.Keys.Where(t => t != "Microsoft.Network/virtualNetworks/subnets"), "Microsoft.Network/privateDnsZones"];

    internal sealed record Resource(string Type, string Name, IReadOnlyDictionary<string, string> Tags, JsonNode Node);

    internal static IEnumerable<string> AllNames(IEnumerable<Resource> resources) => resources.Select(r => r.Name).Concat(SubnetNames(resources));

    internal static IEnumerable<string> SubnetNames(IEnumerable<Resource> resources)
        => resources.Where(r => r.Type.Equals("Microsoft.Network/virtualNetworks", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Node["properties"]?["subnets"]?.AsArray().Select(s => s!["name"]!.GetValue<string>()) ?? []);

    internal static List<string> Verify(IReadOnlyList<Resource> resources, string regionCode, string suffix, string workload = "adr", string environment = "dev", string application = "adr-mcp", string[]? required = null)
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
                var component = shape.Components.Length == 0 ? "" : $"(?:(?:{string.Join("|", shape.Components.Select(Regex.Escape))})-)?";
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

            if (TaggedTypes.Contains(r.Type, StringComparer.OrdinalIgnoreCase) && (topLevel || isDnsZone)) CheckTags(r, problems, workload, environment, application);
        }

        foreach (var subnet in SubnetNames(resources)) Check("Microsoft.Network/virtualNetworks/subnets", subnet, "subnet");

        foreach (var needed in required ?? AdrRequiredTypes)
            if (!seen.Contains(needed)) problems.Add($"The snapshot has no {needed}; the check did not see what it should.");

        return problems;
    }

    private static void CheckTags(Resource r, List<string> problems, string workload, string environment, string application)
    {
        var expected = new Dictionary<string, string> { ["workload"] = workload, ["application"] = application, ["environment"] = environment };
        foreach (var (key, value) in expected)
            if (!r.Tags.TryGetValue(key, out var actual) || actual != value) problems.Add($"{r.Type} '{r.Name}': tag {key} should be '{value}', found '{actual ?? "(missing)"}'.");
        if (!r.Tags.ContainsKey("azd-env-name")) problems.Add($"{r.Type} '{r.Name}': tag azd-env-name is missing (azd needs it).");
        if (r.Type.Equals("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase) && !(r.Tags.TryGetValue("azd-service-name", out var service) && service == "api"))
            problems.Add($"{r.Type} '{r.Name}': tag azd-service-name must be 'api' (azure.yaml service name); azd deploy finds the function app by it.");
        if (r.Type.Equals("Microsoft.App/containerApps", StringComparison.OrdinalIgnoreCase))
        {
            var expectedService = r.Name.Contains("-sync-", StringComparison.Ordinal) ? "sync" : "vault";   // the two services in src/VaultMcp/azure.yaml
            if (!(r.Tags.TryGetValue("azd-service-name", out var app) && app == expectedService))
                problems.Add($"{r.Type} '{r.Name}': tag azd-service-name must be '{expectedService}' (src/VaultMcp/azure.yaml service name); azd deploy finds the container app by it.");
        }

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
/// Runs <c>bicep snapshot</c> on a copy of infra/ with a generated .bicepparam file per scenario (the ADR template by default; scenarios that
/// start with <c>vault:</c> evaluate infra/vault/main.bicep). The snapshot lists the resources the template
/// would create, with their names and tags, without contacting Azure; it uses a made-up subscription id, so nothing real is read or written.
/// All scenarios start together on first use, a few at a time, because each Bicep run takes a few seconds.
/// </summary>
public sealed class BicepSnapshotFixture : IDisposable
{
    internal const string AzdEnvironmentName = "azd-test-dev";   // ends with -dev as the template requires, but is not the pattern's "adr-dev": it must appear in a tag and in no name
    private const string FakeSubscriptionId = "00000000-0000-0000-0000-000000000000";
    private readonly Lazy<string> _workDirectory;
    private readonly Lazy<IReadOnlyDictionary<string, Task<BicepSnapshot>>> _runs;
    private const string AdrTemplate = "./infra/main.bicep";
    private const string VaultTemplate = "./infra/vault/main.bicep";
    private readonly SemaphoreSlim _limit = new(Math.Clamp(Environment.ProcessorCount, 2, 6));
    private readonly CancellationTokenSource _stop = new();

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

    // `bicep snapshot` exists from Bicep 0.41.2 (Microsoft Learn, "Bicep CLI commands").
    private static readonly Version MinimumVersion = new(0, 41, 2);
    private static readonly Lazy<string?> Unusable = new(CheckUsable);

    /// <summary>Why the tests cannot run (no CLI, or one too old for `bicep snapshot`), or null when they can.</summary>
    internal static string? UnusableReason() => Unusable.Value;

    private static string? CheckUsable()
    {
        var bicep = FindBicep();
        if (bicep is null) return "The Bicep CLI was not found (run `az bicep install`, or set ADR_BICEP_PATH).";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(bicep, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000)) { process.Kill(); return "`bicep --version` did not answer within 30 seconds."; }
            var match = Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)");
            if (match.Success && new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) < MinimumVersion)
                return $"Bicep {match.Value} is older than {MinimumVersion}, which `bicep snapshot` needs (run `az bicep upgrade`).";
            return null;
        }
        catch (System.ComponentModel.Win32Exception e) { return $"The Bicep CLI could not be started: {e.Message}"; }
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
        static Dictionary<string, string> InEnvironment(string environment, Dictionary<string, string> parameters)
        {
            parameters["environmentName"] = Quote($"azd-test-{environment}");
            return parameters;
        }

        var scenarios = new Dictionary<string, Dictionary<string, string>>();
        var regions = JsonNode.Parse(File.ReadAllText(Path.Join(_workDirectory.Value, "infra", "region-codes.json")))!.AsObject().Select(p => p.Key).ToList();
        foreach (var region in regions) scenarios[$"region:{region}"] = Parameters(region, ("enablePrivateNetworking", "true"));

        scenarios["suffix"] = Parameters("southcentralus", ("enablePrivateNetworking", "true"), ("nameSuffix", Quote("b")));
        scenarios["group-override"] = Parameters("southcentralus", ("resourceGroupName", Quote("rg-custom")));
        scenarios["spaced-region"] = Parameters("South Central US");
        foreach (var region in new[] { "westcentralus", "northcentralus" })   // the longest region codes (4 characters)
            scenarios[$"long:{region}"] = InEnvironment("prod", Parameters(region, ("enablePrivateNetworking", "true"), ("workloadName", Quote("adrmcp")), ("environmentShort", Quote("prod")), ("nameSuffix", Quote("b"))));
        scenarios["second-environment"] = InEnvironment("prod", Parameters("southcentralus", ("enablePrivateNetworking", "true"), ("environmentShort", Quote("prod"))));
        scenarios["reject:environmentName"] = InEnvironment("prod", Parameters("southcentralus"));   // azd environment ends with -prod but environmentShort stays dev
        scenarios["reject:location"] = Parameters("brazilsouth");
        scenarios["reject:workloadName"] = Parameters("southcentralus", ("workloadName", Quote("ADR")));
        scenarios["reject:environmentShort"] = Parameters("southcentralus", ("environmentShort", Quote("de_v")));
        scenarios["reject:nameSuffix"] = Parameters("southcentralus", ("nameSuffix", Quote("b-")));
        scenarios["reject:keyvault-length"] = Parameters("southcentralus", ("workloadName", Quote("abcdefgh")), ("nameSuffix", Quote("abcd")));   // kv-abcdefgh-dev-scus-01abcd = 27 characters
        scenarios["reject:reserved-workload"] = Parameters("southcentralus", ("workloadName", Quote("obsidian")));   // the vault server's workload
        scenarios["entra-pipeline"] = Parameters("southcentralus", ("authMode", Quote("EntraBuiltIn")), ("entraClientId", Quote("66666666-7777-8888-9999-aaaaaaaaaaaa")), ("entraAllowedClientIds", Quote("bbbbbbbb-cccc-dddd-eeee-ffffffffffff")), ("entraValidateInApp", "true"), ("entraPipelineClientId", Quote("dddddddd-eeee-ffff-0000-111111111111")));   // the deploy identity may read (made-up ids)
        scenarios["reject:pipeline-without-entra"] = Parameters("southcentralus", ("entraPipelineClientId", Quote("dddddddd-eeee-ffff-0000-111111111111")));   // FunctionKey mode has no application sign-in
        scenarios["reject:reserved-workload-prefix"] = Parameters("southcentralus", ("workloadName", Quote("obsidian2")));   // the whole prefix is reserved, not one word
        scenarios["reject:reserved-workload-prefix-of-vault"] = InEnvironment("ndev", Parameters("southcentralus", ("workloadName", Quote("obsidia")), ("environmentShort", Quote("ndev"))));   // obsidia + ndev would spell stobsidiandevscus01

        // The vault server's template (ADR-0010): the same guards, its own names. Its defaults are workloadName 'vault' and environmentShort 'dev'.
        foreach (var region in regions) scenarios[$"vault:region:{region}"] = Parameters(region);
        scenarios["vault:suffix"] = Parameters("southcentralus", ("nameSuffix", Quote("b")));
        scenarios["vault:exists"] = Parameters("southcentralus", ("vaultExists", "true"), ("shareAuth", Quote("AccountKey")));   // every later provision, and the key fallback
        scenarios["vault:sync"] = Parameters("southcentralus", ("syncEnabled", "true"), ("syncRemoteVault", Quote("My Vault")), ("syncExcludeSeed", Quote("Personal,Journal")), ("syncLoginFromKeyVault", "true"));   // step 2: the headless Sync app
        scenarios["vault:reject:sync-vault"] = Parameters("southcentralus", ("syncEnabled", "true"));   // sync on without a remote vault name
        scenarios["vault:entra"] = Parameters("southcentralus", ("authMode", Quote("Entra")), ("entraClientId", Quote("66666666-7777-8888-9999-aaaaaaaaaaaa")), ("entraAllowedClientIds", Quote("bbbbbbbb-cccc-dddd-eeee-ffffffffffff,cccccccc-dddd-eeee-ffff-000000000000")));   // step 3: sign-in on (made-up ids)
        scenarios["vault:reject:entra-client"] = Parameters("southcentralus", ("authMode", Quote("Entra")), ("entraAllowedClientIds", Quote("bbbbbbbb-cccc-dddd-eeee-ffffffffffff")));   // sign-in on without the server app's client id
        scenarios["vault:reject:entra-allowed"] = Parameters("southcentralus", ("authMode", Quote("Entra")), ("entraClientId", Quote("66666666-7777-8888-9999-aaaaaaaaaaaa")));   // sign-in on without an allowed client
        scenarios["vault:long:westcentralus"] = InEnvironment("prd", Parameters("westcentralus", ("workloadName", Quote("obsidian")), ("environmentShort", Quote("prd")), ("nameSuffix", Quote("b"))));   // kv-obsidian-prd-wcus-01b = 24
        scenarios["vault:reject:location"] = Parameters("brazilsouth");
        scenarios["vault:reject:environmentName"] = InEnvironment("prod", Parameters("southcentralus"));
        scenarios["vault:reject:keyvault-length"] = Parameters("southcentralus", ("workloadName", Quote("obsidian2")), ("nameSuffix", Quote("abcd")));   // kv-obsidian2-dev-scus-01abcd = 28 characters, with the required prefix
        scenarios["vault:reject:reserved-workload"] = Parameters("southcentralus", ("workloadName", Quote("adr")));   // would take the ADR server's names
        scenarios["vault:reject:custom-workload"] = Parameters("southcentralus", ("workloadName", Quote("team")));   // any name without the obsidian prefix could collide with a custom ADR_WORKLOAD_NAME

        return scenarios.ToDictionary(s => s.Key, s => Task.Run(() => RunAsync(s.Key.StartsWith("vault:", StringComparison.Ordinal) ? VaultTemplate : AdrTemplate, s.Value)));
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

    private async Task<BicepSnapshot> RunAsync(string template, Dictionary<string, string> parameters)
    {
        if (UnusableReason() is { } reason) throw new InvalidOperationException(reason);
        var bicep = FindBicep()!;
        var work = _workDirectory.Value;
        var id = Guid.NewGuid().ToString("N");
        var paramFile = Path.Join(work, $"p-{id}.bicepparam");
        await File.WriteAllTextAsync(paramFile, $"using '{template}'" + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, parameters.Select(p => $"param {p.Key} = {p.Value}")) + Environment.NewLine);

        var location = parameters["location"].Trim('\'').Replace(" ", "").ToLowerInvariant();
        var start = new ProcessStartInfo(bicep) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "snapshot", paramFile, "--mode", "overwrite", "--subscription-id", FakeSubscriptionId, "--location", location }) start.ArgumentList.Add(a);

        await _limit.WaitAsync(_stop.Token);
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
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
        _stop.Cancel();   // a run that is still going (for example after a filtered test run) is stopped, then its folder can be removed
        if (_runs.IsValueCreated) try { Task.WaitAll(_runs.Value.Values.ToArray(), TimeSpan.FromSeconds(30)); } catch (AggregateException) { /* cancelled runs */ }
        if (_workDirectory.IsValueCreated)
            try { Directory.Delete(_workDirectory.Value, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* temp folder; the OS cleans it */ }
        _stop.Dispose();
    }
}

internal sealed record BicepSnapshot(bool Succeeded, string Output, IReadOnlyList<NamingChecks.Resource> Resources);

/// <summary>Like <see cref="FactAttribute"/>, but skipped when the Bicep CLI is missing, unless ADR_TEST_REQUIRE_BICEP=true (set in CI): then it runs and fails.</summary>
public sealed class BicepFactAttribute : FactAttribute
{
    public BicepFactAttribute()
    {
        if (!BicepSnapshotFixture.Required && BicepSnapshotFixture.UnusableReason() is { } reason) Skip = reason;
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="BicepFactAttribute"/>.</summary>
public sealed class BicepTheoryAttribute : TheoryAttribute
{
    public BicepTheoryAttribute()
    {
        if (!BicepSnapshotFixture.Required && BicepSnapshotFixture.UnusableReason() is { } reason) Skip = reason;
    }
}
