using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AdrMcp.Application.Abstractions;
using AdrMcp.Infrastructure.Auth;

namespace AdrMcp.UnitTests.Auth;

/// <summary>
/// Every way a bearer token can be wrong, and the one way it can be right (ADR-0008). Tokens are minted at test time with a key that
/// exists only in the test; nothing here touches Entra.
/// </summary>
public class BearerTokenValidatorTests : IDisposable
{
    private readonly TestIdp _idp = new();

    public void Dispose() => _idp.Dispose();

    private async Task<BearerResult> Check(string? header, Action<AdrMcp.Application.Options.EntraTokenOptions>? configure = null, StubJwksHandler? handler = null)
    {
        var policy = _idp.Policy(configure);
        return await _idp.Validator(policy, handler ?? _idp.Handler()).ValidateAsync(header, default);
    }

    private Task<BearerResult> CheckToken(string token, Action<AdrMcp.Application.Options.EntraTokenOptions>? configure = null) => Check(TestIdp.Bearer(token), configure);

    private Task<BearerResult> CheckClaims(Action<JsonObject> edit, Action<AdrMcp.Application.Options.EntraTokenOptions>? configure = null)
        => CheckToken(_idp.Mint(_idp.Claims(edit)), configure);

    private static void AssertRefused(BearerResult r, BearerFailure expected, int status)
    {
        Assert.False(r.Succeeded);
        Assert.Null(r.Principal);
        Assert.Equal(expected, r.Failure);
        Assert.Equal(status, r.HttpStatus);
    }

