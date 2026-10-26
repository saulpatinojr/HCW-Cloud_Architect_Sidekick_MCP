using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Errors;
using AdrMcp.Infrastructure.Auth;
using AdrMcp.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdrMcp.UnitTests.Auth;

/// <summary>
/// A real tool call through the whole pipeline with in-app token validation switched on (ADR-0008): who gets in, with which role, what
/// the caller is told when refused, and that the platform principal header is not believed.
/// </summary>
public class BearerPipelineTests : IDisposable
{
    private readonly TestIdp _idp = new();
    private readonly StubJwksHandler _jwks;
    private readonly CapturingLoggerProvider _logs = new();

    public BearerPipelineTests() => _jwks = _idp.Handler();

    public void Dispose() => _idp.Dispose();

    private TestHost Host(Action<Dictionary<string, string?>>? configure = null) => new(s =>
    {
        s["Auth:Mode"] = "EntraBuiltIn";
        s["Auth:Tenants:0:Id"] = TestIdp.TenantId;
        s["Auth:Entra:ValidateBearerToken"] = "true";
        s["Auth:Entra:TenantId"] = TestIdp.TenantId;
        s["Auth:Entra:ClientId"] = TestIdp.ServerClientId;
        s["Auth:Entra:AllowedClientIds:0"] = TestIdp.AllowedClientId;
        s["Auth:Entra:ResourceUri"] = TestIdp.ResourceUri;
        configure?.Invoke(s);
    }, services =>
    {
        services.AddSingleton<IClock>(_idp.Clock);
        services.AddLogging(b => b.AddProvider(_logs));
        // The real provider, answering from memory instead of the network.
        services.AddSingleton<ISigningKeyProvider>(sp => new JwksSigningKeyProvider(sp.GetRequiredService<EntraTokenPolicy>(), new HttpClient(_jwks), _idp.Clock, sp.GetRequiredService<ILogger<JwksSigningKeyProvider>>()));
    });

    private static Dictionary<string, string> Headers(string? token) => token is null ? new() : new() { ["Authorization"] = TestIdp.Bearer(token) };

    private static string? Code(JsonElement envelope) => envelope.GetProperty("error").GetProperty("code").GetString();

