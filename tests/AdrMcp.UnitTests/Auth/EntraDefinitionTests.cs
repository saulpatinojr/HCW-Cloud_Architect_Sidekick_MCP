using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdrMcp.Application.Options;

namespace AdrMcp.UnitTests.Auth;

/// <summary>
/// Static checks on the Entra definitions the runbook creates and on the places that name them (code, Bicep, docs). No tenant is touched.
/// The first rule exists because of a real failure: in the sister project HCW-CloudAssessor_App the first real-tenant plan was refused
/// because an app role and a delegated scope shared one value. Entra keeps both in one permission-definition collection
/// (Microsoft Learn, "App role limits"), so a role value must never equal a scope value, and an id must never be reused.
/// </summary>
public class EntraDefinitionTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "AdrMcp.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AdrMcp.sln not found above the test assembly.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Join(Root, relative));
    private static JsonNode Json(string relative) => JsonNode.Parse(Read(relative))!;

    private static (string[] Values, string[] Ids) Roles()
    {
        var roles = Json("scripts/entra/app-roles.json").AsArray();
        return (roles.Select(r => r!["value"]!.GetValue<string>()).ToArray(), roles.Select(r => r!["id"]!.GetValue<string>()).ToArray());
    }

    private static (string[] Values, string[] Ids) Scopes()
    {
        var scopes = Json("scripts/entra/oauth2-permission-scopes.json")["api"]!["oauth2PermissionScopes"]!.AsArray();
        return (scopes.Select(r => r!["value"]!.GetValue<string>()).ToArray(), scopes.Select(r => r!["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void No_app_role_value_equals_a_delegated_scope_value()
    {
        var (roleValues, _) = Roles();
        var (scopeValues, _) = Scopes();
        var clash = roleValues.Intersect(scopeValues, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(clash.Count == 0, "A role value and a scope value are the same: " + string.Join(", ", clash));
    }

    [Fact]
    public void No_id_is_used_twice_across_roles_and_scopes()
    {
        var ids = Roles().Ids.Concat(Scopes().Ids).ToList();
        Assert.All(ids, id => Assert.True(Guid.TryParse(id, out var g) && g != Guid.Empty, id));
        Assert.Equal(ids.Count, ids.Select(i => i.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void The_roles_the_runbook_creates_are_exactly_the_roles_the_server_maps()
    {
        var mapped = new AuthOptions().AppRoleMap.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.True(mapped.SetEquals(Roles().Values), "scripts/entra/app-roles.json and AuthOptions.AppRoleMap have drifted apart.");
        Assert.All(Json("scripts/entra/app-roles.json").AsArray(), r =>
        {
            Assert.True(r!["isEnabled"]!.GetValue<bool>());
            var members = r["allowedMemberTypes"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray();
            // People hold the four user roles; only the pipeline role can be assigned to an application, and no role can be held by both.
            Assert.Equal(r["value"]!.GetValue<string>() == AuthOptions.ApplicationReaderRole ? new[] { "Application" } : new[] { "User" }, members);
        });
    }

    [Fact]
    public void The_one_application_role_maps_to_Reader_and_nothing_else_is_open_to_applications()
    {
        var roles = Json("scripts/entra/app-roles.json").AsArray();
        var application = Assert.Single(roles, r => r!["allowedMemberTypes"]!.AsArray().Any(m => m!.GetValue<string>() == "Application"))!;
        Assert.Equal(AuthOptions.ApplicationReaderRole, application["value"]!.GetValue<string>());
        Assert.Equal("Reader", new AuthOptions().AppRoleMap[AuthOptions.ApplicationReaderRole]);   // docs/12 A4: the pipeline reads, nothing more
        Assert.Contains("Adr.Reader.Application", Read("docs/11-entra-sign-in.md"));
    }

    [Fact]
    public void The_pipeline_identity_is_opt_in_everywhere_and_defaults_to_off()
    {
        Assert.Contains("param entraPipelineClientId string = ''", Read("infra/main.bicep"));
        Assert.Contains("param entraPipelineClientId string = ''", Read("infra/modules/functionapp.bicep"));
        Assert.Contains("${ADR_ENTRA_PIPELINE_CLIENT_ID}", Read("infra/main.parameters.json"));
        var workflow = Read(".github/workflows/ci-cd.yml");
        Assert.Contains("vars.ADR_ENTRA_PIPELINE_READ == 'true' && vars.AZURE_CLIENT_ID || ''", workflow);   // the job's own identity, never a second credential
        Assert.Contains("--read-only", workflow);
        Assert.DoesNotContain("ADR_ENTRA_PIPELINE_CLIENT_SECRET", workflow);
    }

    [Fact]
    public void The_scope_the_runbook_creates_is_the_scope_the_server_requires_by_default()
        => Assert.Equal(new[] { EntraTokenOptions.DefaultScope }, Scopes().Values);

    [Fact]
    public void The_default_scope_is_not_a_default_role()
        => Assert.DoesNotContain(EntraTokenOptions.DefaultScope, new AuthOptions().AppRoleMap.Keys, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Bicep_publishes_the_same_scope_the_runbook_creates()
    {
        var bicep = Read("infra/modules/functionapp.bicep");
        var match = Regex.Match(bicep, @"'api://\$\{entraClientId\}/([A-Za-z0-9._:-]+)'");
        Assert.True(match.Success, "functionapp.bicep no longer builds api://<client id>/<scope>.");
        Assert.Equal(EntraTokenOptions.DefaultScope, match.Groups[1].Value);
        Assert.DoesNotContain(match.Groups[1].Value, Roles().Values, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pre_authorization_names_only_scopes_that_exist_and_only_the_azure_cli()
    {
        var pre = Json("scripts/entra/preauthorize-azure-cli.json")["api"]!["preAuthorizedApplications"]!.AsArray();
        var app = Assert.Single(pre)!;
        Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", app["appId"]!.GetValue<string>());   // the Azure CLI's public application ID
        var granted = app["delegatedPermissionIds"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
        Assert.NotEmpty(granted);
        Assert.All(granted, id => Assert.Contains(id, Scopes().Ids));
    }

    [Fact]
    public void In_app_validation_is_off_unless_asked_for_in_code_and_in_every_deployment_default()
    {
        Assert.False(new AuthOptions().Entra.ValidateBearerToken);
        Assert.False(new EntraTokenOptions().AllowApplicationTokens);
        Assert.Contains("param entraValidateInApp bool = false", Read("infra/main.bicep"));
        Assert.Contains("param entraValidateInApp bool = false", Read("infra/modules/functionapp.bicep"));
        Assert.Contains("${ADR_ENTRA_VALIDATE_IN_APP=false}", Read("infra/main.parameters.json"));
        Assert.Contains("vars.ADR_ENTRA_VALIDATE_IN_APP || 'false'", Read(".github/workflows/ci-cd.yml"));
        Assert.Contains("\"Auth__Mode\": \"FunctionKey\"", Read("src/AdrMcp.Functions/local.settings.json.example"));
        Assert.DoesNotContain("Auth__Entra__", Read("src/AdrMcp.Functions/local.settings.json.example"));
    }

    [Fact]
    public void Bicep_only_emits_the_in_app_settings_for_entra_mode_with_the_switch_on()
    {
        var bicep = Read("infra/modules/functionapp.bicep");
        Assert.Contains("var validateInApp = entraValidateInApp ? (entraEnabled ? true : fail(", bicep);   // asking for it without Entra mode fails the deployment
        Assert.Contains("Auth__Entra__AuthorityHost", bicep);
        Assert.Contains("var entraInAppSettings = validateInApp ?", bicep);
        // Every Auth__Entra__ setting lives in that one conditional block.
        var code = string.Join("\n", bicep.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));   // comments may name the settings
        var outside = Regex.Matches(code, @"Auth__Entra__\w+").Count;
        var inside = Regex.Matches(Regex.Match(bicep, @"var entraInAppSettings = .*?\r?\n\s*\r?\nresource storage", RegexOptions.Singleline).Value, @"Auth__Entra__\w+").Count;
        Assert.Equal(inside, outside);
        Assert.True(inside >= 4);
    }

    [Fact]
    public void The_runbook_uses_the_checked_in_definitions_instead_of_retyping_them()
    {
        var doc = Read("docs/11-entra-sign-in.md");
        foreach (var file in new[] { "app-roles.json", "oauth2-permission-scopes.json", "preauthorize-azure-cli.json" })
        {
            Assert.Contains("scripts/entra/" + file, doc);
            Assert.True(File.Exists(Path.Join(Root, "scripts", "entra", file)), file);
        }
    }

    [Fact]
    public void The_setup_script_uses_every_checked_in_definition_and_never_creates_a_secret()
    {
        var script = Read("scripts/setup-entra-signin.ps1");
        foreach (var file in new[] { "app-roles.json", "oauth2-permission-scopes.json", "vault-app-roles.json", "vault-oauth2-permission-scopes.json" })
            Assert.Contains($"'{file}'", script);
        foreach (var role in new[] { "Adr.Admin", AuthOptions.ApplicationReaderRole, "Vault.Reader" })
            Assert.Contains($"'{role}'", script);
        Assert.Contains("--requested-access-token-version 2", script);
        Assert.Contains("appRoleAssignmentRequired=true", script);
        Assert.Contains("--is-fallback-public-client true", script);
        Assert.Contains("https://claude.ai/api/mcp/auth_callback", script);
        // The secret of the confidential client is the owner's: the script may print the command, never run it.
        Assert.DoesNotMatch(@"(?m)^\s*az ad app credential reset", script);
        Assert.DoesNotContain("--client-secret", script);
        Assert.Contains("docs/11", Read("docs/12-entra-sign-in-checklist.md"));
        Assert.Contains("scripts/setup-entra-signin.ps1", Read("docs/11-entra-sign-in.md"));
    }

    [Fact]
    public void Infrastructure_never_forces_the_platform_authentication_flag_or_ships_a_token_or_secret_for_this_feature()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Join(Root, "infra"), "*.*", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(Path.Join(Root, "scripts", "entra"))))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("WEBSITE_AUTH_ENABLED", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", text);   // no JWT
            Assert.DoesNotMatch(@"(?i)client[_-]?secret""?\s*[:=]\s*""?[A-Za-z0-9~._-]{12,}", text);
        }
    }
}