    // ---- the accepted token ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_delegated_token_is_accepted_and_the_principal_comes_from_its_claims()
    {
        var r = await CheckToken(_idp.Mint());

        Assert.True(r.Succeeded, r.ReasonCode);
        var p = r.Principal!;
        Assert.Equal(TestIdp.TenantId, p.TenantId);
        Assert.Equal(TestIdp.ObjectId, p.ObjectId);
        Assert.Equal(TestIdp.AllowedClientId, p.ClientAppId);
        Assert.Equal(new[] { "Adr.Admin" }, p.Roles);
        Assert.Equal(new[] { "user_impersonation" }, p.Scopes);
        Assert.Equal(TestIdp.UserName, p.Display);
        Assert.Equal(_idp.Clock.UtcNow.AddMinutes(60), p.ExpiresAt);
    }

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("bearer ")]
    [InlineData("BEARER ")]
    public async Task The_bearer_scheme_is_case_insensitive(string prefix)
        => Assert.True((await Check(prefix + _idp.Mint())).Succeeded);

    [Fact]
    public async Task An_extra_configured_audience_is_accepted_and_the_default_set_is_only_the_client_id()
    {
        var token = _idp.Mint(_idp.Claims(c => c["aud"] = "api://" + TestIdp.ServerClientId));
        AssertRefused(await CheckToken(token), BearerFailure.WrongAudience, 401);
        Assert.True((await CheckToken(token, o => o.Audiences = new[] { "api://" + TestIdp.ServerClientId })).Succeeded);
    }

    [Fact]
    public async Task Clock_skew_is_honoured_on_both_bounds_and_no_further()
    {
        var now = _idp.Clock.UtcNow;
        Assert.True((await CheckClaims(c => c["exp"] = now.AddSeconds(-59).ToUnixTimeSeconds())).Succeeded);
        AssertRefused(await CheckClaims(c => c["exp"] = now.AddSeconds(-61).ToUnixTimeSeconds()), BearerFailure.Expired, 401);
        Assert.True((await CheckClaims(c => c["nbf"] = now.AddSeconds(59).ToUnixTimeSeconds())).Succeeded);
        AssertRefused(await CheckClaims(c => c["nbf"] = now.AddSeconds(61).ToUnixTimeSeconds()), BearerFailure.NotYetValid, 401);
        // The skew is configurable: with none, one second past exp is expired.
        AssertRefused(await CheckClaims(c => c["exp"] = now.AddSeconds(-1).ToUnixTimeSeconds(), o => o.ClockSkewSeconds = 0), BearerFailure.Expired, 401);
    }

    // ---- no usable credential -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Token abc.def.ghi")]
    public async Task A_missing_or_non_bearer_credential_is_refused_as_missing(string? header)
        => AssertRefused(await Check(header), BearerFailure.MissingToken, 401);

    [Theory]
    [InlineData("Bearer not-a-jwt")]
    [InlineData("Bearer a.b")]
    [InlineData("Bearer a.b.c.d")]
    [InlineData("Bearer two tokens here")]
    [InlineData("Bearer eyJhbGciOiJSUzI1NiJ9.e30.abc, Bearer eyJhbGciOiJSUzI1NiJ9.e30.abc")]
    [InlineData("Bearer eyJ%hbGciOiJSUzI1NiJ9.e30.abc")]
    public async Task A_malformed_token_is_refused_without_touching_the_keys(string header)
    {
        var handler = _idp.Handler();
        var r = await Check(header, handler: handler);
        Assert.False(r.Succeeded);
        Assert.Equal(401, r.HttpStatus);
        Assert.Equal(0, handler.Requests);   // refused before any key fetch
    }

    [Fact]
    public async Task A_token_over_the_size_cap_is_refused_before_parsing()
        => AssertRefused(await Check("Bearer " + new string('a', BearerTokenValidator.MaxTokenLength + 1)), BearerFailure.TokenTooLarge, 401);

    [Fact]
    public async Task An_encrypted_five_part_token_is_refused()
        => Assert.False((await CheckToken("eyJhbGciOiJkaXIiLCJlbmMiOiJBMTI4Q0JDLUhTMjU2In0..AAAAAAAAAAAAAAAAAAAAAA.AAAA.AAAAAAAAAAAAAAAAAAAAAA")).Succeeded);

    // ---- algorithm and key confusion ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unsigned_alg_none_token_is_refused_before_any_key_fetch()
    {
        var handler = _idp.Handler();
        var r = await Check(TestIdp.Bearer(_idp.Mint(alg: "none")), handler: handler);
        AssertRefused(r, BearerFailure.UnsupportedAlgorithm, 401);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("NONE")]
    [InlineData("nOnE")]
    public async Task Alg_none_spelled_with_other_casing_is_refused(string alg)
        => AssertRefused(await CheckToken(_idp.Mint(alg: alg)), BearerFailure.UnsupportedAlgorithm, 401);

    [Fact]
    public async Task An_hs256_token_signed_with_the_public_key_as_the_secret_is_refused()
        => AssertRefused(await CheckToken(_idp.Mint(alg: "HS256")), BearerFailure.UnsupportedAlgorithm, 401);

    [Fact]
    public async Task A_correctly_signed_rs384_token_is_refused_because_only_rs256_is_issued()
        => AssertRefused(await CheckToken(_idp.Mint(alg: "RS384")), BearerFailure.UnsupportedAlgorithm, 401);

    [Fact]
    public async Task A_token_without_a_key_id_is_refused_and_no_key_is_tried()
    {
        var handler = _idp.Handler();
        AssertRefused(await Check(TestIdp.Bearer(_idp.Mint(kid: null)), handler: handler), BearerFailure.UnknownKey, 401);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task An_unknown_key_id_is_refused_as_401_after_one_refresh()
    {
        var handler = _idp.Handler();
        var r = await Check(TestIdp.Bearer(_idp.Mint(kid: "kid-never-published")), handler: handler);
        AssertRefused(r, BearerFailure.UnknownKey, 401);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_jku_or_embedded_jwk_header_is_not_followed()
    {
        using var attacker = RSA.Create(2048);
        var p = attacker.ExportParameters(false);
        var token = _idp.Mint(signWith: attacker, editHeader: h =>
        {
            h["jku"] = "https://attacker.example/keys";
            h["jwk"] = new JsonObject { ["kty"] = "RSA", ["n"] = TestIdp.B64(p.Modulus!), ["e"] = TestIdp.B64(p.Exponent!) };
        });
        AssertRefused(await CheckToken(token), BearerFailure.InvalidSignature, 401);
    }

    [Fact]
    public async Task A_signature_from_a_different_key_with_the_published_key_id_is_refused()
    {
        using var attacker = RSA.Create(2048);
        AssertRefused(await CheckToken(_idp.Mint(signWith: attacker)), BearerFailure.InvalidSignature, 401);
    }

    [Fact]
    public async Task A_tampered_payload_is_refused()
    {
        var good = _idp.Mint(_idp.Claims(c => c["roles"] = new JsonArray("Adr.Reader")));
        var forged = TestIdp.Tamper(good, 1, _ => TestIdp.PayloadWith(good, c => c["roles"] = new JsonArray("Adr.Admin")));
        AssertRefused(await CheckToken(forged), BearerFailure.InvalidSignature, 401);
    }

    [Fact]
    public async Task A_truncated_or_empty_signature_is_refused()
    {
        var good = _idp.Mint();
        Assert.False((await CheckToken(TestIdp.Tamper(good, 2, s => s[..^4]))).Succeeded);
        Assert.False((await CheckToken(TestIdp.Tamper(good, 2, _ => ""))).Succeeded);
    }

    // ---- who issued it, for whom, to which client ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_for_another_audience_is_refused()
        => AssertRefused(await CheckClaims(c => c["aud"] = TestIdp.OtherClientId), BearerFailure.WrongAudience, 401);

    [Fact]
    public async Task A_token_with_the_audience_missing_or_an_array_of_other_audiences_is_refused()
    {
        Assert.False((await CheckClaims(c => c.Remove("aud"))).Succeeded);
        Assert.False((await CheckClaims(c => c["aud"] = new JsonArray(TestIdp.OtherClientId, "api://other"))).Succeeded);
    }

    [Fact]
    public async Task A_token_from_another_tenants_issuer_is_refused()
        => AssertRefused(await CheckClaims(c => { c["iss"] = TestIdp.Issuer(TestIdp.OtherTenantId); c["tid"] = TestIdp.OtherTenantId; }), BearerFailure.WrongIssuer, 401);

    [Fact]
    public async Task A_v1_issuer_is_refused()
        => AssertRefused(await CheckClaims(c => c["iss"] = $"https://sts.windows.net/{TestIdp.TenantId}/"), BearerFailure.WrongIssuer, 401);

    [Theory]
    [InlineData("https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0/")]    // trailing slash: exact match only
    [InlineData("HTTPS://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0")]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com.attacker.example/11111111-1111-1111-1111-111111111111/v2.0")]
    public async Task The_issuer_must_match_exactly(string issuer)
        => AssertRefused(await CheckClaims(c => c["iss"] = issuer), BearerFailure.WrongIssuer, 401);

    [Fact]
    public async Task A_matching_issuer_with_the_wrong_tid_claim_is_refused_as_wrong_tenant()
        => AssertRefused(await CheckClaims(c => c["tid"] = TestIdp.OtherTenantId), BearerFailure.WrongTenant, 401);

    [Fact]
    public async Task A_token_without_a_tid_or_with_a_non_guid_tid_is_refused()
    {
        AssertRefused(await CheckClaims(c => c.Remove("tid")), BearerFailure.MissingClaim, 401);
        AssertRefused(await CheckClaims(c => c["tid"] = "contoso.onmicrosoft.com"), BearerFailure.WrongTenant, 401);
    }

    [Fact]
    public async Task A_v1_token_is_refused_even_when_everything_else_matches()
    {
        AssertRefused(await CheckClaims(c => c["ver"] = "1.0"), BearerFailure.UnsupportedVersion, 401);
        AssertRefused(await CheckClaims(c => c.Remove("ver")), BearerFailure.UnsupportedVersion, 401);
    }

    [Fact]
    public async Task A_client_that_is_not_on_the_allow_list_is_refused()
    {
        AssertRefused(await CheckClaims(c => c["azp"] = TestIdp.OtherClientId), BearerFailure.WrongClient, 401);
        AssertRefused(await CheckClaims(c => c.Remove("azp")), BearerFailure.MissingClaim, 401);
        Assert.True((await CheckClaims(c => c["azp"] = TestIdp.OtherClientId, o => o.AllowedClientIds = new[] { TestIdp.AllowedClientId, TestIdp.OtherClientId.ToUpperInvariant() })).Succeeded);
    }

    [Fact]
    public async Task A_token_without_an_object_id_is_refused()
    {
        AssertRefused(await CheckClaims(c => c.Remove("oid")), BearerFailure.MissingClaim, 401);
        AssertRefused(await CheckClaims(c => c["oid"] = "not-a-guid"), BearerFailure.MissingClaim, 401);
    }

    // ---- lifetime ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_expired_token_is_refused()
        => AssertRefused(await CheckClaims(c => c["exp"] = _idp.Clock.UtcNow.AddHours(-2).ToUnixTimeSeconds()), BearerFailure.Expired, 401);

    [Fact]
    public async Task A_token_that_is_not_valid_yet_is_refused()
        => AssertRefused(await CheckClaims(c => c["nbf"] = _idp.Clock.UtcNow.AddHours(1).ToUnixTimeSeconds()), BearerFailure.NotYetValid, 401);

    [Fact]
    public async Task A_token_without_exp_or_without_nbf_is_refused()
    {
        AssertRefused(await CheckClaims(c => c.Remove("exp")), BearerFailure.MissingClaim, 401);
        AssertRefused(await CheckClaims(c => c.Remove("nbf")), BearerFailure.MissingClaim, 401);
    }

    [Fact]
    public async Task Expiry_is_judged_by_the_injected_clock_not_the_machine_clock()
    {
        var token = _idp.Mint();
        var policy = _idp.Policy();
        var validator = _idp.Validator(policy, _idp.Handler());
        Assert.True((await validator.ValidateAsync(TestIdp.Bearer(token), default)).Succeeded);
        _idp.Clock.Advance(TimeSpan.FromMinutes(62));
        Assert.Equal(BearerFailure.Expired, (await validator.ValidateAsync(TestIdp.Bearer(token), default)).Failure);
    }

    // ---- scope and application tokens -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_delegated_token_without_the_required_scope_is_403()
    {
        AssertRefused(await CheckClaims(c => c["scp"] = "Files.Read"), BearerFailure.InsufficientScope, 403);
        AssertRefused(await CheckClaims(c => c["scp"] = "user_impersonation_extra"), BearerFailure.InsufficientScope, 403);
        AssertRefused(await CheckClaims(c => c["scp"] = "USER_IMPERSONATION"), BearerFailure.InsufficientScope, 403);   // scope values are case sensitive
    }

    [Fact]
    public async Task One_required_scope_among_several_granted_is_enough()
        => Assert.True((await CheckClaims(c => c["scp"] = "Files.Read user_impersonation openid")).Succeeded);

    [Fact]
    public async Task A_token_without_scp_is_refused_unless_application_tokens_are_allowed()
    {
        AssertRefused(await CheckClaims(c => c.Remove("scp")), BearerFailure.ApplicationTokenNotAllowed, 403);
        var allowed = await CheckClaims(c => c.Remove("scp"), o => o.AllowApplicationTokens = true);
        Assert.True(allowed.Succeeded);
        Assert.Empty(allowed.Principal!.Scopes);
    }

    [Fact]
    public async Task A_configured_scope_replaces_the_default()
    {
        AssertRefused(await CheckClaims(c => c["scp"] = "user_impersonation", o => o.RequiredScopes = new[] { "mcp.use" }), BearerFailure.InsufficientScope, 403);
        Assert.True((await CheckClaims(c => c["scp"] = "mcp.use", o => o.RequiredScopes = new[] { "mcp.use" })).Succeeded);
    }

    [Fact]
    public async Task Roles_are_passed_through_as_the_token_states_them_without_duplicates()
    {
        var r = await CheckClaims(c => c["roles"] = new JsonArray("Adr.Reader", "Adr.Author", "Adr.Reader"));
        Assert.Equal(new[] { "Adr.Reader", "Adr.Author" }, r.Principal!.Roles);
    }

    [Fact]
    public async Task A_claim_of_the_wrong_json_type_never_passes_for_a_valid_one()
    {
        Assert.False((await CheckClaims(c => c["tid"] = 12345)).Succeeded);
        Assert.False((await CheckClaims(c => c["azp"] = new JsonArray(TestIdp.AllowedClientId))).Succeeded);
        Assert.False((await CheckClaims(c => c["ver"] = 2.0)).Succeeded);
    }

    // ---- keys are unavailable: never accept ------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_key_set_cannot_be_fetched_the_answer_is_503_and_never_acceptance()
    {
        var handler = _idp.Handler();
        handler.Fail = true;
        AssertRefused(await Check(TestIdp.Bearer(_idp.Mint()), handler: handler), BearerFailure.KeysUnavailable, 503);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(302)]   // redirects are not followed
    public async Task A_non_success_answer_from_the_key_endpoint_is_503(int status)
    {
        var handler = _idp.Handler();
        handler.Override = _ => new HttpResponseMessage((System.Net.HttpStatusCode)status);
        AssertRefused(await Check(TestIdp.Bearer(_idp.Mint()), handler: handler), BearerFailure.KeysUnavailable, 503);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{\"keys\":[{\"kty\":\"oct\",\"kid\":\"kid-current\",\"k\":\"c2VjcmV0\"}]}")]
    public async Task A_key_document_without_a_usable_rsa_key_is_503(string document)
    {
        var handler = _idp.Handler();
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(document) };
        AssertRefused(await Check(TestIdp.Bearer(_idp.Mint()), handler: handler), BearerFailure.KeysUnavailable, 503);
    }

    [Fact]
    public async Task A_key_set_bigger_than_the_cap_is_503()
    {
        var handler = _idp.Handler();
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(new string(' ', JwksSigningKeyProvider.MaxDocumentBytes + 10) + _idp.Jwks()) };
        AssertRefused(await Check(TestIdp.Bearer(_idp.Mint()), handler: handler), BearerFailure.KeysUnavailable, 503);
    }

    [Fact]
    public async Task A_key_bound_to_another_issuer_is_not_used()
    {
        var handler = _idp.Handler();
        var other = _idp.Jwks((TestIdp.KeyId, _idp.Key, TestIdp.Issuer(TestIdp.OtherTenantId)));
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(other) };
        Assert.Equal(BearerFailure.KeysUnavailable, (await Check(TestIdp.Bearer(_idp.Mint()), handler: handler)).Failure);   // the only key was dropped
    }

    [Fact]
    public async Task A_key_bound_to_the_tenant_template_issuer_is_used()
    {
        var handler = _idp.Handler();
        var doc = _idp.Jwks((TestIdp.KeyId, _idp.Key, "https://login.microsoftonline.com/{tenantid}/v2.0"));
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(doc) };
        Assert.True((await Check(TestIdp.Bearer(_idp.Mint()), handler: handler)).Succeeded);
    }

    [Fact]
    public async Task A_weak_rsa_key_is_not_accepted()
    {
        using var weak = RSA.Create(1024);
        var handler = _idp.Handler();
        var doc = _idp.Jwks((TestIdp.KeyId, weak, null));
        handler.Override = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(doc) };
        Assert.False((await Check(TestIdp.Bearer(_idp.Mint(signWith: weak)), handler: handler)).Succeeded);
    }

    // ---- the disabled stand-in -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_disabled_validator_reports_itself_disabled_and_never_accepts()
    {
        var disabled = new DisabledBearerTokenValidator();
        Assert.False(disabled.Enabled);
        Assert.False((await disabled.ValidateAsync(TestIdp.Bearer(_idp.Mint()), default)).Succeeded);
    }
}
