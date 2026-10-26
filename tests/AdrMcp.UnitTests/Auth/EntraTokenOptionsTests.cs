using AdrMcp.Application.Options;
using AdrMcp.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AdrMcp.UnitTests.Auth;

/// <summary>Settings that cannot mean what the operator intended stop the host at start; the defaults change nothing (ADR-0008).</summary>
public class EntraTokenOptionsTests
{
    private static EntraTokenOptionsValidator Validator(string? platformHost = "func-adr-test.azurewebsites.net")
        => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [EntraTokenPolicy.PlatformHostNameSetting] = platformHost }).Build());

    private static AuthOptions Good(Action<AuthOptions>? edit = null)
    {
        var o = new AuthOptions
        {
            Mode = "EntraBuiltIn",
            Tenants = { new TenantEnrollment { Id = TestIdp.TenantId } },
            Entra = new EntraTokenOptions
            {
                ValidateBearerToken = true,
                TenantId = TestIdp.TenantId,
                ClientId = TestIdp.ServerClientId,
                AllowedClientIds = new[] { TestIdp.AllowedClientId },
            },
        };
        edit?.Invoke(o);
        return o;
    }

    private static ValidateOptionsResult Check(AuthOptions o, string? platformHost = "func-adr-test.azurewebsites.net") => Validator(platformHost).Validate(null, o);

    [Fact]
    public void The_defaults_validate_and_turn_everything_off()
    {
        var o = new AuthOptions();
        Assert.False(o.Entra.ValidateBearerToken);
        Assert.True(Check(o).Succeeded);
        Assert.True(Check(new AuthOptions { Mode = "EntraBuiltIn" }, platformHost: null).Succeeded);   // a half-filled block does not matter while the switch is off
    }

    [Fact]
    public void A_complete_configuration_validates()
        => Assert.True(Check(Good()).Succeeded);

    [Fact]
    public void The_switch_requires_entra_mode()
        => Assert.Contains("Auth:Mode=EntraBuiltIn", Check(Good(o => o.Mode = "FunctionKey")).FailureMessage);

    [Theory]
    [InlineData("")]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void The_tenant_must_be_a_real_guid(string tenant)
        => Assert.Contains("Auth:Entra:TenantId", Check(Good(o => o.Entra.TenantId = tenant)).FailureMessage);

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void The_server_client_id_must_be_a_guid(string client)
        => Assert.Contains("Auth:Entra:ClientId", Check(Good(o => o.Entra.ClientId = client)).FailureMessage);

    [Fact]
    public void An_empty_allow_list_is_refused_because_it_would_accept_every_client()
    {
        Assert.Contains("AllowedClientIds", Check(Good(o => o.Entra.AllowedClientIds = Array.Empty<string>())).FailureMessage);
        Assert.Contains("AllowedClientIds", Check(Good(o => o.Entra.AllowedClientIds = new[] { " " })).FailureMessage);
        Assert.Contains("AllowedClientIds entry 1", Check(Good(o => o.Entra.AllowedClientIds = new[] { TestIdp.AllowedClientId, "*" })).FailureMessage);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("api://*")]
    [InlineData("two words")]
    [InlineData("http://not-allowed.example")]
    public void Audiences_cannot_be_wildcards_or_loose_text(string audience)
        => Assert.Contains("Auth:Entra:Audiences", Check(Good(o => o.Entra.Audiences = new[] { audience })).FailureMessage);

    [Fact]
    public void A_scope_that_is_also_an_app_role_value_is_refused()
    {
        var r = Check(Good(o => o.Entra.RequiredScopes = new[] { "Adr.Admin" }));
        Assert.Contains("also an app role value", r.FailureMessage);
        Assert.Contains("also an app role value", Check(Good(o => o.Entra.RequiredScopes = new[] { "adr.reader" })).FailureMessage);   // case-insensitive
    }

    [Fact]
    public void The_default_scope_is_refused_if_someone_maps_it_as_an_app_role()
        => Assert.Contains("also an app role value", Check(Good(o => o.AppRoleMap[EntraTokenOptions.DefaultScope] = "Reader")).FailureMessage);

    [Theory]
    [InlineData("https://login.microsoftonline.com")]
    [InlineData("login.microsoftonline.com/")]
    [InlineData("login.microsoftonline.com:443")]
    [InlineData("localhost")]
    [InlineData("a b.example")]
    [InlineData("user@login.microsoftonline.com")]
    public void The_authority_host_is_a_bare_dotted_host_name(string host)
        => Assert.Contains("AuthorityHost", Check(Good(o => o.Entra.AuthorityHost = host)).FailureMessage);

    [Theory]
    [InlineData("http://login.microsoftonline.com/keys")]
    [InlineData("ftp://x.example/keys")]
    [InlineData("https://user:pw@x.example/keys")]
    [InlineData("https://x.example/keys#frag")]
    [InlineData("/relative/keys")]
    public void The_key_address_must_be_https(string url)
        => Assert.Contains("JwksUrl", Check(Good(o => o.Entra.JwksUrl = url)).FailureMessage);

    [Theory]
    [InlineData("https://login.microsoftonline.com/t/discovery/v2.0/keys")]
    [InlineData("http://127.0.0.1:9999/keys")]
    [InlineData("http://localhost:9999/keys")]
    public void An_https_or_loopback_key_address_is_accepted(string url)
        => Assert.True(Check(Good(o => o.Entra.JwksUrl = url)).Succeeded);

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void Clock_skew_is_between_0_and_300_seconds(int seconds)
        => Assert.Contains("ClockSkewSeconds", Check(Good(o => o.Entra.ClockSkewSeconds = seconds)).FailureMessage);

    [Theory]
    [InlineData(4)]
    [InlineData(1441)]
    public void The_key_cache_lifetime_is_between_5_and_1440_minutes(int minutes)
        => Assert.Contains("JwksCacheMinutes", Check(Good(o => o.Entra.JwksCacheMinutes = minutes)).FailureMessage);

    [Fact]
    public void The_resource_uri_comes_from_settings_or_from_the_platform_host_name_and_nothing_else()
    {
        Assert.True(Check(Good(), platformHost: "func-adr-test.azurewebsites.net").Succeeded);
        Assert.Contains("ResourceUri is required", Check(Good(), platformHost: null).FailureMessage);
        Assert.True(Check(Good(o => o.Entra.ResourceUri = "https://mcp.contoso.example/runtime/webhooks/mcp"), platformHost: null).Succeeded);
        Assert.Contains("ResourceUri", Check(Good(o => o.Entra.ResourceUri = "http://mcp.contoso.example/runtime/webhooks/mcp")).FailureMessage);
        Assert.Contains("ResourceUri", Check(Good(o => o.Entra.ResourceUri = "https://mcp.contoso.example/mcp?x=1")).FailureMessage);
    }

    [Fact]
    public void The_tenant_must_be_one_of_the_enrolled_tenants_when_any_are_enrolled()
    {
        Assert.Contains("not one of the enrolled tenants", Check(Good(o => o.Tenants[0].Id = TestIdp.OtherTenantId)).FailureMessage);
        Assert.True(Check(Good(o => o.Tenants.Clear())).Succeeded);   // nothing enrolled: any tenant resolves, the token check still pins one
        Assert.True(Check(Good(o => { o.Tenants.Clear(); o.TenantMap[TestIdp.TenantId.ToUpperInvariant()] = "contoso"; })).Succeeded);
    }

    [Fact]
    public void Failure_messages_name_the_setting_and_never_echo_the_configured_value()
    {
        const string unwanted = "Configured-Value-Must-Not-Be-Echoed";
        var r = Check(Good(o =>
        {
            o.Entra.TenantId = unwanted; o.Entra.ClientId = unwanted; o.Entra.AllowedClientIds = new[] { unwanted };
            o.Entra.Audiences = new[] { unwanted + " x" }; o.Entra.AuthorityHost = unwanted + "/"; o.Entra.JwksUrl = "http://" + unwanted;
            o.Entra.RequiredScopes = new[] { unwanted + "!" }; o.Entra.ResourceUri = "http://" + unwanted;
        }));
        Assert.True(r.Failed);
        Assert.DoesNotContain(unwanted, r.FailureMessage);
    }

    [Fact]
    public void Settings_bind_from_configuration_including_the_indexed_arrays_app_settings_use()
    {
        // App settings such as Auth__Entra__AllowedClientIds__0 arrive here as Auth:Entra:AllowedClientIds:0.
        var colon = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Entra:ValidateBearerToken"] = "true",
            ["Auth:Entra:TenantId"] = TestIdp.TenantId,
            ["Auth:Entra:AllowedClientIds:0"] = TestIdp.AllowedClientId,
            ["Auth:Entra:AllowedClientIds:1"] = TestIdp.OtherClientId,
            ["Auth:Entra:RequiredScopes:0"] = "mcp.use",
            ["Auth:Entra:AllowApplicationTokens"] = "true",
            ["Auth:Entra:JwksCacheMinutes"] = "120",
        }).Build();
        var bound = colon.GetSection("Auth").Get<AuthOptions>()!;
        Assert.True(bound.Entra.ValidateBearerToken);
        Assert.Equal(new[] { TestIdp.AllowedClientId, TestIdp.OtherClientId }, bound.Entra.AllowedClientIds);
        Assert.Equal(new[] { "mcp.use" }, bound.Entra.RequiredScopes);
        Assert.True(bound.Entra.AllowApplicationTokens);
        Assert.Equal(120, bound.Entra.JwksCacheMinutes);
    }

    [Fact]
    public void The_policy_derives_the_issuer_key_address_audiences_and_client_scope_from_the_settings()
    {
        var p = EntraTokenPolicy.From(Good().Entra, "func-adr-test.azurewebsites.net");
        Assert.Equal($"https://login.microsoftonline.com/{TestIdp.TenantId}/v2.0", p.Issuer);
        Assert.Equal($"https://login.microsoftonline.com/{TestIdp.TenantId}/discovery/v2.0/keys", p.JwksUri.ToString());
        Assert.Equal(new[] { TestIdp.ServerClientId }, p.Audiences);
        Assert.Equal($"api://{TestIdp.ServerClientId}/user_impersonation", p.ClientScope);
        Assert.Equal("https://func-adr-test.azurewebsites.net/runtime/webhooks/mcp", p.ResourceUri);
        Assert.Equal(TimeSpan.FromSeconds(60), p.ClockSkew);
        Assert.Equal(TimeSpan.FromMinutes(60), p.JwksCacheTtl);
        Assert.False(p.AllowApplicationTokens);
    }

    [Fact]
    public void A_sovereign_cloud_authority_host_changes_the_issuer_and_key_address()
    {
        var p = EntraTokenPolicy.From(Good(o => o.Entra.AuthorityHost = "login.microsoftonline.us").Entra, "h.example");
        Assert.Equal($"https://login.microsoftonline.us/{TestIdp.TenantId}/v2.0", p.Issuer);
        Assert.StartsWith("https://login.microsoftonline.us/", p.JwksUri.ToString());
    }
}
