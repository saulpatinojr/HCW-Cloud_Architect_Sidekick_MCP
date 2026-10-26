using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Application.Abstractions;
using AdrMcp.Functions.Http;
using AdrMcp.Infrastructure.Auth;
using AdrMcp.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdrMcp.UnitTests.Auth;

/// <summary>
/// The two HTTP functions the app owns end to end: the protected resource metadata document and the sign-in check. Here the app
/// really does set the status and the WWW-Authenticate header, so the spec's challenge text is asserted on the wire values.
/// </summary>
public class AuthEndpointTests : IDisposable
{
    private const string Prm = "https://func-adr-test.azurewebsites.net/.well-known/oauth-protected-resource/runtime/webhooks/mcp";
    private static readonly string Scope = $"api://{TestIdp.ServerClientId}/user_impersonation";

    private readonly TestIdp _idp = new();
    private readonly StubJwksHandler _jwks;

    public AuthEndpointTests() => _jwks = _idp.Handler();

    public void Dispose() => _idp.Dispose();

    private TestHost Host(bool enabled = true) => new(s =>
    {
        s["Auth:Mode"] = "EntraBuiltIn";
        s["Auth:Tenants:0:Id"] = TestIdp.TenantId;
        if (!enabled) return;
        s["Auth:Entra:ValidateBearerToken"] = "true";
        s["Auth:Entra:TenantId"] = TestIdp.TenantId;
        s["Auth:Entra:ClientId"] = TestIdp.ServerClientId;
        s["Auth:Entra:AllowedClientIds:0"] = TestIdp.AllowedClientId;
        s["Auth:Entra:ResourceUri"] = TestIdp.ResourceUri;
    }, services =>
    {
        services.AddSingleton<IClock>(_idp.Clock);
        services.AddSingleton<ISigningKeyProvider>(sp => new JwksSigningKeyProvider(sp.GetRequiredService<EntraTokenPolicy>(), new HttpClient(_jwks), _idp.Clock, sp.GetRequiredService<ILogger<JwksSigningKeyProvider>>()));
    });

    private static (int Status, string Body, IHeaderDictionary Headers) Run(IActionResult result, HttpContext context)
    {
        if (result is NotFoundResult) return (404, "", context.Response.Headers);
        var content = Assert.IsType<ContentResult>(result);
        return (content.StatusCode ?? 200, content.Content ?? "", context.Response.Headers);
    }

    private async Task<(int Status, string Body, IHeaderDictionary Headers)> Check(TestHost host, string? authorization)
    {
        var function = ActivatorUtilities.CreateInstance<AuthCheckFunction>(host.Services);
        var context = new DefaultHttpContext();
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        return Run(await function.Run(context.Request, default), context);
    }

    private (int Status, string Body, IHeaderDictionary Headers) Metadata(TestHost host, string? path)
    {
        var function = ActivatorUtilities.CreateInstance<ProtectedResourceMetadataFunction>(host.Services);
        var context = new DefaultHttpContext();
        return Run(function.Run(context.Request, path), context);
    }

    // ---- /api/auth/check ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_token_is_401_with_the_challenge_that_points_clients_at_the_metadata()
    {
        var (status, body, headers) = await Check(Host(), null);
        Assert.Equal(401, status);
        Assert.Equal($"Bearer resource_metadata=\"{Prm}\", scope=\"{Scope}\"", headers.WWWAuthenticate.ToString());
        Assert.Equal("unauthorized", JsonNode.Parse(body)!["error"]!.GetValue<string>());
        Assert.Equal("no-store", headers.CacheControl.ToString());
    }