    private static Dictionary<string, string> ForgedPlatformPrincipal(string role)
    {
        var principal = JsonSerializer.Serialize(new { auth_typ = "aad", claims = new[] { new { typ = "tid", val = TestIdp.TenantId }, new { typ = "oid", val = "attacker" }, new { typ = "roles", val = role } } });
        return new() { ["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal)) };
    }

    [Fact]
    public async Task A_valid_admin_token_runs_a_tool_in_the_tokens_tenant()
    {
        var host = Host();
        var created = await host.CallAsync("adr_create", new { fields = new { title = "Via bearer", decision = "D" } }, Headers(_idp.Mint()));
        Assert.True(created.GetProperty("ok").GetBoolean(), created.ToString());
        Assert.Single(host.Store.RawJson("adrs", TestIdp.TenantId + "|default"));
    }

    [Theory]
    [InlineData("Adr.Reader", true, false)]
    [InlineData("Adr.Author", true, true)]
    [InlineData("Adr.Admin", true, true)]
    public async Task The_adr_role_comes_from_the_token_roles_claim(string role, bool canRead, bool canCreate)
    {
        var host = Host();
        var headers = Headers(_idp.Mint(_idp.Claims(c => c["roles"] = new JsonArray(role))));
        Assert.Equal(canRead, (await host.CallAsync("adr_list", new { }, headers)).GetProperty("ok").GetBoolean());
        var create = await host.CallAsync("adr_create", new { fields = new { title = "T", decision = "D" } }, headers);
        Assert.Equal(canCreate, create.GetProperty("ok").GetBoolean());
        if (!canCreate) Assert.Equal(ErrorCodes.Forbidden, Code(create));
    }

    [Fact]
    public async Task A_token_without_a_mapped_role_gets_nothing_by_default()
    {
        var host = Host();
        var none = await host.CallAsync("adr_list", new { }, Headers(_idp.Mint(_idp.Claims(c => c.Remove("roles")))));
        Assert.Equal(ErrorCodes.Forbidden, Code(none));
        var unknown = await host.CallAsync("adr_list", new { }, Headers(_idp.Mint(_idp.Claims(c => c["roles"] = new JsonArray("Some.Other.Role")))));
        Assert.Equal(ErrorCodes.Forbidden, Code(unknown));
    }

    [Fact]
    public async Task Owner_only_first_a_token_whose_user_holds_no_role_is_refused_even_if_everything_else_is_right()
    {
        // Only the owner is assigned Adr.Admin in Entra. A second signed-in user with a valid token and no assignment gets FORBIDDEN.
        var host = Host();
        var otherUser = _idp.Claims(c => { c["oid"] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"; c.Remove("roles"); });
        Assert.Equal(ErrorCodes.Forbidden, Code(await host.CallAsync("adr_list", new { }, Headers(_idp.Mint(otherUser)))));
        Assert.True((await host.CallAsync("adr_list", new { }, Headers(_idp.Mint()))).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task No_token_is_unauthorized()
    {
        var host = Host();
        Assert.Equal(ErrorCodes.Unauthorized, Code(await host.CallAsync("adr_list", new { }, Headers(null))));
    }

    [Fact]
    public async Task The_platform_principal_header_is_not_believed_when_the_app_validates_the_token()
    {
        var host = Host();
        // A forged admin principal and no token: refused.
        Assert.Equal(ErrorCodes.Unauthorized, Code(await host.CallAsync("adr_list", new { }, ForgedPlatformPrincipal("Adr.Admin"))));
        // A genuine Reader token plus a forged admin principal: still only a Reader.
        var headers = Headers(_idp.Mint(_idp.Claims(c => c["roles"] = new JsonArray("Adr.Reader"))));
        foreach (var kv in ForgedPlatformPrincipal("Adr.Admin")) headers[kv.Key] = kv.Value;
        var create = await host.CallAsync("adr_create", new { fields = new { title = "T", decision = "D" } }, headers);
        Assert.Equal(ErrorCodes.Forbidden, Code(create));
    }

    [Fact]
    public async Task The_platform_flag_is_not_needed_for_a_verified_token_but_a_forged_flag_header_does_not_help_an_attacker()
    {
        var host = Host(s => s["WEBSITE_AUTH_ENABLED"] = "true");
        Assert.True((await host.CallAsync("adr_list", new { }, Headers(_idp.Mint()))).GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.Unauthorized, Code(await host.CallAsync("adr_list", new { }, ForgedPlatformPrincipal("Adr.Admin"))));
    }

    [Fact]
    public async Task A_bad_token_is_unauthorized_with_one_generic_message_whatever_the_reason()
    {
        var host = Host();
        var messages = new HashSet<string>();
        var bad = new[]
        {
            _idp.Mint(_idp.Claims(c => c["exp"] = _idp.Clock.UtcNow.AddHours(-3).ToUnixTimeSeconds())),
            _idp.Mint(_idp.Claims(c => c["aud"] = TestIdp.OtherClientId)),
            _idp.Mint(_idp.Claims(c => c["iss"] = TestIdp.Issuer(TestIdp.OtherTenantId))),
            _idp.Mint(alg: "none"),
            _idp.Mint(kid: "nope"),
            _idp.Mint(_idp.Claims(c => c["azp"] = TestIdp.OtherClientId)),
        };
        foreach (var token in bad)
        {
            var r = await host.CallAsync("adr_list", new { }, Headers(token));
            Assert.Equal(ErrorCodes.Unauthorized, Code(r));
            messages.Add(r.GetProperty("error").GetProperty("message").GetString()!);
        }
        Assert.Single(messages);   // the caller cannot tell which check failed
    }

    [Fact]
    public async Task A_token_with_the_wrong_scope_is_forbidden()
    {
        var host = Host();
        var r = await host.CallAsync("adr_list", new { }, Headers(_idp.Mint(_idp.Claims(c => c["scp"] = "Files.Read"))));
        Assert.Equal(ErrorCodes.Forbidden, Code(r));
    }

    [Fact]
    public async Task A_key_fetch_failure_is_a_retryable_auth_unavailable_error_never_a_success()
    {
        var host = Host();
        _jwks.Fail = true;
        var r = await host.CallAsync("adr_list", new { }, Headers(_idp.Mint()));
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.AuthUnavailable, Code(r));
        Assert.True(r.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.Empty(host.Store.RawJson("adrs", TestIdp.TenantId + "|default"));
        // And it recovers once the key endpoint does.
        _jwks.Fail = false;
        _idp.Clock.Advance(JwksSigningKeyProvider.FailureBackoff + TimeSpan.FromSeconds(1));
        Assert.True((await host.CallAsync("adr_list", new { }, Headers(_idp.Mint()))).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Settings_that_name_a_tenant_the_app_does_not_enrol_stop_the_host()
    {
        // Otherwise every token would pass the signature check and then be refused at the tenant lookup, one call at a time.
        var ex = Assert.Throws<OptionsValidationException>(() => Host(s => s["Auth:Tenants:0:Id"] = TestIdp.OtherTenantId).Services.GetRequiredService<ITenantResolver>());
        Assert.Contains("not one of the enrolled tenants", ex.Message);
    }

    [Fact]
    public void Invalid_entra_settings_stop_the_host_instead_of_weakening_a_check()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Host(s => s["Auth:Entra:AllowedClientIds:0"] = "*").Services.GetRequiredService<ITenantResolver>());
        Assert.Contains("AllowedClientIds", ex.Message);
    }

    [Fact]
    public async Task With_the_switch_off_the_authorization_header_is_ignored_and_the_legacy_path_is_unchanged()
    {
        // FunctionKey mode (the default deployment): a bearer token is neither needed nor looked at.
        var host = new TestHost();
        Assert.True((await host.CallAsync("adr_list", new { }, new Dictionary<string, string> { ["Authorization"] = "Bearer garbage" })).GetProperty("ok").GetBoolean());
        Assert.False(host.Services.GetRequiredService<IBearerTokenValidator>().Enabled);

        // Entra mode without the switch still reads only the platform principal.
        var legacy = new TestHost(s => { s["Auth:Mode"] = "EntraBuiltIn"; s["Auth:Tenants:0:Id"] = TestIdp.TenantId; s["WEBSITE_AUTH_ENABLED"] = "True"; });
        Assert.False(legacy.Services.GetRequiredService<IBearerTokenValidator>().Enabled);
        Assert.Equal(ErrorCodes.Unauthorized, Code(await legacy.CallAsync("adr_list", new { }, Headers(_idp.Mint()))));   // a bearer token alone does not authenticate there
    }

    [Fact]
    public async Task With_the_switch_off_no_key_provider_is_ever_created()
    {
        var host = new TestHost(s => { s["Auth:Mode"] = "EntraBuiltIn"; s["Auth:Tenants:0:Id"] = TestIdp.TenantId; s["WEBSITE_AUTH_ENABLED"] = "True"; },
            services => services.AddSingleton<ISigningKeyProvider>(sp => throw new InvalidOperationException("must not be created")));
        // Resolving the provider throws; a refusal (not an exception) proves nothing asked for it.
        Assert.Equal(ErrorCodes.Unauthorized, Code(await host.CallAsync("adr_list", new { }, Headers(_idp.Mint()))));
    }

    [Fact]
    public async Task Logs_are_content_free_across_success_refusal_and_outage()
    {
        var host = Host();
        var good = _idp.Mint();
        var expired = _idp.Mint(_idp.Claims(c => c["exp"] = _idp.Clock.UtcNow.AddHours(-3).ToUnixTimeSeconds()));
        var wrongAudience = _idp.Mint(_idp.Claims(c => c["aud"] = TestIdp.OtherClientId));
        await host.CallAsync("adr_list", new { }, Headers(good));
        await host.CallAsync("adr_list", new { }, Headers(expired));
        await host.CallAsync("adr_list", new { }, Headers(wrongAudience));
        await host.CallAsync("adr_list", new { }, Headers(_idp.Mint(kid: "kid-secret-looking-0001")));
        await host.CallAsync("adr_list", new { }, Headers("not.a.jwt"));
        _jwks.Fail = true;
        _idp.Clock.Advance(TimeSpan.FromHours(2));
        await host.CallAsync("adr_list", new { }, Headers(_idp.Mint(_idp.Claims(c => { c["exp"] = _idp.Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(); c["nbf"] = _idp.Clock.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(); }))));

        var all = string.Join("\n", _logs.Lines);
        Assert.Contains("Bearer token refused (expired, HTTP 401)", all);        // the fixed reason code is logged...
        Assert.Contains("Bearer token refused (keys_unavailable, HTTP 503)", all);
        foreach (var secret in new[] { good, expired, wrongAudience, TestIdp.ObjectId, TestIdp.UserName, "Owner Example", "kid-secret-looking-0001", TestIdp.KeyId, TestIdp.TenantId, TestIdp.ServerClientId, TestIdp.AllowedClientId, "not.a.jwt", "eyJ" })
            Assert.DoesNotContain(secret, all);                                   // ...and nothing else about the caller or the token
    }
}
