using AdrMcp.Application.Options;
using AdrMcp.Infrastructure.Options;

namespace AdrMcp.UnitTests.Application;

public class TenantDirectoryTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Enrollment_is_case_insensitive_and_resolution_and_maintenance_share_one_partition_key()
    {
        // An operator may paste the GUID in upper case; Entra issues it in lower case. Both paths must land on the same partition.
        var directory = new TenantDirectory(new AuthOptions { Mode = "EntraBuiltIn", Tenants = { new TenantEnrollment { Id = Tid.ToUpperInvariant() } } });
        Assert.True(directory.TryResolveTenant(Tid, out var tenantId));
        Assert.Equal(Tid, tenantId);
        Assert.Equal((Tid, "default"), Assert.Single(directory.KnownScopes()));
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

    [Fact]
    public void Validator_accepts_guid_enrollments()
        => Assert.True(new AuthOptionsValidator().Validate(null, new AuthOptions { Tenants = { new TenantEnrollment { Id = Tid } } }).Succeeded);
}