    [Fact]
    public async Task A_bad_token_is_401_invalid_token()
    {
        var (status, _, headers) = await Check(Host(), TestIdp.Bearer(_idp.Mint(_idp.Claims(c => c["aud"] = TestIdp.OtherClientId))));
        Assert.Equal(401, status);
        Assert.Equal($"Bearer error=\"invalid_token\", error_description=\"The access token is not valid for this server.\", resource_metadata=\"{Prm}\", scope=\"{Scope}\"", headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task A_valid_token_is_200_and_reports_only_what_the_token_already_says()
    {
        var (status, body, headers) = await Check(Host(), TestIdp.Bearer(_idp.Mint()));
        Assert.Equal(200, status);
        Assert.False(headers.ContainsKey("WWW-Authenticate"));
        var json = JsonNode.Parse(body)!;
        Assert.Equal("ok", json["status"]!.GetValue<string>());
        Assert.Equal(new[] { "Admin" }, json["roles"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "user_impersonation" }, json["scopes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(3600, json["expiresInSeconds"]!.GetValue<long>());   // the test token expires 60 minutes after the fixed test clock
        Assert.DoesNotContain(TestIdp.ObjectId, body);                     // no identifier of the caller is echoed
        Assert.DoesNotContain(TestIdp.UserName, body);
    }

    [Fact]
    public async Task A_valid_token_without_an_adr_role_is_a_plain_403_with_no_challenge()
    {
        var (status, body, headers) = await Check(Host(), TestIdp.Bearer(_idp.Mint(_idp.Claims(c => c.Remove("roles")))));
        Assert.Equal(403, status);
        Assert.False(headers.ContainsKey("WWW-Authenticate"));
        Assert.Equal("forbidden", JsonNode.Parse(body)!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_wrong_scope_is_403_and_names_the_scope_to_request()
    {
        var (status, _, headers) = await Check(Host(), TestIdp.Bearer(_idp.Mint(_idp.Claims(c => c["scp"] = "Files.Read"))));
        Assert.Equal(403, status);
        Assert.Contains($"scope=\"{Scope}\"", headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task A_key_outage_is_503_with_retry_after_and_no_challenge()
    {
        _jwks.Fail = true;
        var (status, _, headers) = await Check(Host(), TestIdp.Bearer(_idp.Mint()));
        Assert.Equal(503, status);
        Assert.Equal("30", headers.RetryAfter.ToString());
        Assert.False(headers.ContainsKey("WWW-Authenticate"));
    }

    [Fact]
    public async Task The_check_does_not_exist_while_validation_is_off()
        => Assert.Equal(404, (await Check(Host(enabled: false), TestIdp.Bearer(_idp.Mint()))).Status);

    // ---- protected resource metadata ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("runtime/webhooks/mcp")]
    [InlineData("/runtime/webhooks/mcp/")]
    public void The_metadata_is_served_at_the_root_and_the_path_inserted_address(string? path)
    {
        var (status, body, headers) = Metadata(Host(), path);
        Assert.Equal(200, status);
        var json = JsonNode.Parse(body)!;
        Assert.Equal(TestIdp.ResourceUri, json["resource"]!.GetValue<string>());
        Assert.Equal(new[] { TestIdp.Issuer() }, json["authorization_servers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { Scope }, json["scopes_supported"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "header" }, json["bearer_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("*", headers.AccessControlAllowOrigin.ToString());
        Assert.Equal("public, max-age=300", headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("runtime/webhooks/other")]
    [InlineData("mcp")]
    [InlineData("runtime/webhooks/mcp/extra")]
    [InlineData("../admin")]
    public void The_metadata_for_any_other_resource_path_is_404(string path)
        => Assert.Equal(404, Metadata(Host(), path).Status);

    [Fact]
    public void The_metadata_does_not_exist_while_validation_is_off()
        => Assert.Equal(404, Metadata(Host(enabled: false), null).Status);

    [Fact]
    public void The_metadata_names_the_same_resource_and_scope_the_challenge_uses()
    {
        var host = Host();
        var policy = host.Services.GetRequiredService<EntraTokenPolicy>();
        var document = JsonSerializer.Deserialize<JsonElement>(Metadata(host, null).Body);
        Assert.Equal(policy.ResourceUri, document.GetProperty("resource").GetString());
        Assert.Equal(policy.ClientScope, document.GetProperty("scopes_supported")[0].GetString());
        Assert.Equal(Prm, ProtectedResourceMetadata.MetadataUrl(policy.ResourceUri));
    }

    [Fact]
    public void The_health_route_is_spelled_out_because_the_route_prefix_is_empty()
    {
        var route = typeof(HealthFunction).GetMethod(nameof(HealthFunction.Run))!.GetParameters()[0]
            .GetCustomAttributes(typeof(Microsoft.Azure.Functions.Worker.HttpTriggerAttribute), false).Cast<Microsoft.Azure.Functions.Worker.HttpTriggerAttribute>().Single().Route;
        Assert.Equal("api/health", route);
    }
}
