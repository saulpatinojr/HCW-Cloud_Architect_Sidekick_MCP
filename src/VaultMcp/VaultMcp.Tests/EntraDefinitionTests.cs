using System.Text.Json.Nodes;
using VaultMcp.Http;
using Xunit;

namespace VaultMcp.Tests;

/// <summary>The vault server's Entra definitions in scripts/entra/vault-*.json (ADR-0010 phase 2 step 3) agree with the code defaults and with each other.</summary>
public sealed class EntraDefinitionTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "AdrMcp.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root (AdrMcp.sln) not found above " + AppContext.BaseDirectory);
    }

    private static JsonNode Json(string relative) => JsonNode.Parse(File.ReadAllText(Path.Join(Root, relative)))!;

    [Fact]
    public void The_role_file_holds_exactly_the_default_role_for_users_only()
    {
        var roles = Json("scripts/entra/vault-app-roles.json").AsArray();
        var role = Assert.Single(roles)!;
        Assert.Equal(VaultEntraOptions.DefaultRole, role["value"]!.GetValue<string>());
        Assert.Equal(new[] { "User" }, role["allowedMemberTypes"]!.AsArray().Select(m => m!.GetValue<string>()));   // no application tokens: the vault is read as a person
        Assert.True(role["isEnabled"]!.GetValue<bool>());
        Assert.True(Guid.TryParse(role["id"]!.GetValue<string>(), out var id) && id != Guid.Empty);
    }

    [Fact]
    public void The_scope_file_holds_the_default_scope_and_its_value_differs_from_the_role()
    {
        var scopes = Json("scripts/entra/vault-oauth2-permission-scopes.json")["api"]!["oauth2PermissionScopes"]!.AsArray();
        var scope = Assert.Single(scopes)!;
        Assert.Equal(VaultEntraOptions.DefaultScope, scope["value"]!.GetValue<string>());
        Assert.Equal("User", scope["type"]!.GetValue<string>());
        Assert.NotEqual(VaultEntraOptions.DefaultRole, VaultEntraOptions.DefaultScope);   // Entra keeps roles and scopes in one collection
        Assert.Contains("vault", scope["adminConsentDisplayName"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_pre_authorization_names_azure_cli_and_the_vault_scope_not_the_adr_servers()
    {
        var pre = Json("scripts/entra/vault-preauthorize-azure-cli.json")["api"]!["preAuthorizedApplications"]!.AsArray();
        var entry = Assert.Single(pre)!;
        Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", entry["appId"]!.GetValue<string>());   // Microsoft's public client id for the Azure CLI
        var vaultScopeId = Json("scripts/entra/vault-oauth2-permission-scopes.json")["api"]!["oauth2PermissionScopes"]![0]!["id"]!.GetValue<string>();
        var adrScopeId = Json("scripts/entra/oauth2-permission-scopes.json")["api"]!["oauth2PermissionScopes"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(new[] { vaultScopeId }, entry["delegatedPermissionIds"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.NotEqual(adrScopeId, vaultScopeId);
        Assert.NotEqual(Json("scripts/entra/app-roles.json").AsArray().Select(r => r!["id"]!.GetValue<string>()), Json("scripts/entra/vault-app-roles.json").AsArray().Select(r => r!["id"]!.GetValue<string>()));
    }

    [Fact]
    public void The_readme_and_the_template_use_the_checked_in_definitions_and_the_same_variable_names()
    {
        var readme = File.ReadAllText(Path.Join(Root, "src/VaultMcp/README.md"));
        foreach (var file in new[] { "vault-app-roles.json", "vault-oauth2-permission-scopes.json", "vault-preauthorize-azure-cli.json" })
        {
            Assert.Contains("scripts/entra/" + file, readme);
        }
        var parameters = File.ReadAllText(Path.Join(Root, "infra/vault/main.parameters.json"));
        foreach (var variable in new[] { "VAULT_AUTH_MODE", "VAULT_ENTRA_CLIENT_ID", "VAULT_ENTRA_ALLOWED_CLIENT_IDS", "VAULT_ENTRA_REQUIRED_ROLE" })
        {
            Assert.Contains(variable, parameters);
            Assert.Contains(variable, readme);
        }
        var module = File.ReadAllText(Path.Join(Root, "infra/vault/modules/containerapp.bicep"));
        foreach (var key in new[] { VaultHostOptions.ModeKey, VaultEntraOptions.TenantIdKey, VaultEntraOptions.ClientIdKey, VaultEntraOptions.AllowedClientIdsKey, VaultEntraOptions.RequiredRoleKey, VaultEntraOptions.ResourceUriKey })
        {
            Assert.Contains(key.Replace(":", "__"), module);   // the template spells the settings as environment variables
        }
    }
}
