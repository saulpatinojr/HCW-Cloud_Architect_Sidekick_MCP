using AdrMcp.Application.Options;
using AdrMcp.Infrastructure.Options;

namespace AdrMcp.UnitTests.Application;

public class TenantDirectoryTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string HexTid = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";   // has letters, so upper and lower case really differ

    [Fact]
    public void Enrollment_is_case_insensitive_and_resolution_and_maintenance_share_one_partition_key()
    {
        // An operator may paste the GUID in upper case; Entra issues it in lower case. Both paths must land on the same partition.
        var directory = new TenantDirectory(new AuthOptions { Mode = "EntraBuiltIn", Tenants = { new TenantEnrollment { Id = HexTid.ToUpperInvariant() } } });
        Assert.True(directory.TryResolveTenant(HexTid, out var tenantId));
        Assert.Equal(HexTid, tenantId);
        Assert.Equal((HexTid, "default"), Assert.Single(directory.KnownScopes()));
        Assert.False(directory.TryResolveTenant("22222222-2222-2222-2222-222222222222", out _));
    }

    [Fact]
    public void Known_scopes_cover_enrolled_entra_tenants_and_skip_wildcards()
    {
        var directory = new TenantDirectory(new AuthOptions
        {
            Mode = "EntraBuiltIn",
            Tenants =
            {
                new TenantEnrollment { Id = Tid },                                                        // no workspaces: default
                new TenantEnrollment { Id = "33333333-3333-3333-3333-333333333333", MapTo = "contoso", Workspaces = new[] { "team-a", "*" } },
            },
        });
        Assert.Equal(new[] { (Tid, "default"), ("contoso", "team-a") }, directory.KnownScopes().OrderBy(s => s.TenantId == Tid ? 0 : 1));
    }

    [Fact]
    public void Known_scopes_keep_the_function_key_binding_first()
    {
        var directory = new TenantDirectory(new AuthOptions { Mode = "FunctionKey", StaticTenantId = "default-tenant", StaticWorkspaceId = "default" });
        Assert.Equal(("default-tenant", "default"), Assert.Single(directory.KnownScopes()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contoso.onmicrosoft.com")]
    public void Validator_rejects_a_tenant_id_that_is_not_a_guid(string id)
        => Assert.True(new AuthOptionsValidator().Validate(null, new AuthOptions { Tenants = { new TenantEnrollment { Id = id } } }).Failed);

    [Fact]
    public void Validator_rejects_a_tenant_configured_twice()
    {
        var options = new AuthOptions { TenantMap = { [Tid] = "a" }, Tenants = { new TenantEnrollment { Id = Tid.ToUpperInvariant(), MapTo = "b" } } };
        Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);
    }

    private const string Tid2 = "33333333-3333-3333-3333-333333333333";

    public static TheoryData<string, AuthOptions> MisconfiguredTenants => new()
    {
        // A copied Auth__Tenants block with only the Id changed: two tenants would share one partition and merge workspaces.
        { "same MapTo", new AuthOptions { Tenants = { new() { Id = Tid, MapTo = "contoso" }, new() { Id = Tid2, MapTo = "contoso", Workspaces = new[] { "*" } } } } },
        { "MapTo is another tenant's tid", new AuthOptions { Tenants = { new() { Id = HexTid }, new() { Id = Tid2, MapTo = HexTid.ToUpperInvariant() } } } },
        { "TenantMap values collide", new AuthOptions { TenantMap = { [Tid] = "contoso", [Tid2] = "contoso" } } },
        { "duplicate in braces", new AuthOptions { Tenants = { new() { Id = Tid, MapTo = "contoso" }, new() { Id = "{" + Tid + "}", MapTo = "fabrikam" } } } },
        { "duplicate without hyphens", new AuthOptions { Tenants = { new() { Id = Tid }, new() { Id = Tid.Replace("-", ""), MapTo = "fabrikam" } } } },
        { "TenantMap key not a GUID", new AuthOptions { TenantMap = { ["contoso.onmicrosoft.com"] = "contoso" } } },
        { "MapTo with a path separator", new AuthOptions { Tenants = { new() { Id = Tid, MapTo = "contoso/team-a" } } } },
        { "workspace with a path separator", new AuthOptions { Tenants = { new() { Id = Tid, Workspaces = new[] { "team-a/../x" } } } } },
        { "TenantWorkspaces key with a path separator", new AuthOptions { TenantWorkspaces = { ["a/b"] = new[] { "default" } } } },
    };

    [Theory]
    [MemberData(nameof(MisconfiguredTenants))]
    public void Validator_rejects_configurations_that_merge_or_escape_tenants(string _, AuthOptions options)
        => Assert.True(new AuthOptionsValidator().Validate(null, options).Failed);

    [Fact]
    public void Validator_accepts_the_wildcard_workspace()
        => Assert.True(new AuthOptionsValidator().Validate(null, new AuthOptions { Tenants = { new() { Id = Tid, Workspaces = new[] { "*", "team-a" } } } }).Succeeded);

    [Fact]
    public void Guid_keyed_workspaces_and_enrollment_meet_on_the_partition_callers_use()
    {
        // Upper-case GUID key in the dictionary form plus a lower-case enrollment: one tenant, one partition key.
        var directory = new TenantDirectory(new AuthOptions
        {
            Mode = "EntraBuiltIn",
            TenantWorkspaces = { [HexTid.ToUpperInvariant()] = new[] { "team-a" } },
            Tenants = { new() { Id = HexTid } },
        });
        Assert.True(directory.TryResolveTenant(HexTid, out var tenantId));
        Assert.Contains("team-a", directory.WorkspacesFor(tenantId, "default"));
        Assert.Equal((HexTid, "team-a"), Assert.Single(directory.KnownScopes()));
    }

    [Fact]
    public void Enrollment_is_ignored_in_function_key_mode()
    {
        var directory = new TenantDirectory(new AuthOptions
        {
            Mode = "FunctionKey", StaticTenantId = "default-tenant",
            Tenants = { new() { Id = Tid, MapTo = "default-tenant", Workspaces = new[] { "*" } } },
        });
        Assert.DoesNotContain("*", directory.WorkspacesFor("default-tenant", "default"));
        Assert.Equal(("default-tenant", "default"), Assert.Single(directory.KnownScopes()));
    }

    [Fact]
    public void Validator_accepts_guid_enrollments()
        => Assert.True(new AuthOptionsValidator().Validate(null, new AuthOptions { Tenants = { new TenantEnrollment { Id = Tid } } }).Succeeded);
}
