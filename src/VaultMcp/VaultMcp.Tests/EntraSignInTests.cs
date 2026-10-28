using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using VaultMcp.Http;
using Xunit;

namespace VaultMcp.Tests;

/// <summary>
/// Entra mode of the HTTP host (ADR-0010 phase 2 step 3): the real pipeline with the tenant's key set replaced by a key made here,
/// so every refusal in the MCP challenge table can be proven offline. Nothing talks to Entra.
/// </summary>
public sealed class EntraSignInTests : IDisposable
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string ServerClient = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const string AllowedClient = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
    private const string OtherClient = "12121212-3434-5656-7878-909090909090";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private const string Resource = "http://localhost/mcp";   // http is accepted for loopback only; in Azure the template passes the https address
    private const string Metadata = "http://localhost/.well-known/oauth-protected-resource/mcp";
    private const string ClientScope = $"api://{ServerClient}/user_impersonation";

    private readonly TempVault _vault = new();
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly RsaSecurityKey _strangerKey = new(RSA.Create(2048)) { KeyId = "other-key" };

    private WebApplicationFactory<Program> Host(IConfigurationManager<OpenIdConnectConfiguration>? keys = null, params (string Key, string Value)[] more)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting(VaultHostOptions.PathKey, _vault.Root)
             .UseSetting(VaultHostOptions.ModeKey, "Entra")
             .UseSetting(VaultEntraOptions.TenantIdKey, Tenant.ToUpperInvariant())   // any GUID spelling is accepted and canonicalised
             .UseSetting(VaultEntraOptions.ClientIdKey, ServerClient)
             .UseSetting(VaultEntraOptions.AllowedClientIdsKey, $"{AllowedClient}, {OtherClient.Replace("1212", "ABAB")}")
             .UseSetting(VaultEntraOptions.ResourceUriKey, Resource);
            foreach (var (key, value) in more) b.UseSetting(key, value);
            b.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(EntraSignIn.Scheme, o =>
                o.ConfigurationManager = keys ?? new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration { Issuer = Issuer, SigningKeys = { _key } })));
        });

    private static HttpClient McpClient(WebApplicationFactory<Program> host, string? token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A token as Entra would issue it for the vault server, with every claim the checks need; each test bends one of them.</summary>
    private string Token(
        string? audience = ServerClient, string? issuer = Issuer, string? tid = Tenant, string? azp = AllowedClient, string? oid = "0f0f0f0f-1e1e-2d2d-3c3c-4b4b4b4b4b4b",
        string? ver = "2.0", string? scp = "user_impersonation", string[]? roles = null, bool withRoles = true, SecurityKey? key = null, string algorithm = SecurityAlgorithms.RsaSha256,
        DateTime? expires = null, DateTime? notBefore = null)
    {
        var claims = new Dictionary<string, object>();
        if (tid is not null) claims["tid"] = tid;
        if (azp is not null) claims["azp"] = azp;
        if (oid is not null) claims["oid"] = oid;
        if (ver is not null) claims["ver"] = ver;
        if (scp is not null) claims["scp"] = scp;
        if (withRoles) claims["roles"] = roles ?? new[] { "Vault.Reader" };
        claims["preferred_username"] = "owner@example.test";
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = notBefore ?? now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? _key, algorithm),
        });
    }

    [Fact]
    public async Task A_token_with_the_role_opens_the_tools_and_health_needs_none()
    {
        using var host = Host();
        using var client = McpClient(host, Token());

        var health = await host.CreateClient().GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("Entra", health.GetProperty("mode").GetString());

        var init = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        if (init.Headers.TryGetValues("Mcp-Session-Id", out var ids)) client.DefaultRequestHeaders.Add("Mcp-Session-Id", ids.First());
        await client.PostAsync("/mcp", JsonRpc("notifications/initialized", null, null));

        var list = await client.PostAsync("/mcp", JsonRpc("tools/list", 2, new { }));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var tools = (await ReadResultAsync(list)).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "vault_backlinks", "vault_note_get", "vault_search", "vault_tags" }, tools);

        var call = await client.PostAsync("/mcp", JsonRpc("tools/call", 3, new { name = "vault_note_get", arguments = new { path = "Work/Cosmos decision.md" } }));
        var text = (await ReadResultAsync(call)).GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Cosmos DB over Table Storage", text);
    }

    [Fact]
    public async Task No_token_gets_401_with_the_resource_metadata_and_the_scope_and_no_error()
    {
        using var host = Host();
        using var client = McpClient(host, null);
        var response = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.Single().ToString();
        Assert.Equal($"Bearer resource_metadata=\"{Metadata}\", scope=\"{ClientScope}\"", challenge);
        Assert.DoesNotContain("error", challenge);
        // The other verbs of the transport are behind the same policy (or not served at all): never 200 without a token.
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/mcp")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.DeleteAsync("/mcp")).StatusCode);
        // A credential that is not a bearer token is "no token", not "invalid".
        using var basic = host.CreateClient();
        basic.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "Zm9vOmJhcg==");
        var refused = await basic.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.DoesNotContain("error", refused.Headers.WwwAuthenticate.Single().ToString());
    }

    public static IEnumerable<object[]> InvalidTokens()
    {
        yield return new object[] { "wrong audience", (Func<EntraSignInTests, string>)(t => t.Token(audience: AllowedClient)) };
        yield return new object[] { "wrong issuer", (Func<EntraSignInTests, string>)(t => t.Token(issuer: $"https://sts.windows.net/{Tenant}/")) };
        yield return new object[] { "expired", (Func<EntraSignInTests, string>)(t => t.Token(expires: DateTime.UtcNow.AddMinutes(-5))) };
        yield return new object[] { "not yet valid", (Func<EntraSignInTests, string>)(t => t.Token(notBefore: DateTime.UtcNow.AddMinutes(5), expires: DateTime.UtcNow.AddMinutes(15))) };
        yield return new object[] { "stranger's key", (Func<EntraSignInTests, string>)(t => t.Token(key: t._strangerKey)) };
        yield return new object[] { "HMAC with public material", (Func<EntraSignInTests, string>)(t => t.Token(key: new SymmetricSecurityKey(t._key.Rsa.ExportRSAPublicKey()) { KeyId = "test-key" }, algorithm: SecurityAlgorithms.HmacSha256)) };
        yield return new object[] { "token version 1.0", (Func<EntraSignInTests, string>)(t => t.Token(ver: "1.0")) };
        yield return new object[] { "no version", (Func<EntraSignInTests, string>)(t => t.Token(ver: null)) };
        yield return new object[] { "another tenant", (Func<EntraSignInTests, string>)(t => t.Token(tid: ServerClient)) };
        yield return new object[] { "no tenant", (Func<EntraSignInTests, string>)(t => t.Token(tid: null)) };
        yield return new object[] { "client not allowed", (Func<EntraSignInTests, string>)(t => t.Token(azp: OtherClient)) };
        yield return new object[] { "no client", (Func<EntraSignInTests, string>)(t => t.Token(azp: null)) };
        yield return new object[] { "no object id", (Func<EntraSignInTests, string>)(t => t.Token(oid: null)) };
        yield return new object[] { "not a token", (Func<EntraSignInTests, string>)(_ => "not.a.token") };
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task A_token_that_is_not_valid_for_this_server_gets_401_invalid_token(string why, Func<EntraSignInTests, string> token)
    {
        using var host = Host();
        using var client = McpClient(host, token(this));
        var response = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.True(HttpStatusCode.Unauthorized == response.StatusCode, $"{why}: {response.StatusCode}");
        var challenge = response.Headers.WwwAuthenticate.Single().ToString();
        Assert.StartsWith("Bearer error=\"invalid_token\"", challenge);
        Assert.Contains($"resource_metadata=\"{Metadata}\"", challenge);
        Assert.Contains($"scope=\"{ClientScope}\"", challenge);
        Assert.DoesNotContain(Tenant, await response.Content.ReadAsStringAsync());   // the body never echoes a claim
    }

    [Theory]
    [InlineData(null)]                      // an application token: no scp at all
    [InlineData("Vault.Read.All openid")]   // a delegated token without the vault scope
    public async Task A_token_without_the_scope_gets_403_insufficient_scope(string? scp)
    {
        using var host = Host();
        using var client = McpClient(host, Token(scp: scp));
        var response = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.Single().ToString();
        Assert.StartsWith("Bearer error=\"insufficient_scope\"", challenge);
        Assert.Contains($"scope=\"{ClientScope}\"", challenge);
        Assert.Contains($"resource_metadata=\"{Metadata}\"", challenge);
    }

    [Theory]
    [InlineData(false, new string[0])]
    [InlineData(true, new string[0])]
    [InlineData(true, new[] { "Adr.Reader", "Vault.Admin" })]
    public async Task A_valid_token_without_the_vault_role_gets_403_and_no_challenge(bool withRoles, string[] roles)
    {
        using var host = Host();
        using var client = McpClient(host, Token(withRoles: withRoles, roles: roles));
        var response = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task The_required_role_is_a_setting_and_the_match_is_exact()
    {
        using var host = Host(more: (VaultEntraOptions.RequiredRoleKey, "Vault.Owner"));
        var accepted = await McpClient(host, Token(roles: new[] { "Vault.Owner" })).PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        foreach (var role in new[] { "Vault.Reader", "vault.owner", "Vault.Owner " })
        {
            var refused = await McpClient(host, Token(roles: new[] { role })).PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
    }

    [Fact]
    public async Task Keys_that_cannot_be_fetched_give_503_with_retry_after_never_acceptance()
    {
        using var host = Host(new FailingKeys());
        using var client = McpClient(host, Token());
        var response = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(EntraSignIn.RetryAfterSecondsOnUnavailable), response.Headers.RetryAfter!.Delta);
        Assert.Empty(response.Headers.WwwAuthenticate);
        // No token at all is still "no token": the keys are not needed to say so.
        var missing = await McpClient(host, null).PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
    }

    [Fact]
    public async Task The_protected_resource_metadata_names_this_server_entra_and_the_scope()
    {
        using var host = Host();
        using var client = host.CreateClient();
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-protected-resource/mcp/" })
        {
            var response = await client.GetAsync(path);
            Assert.True(HttpStatusCode.OK == response.StatusCode, path);
            Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
            Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            var document = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(Resource, document.GetProperty("resource").GetString());
            Assert.Equal(new[] { Issuer }, document.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(new[] { ClientScope }, document.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal(new[] { "header" }, document.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("Obsidian vault MCP server", document.GetProperty("resource_name").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/oauth-protected-resource/health")).StatusCode);

        // Without Entra mode there is nothing to discover.
        using var locked = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting(VaultHostOptions.PathKey, _vault.Root));
        Assert.Equal(HttpStatusCode.NotFound, (await locked.CreateClient().GetAsync("/.well-known/oauth-protected-resource")).StatusCode);
    }

    [Fact]
    public void The_settings_are_checked_at_start_and_never_echoed()
    {
        static IConfiguration Config(params (string Key, string? Value)[] pairs) => new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value)).Build();
        var good = new (string, string?)[]
        {
            (VaultHostOptions.PathKey, _vault.Root), (VaultHostOptions.ModeKey, "entra"),
            (VaultEntraOptions.TenantIdKey, Tenant), (VaultEntraOptions.ClientIdKey, ServerClient),
            (VaultEntraOptions.AllowedClientIdsKey, AllowedClient), (VaultEntraOptions.ResourceUriKey, "https://ca-vault-dev-scus-01.example.azurecontainerapps.io/mcp/"),
        };
        var options = VaultHostOptions.From(Config(good), _ => null);
        Assert.Equal(VaultAccessMode.Entra, options.Mode);
        var entra = options.Entra!;
        Assert.Equal(Tenant, entra.TenantId);
        Assert.Equal(new[] { AllowedClient }, entra.AllowedClientIds);
        Assert.Equal(VaultEntraOptions.DefaultRole, entra.RequiredRole);
        Assert.Equal("https://ca-vault-dev-scus-01.example.azurecontainerapps.io/mcp", entra.ResourceUri);   // trailing slash dropped: the challenge and the metadata must agree
        Assert.Equal("https://ca-vault-dev-scus-01.example.azurecontainerapps.io/.well-known/oauth-protected-resource/mcp", EntraSignIn.ResourceMetadataUrl(entra));
        Assert.Equal(Issuer, entra.Issuer);
        Assert.Equal(ClientScope, entra.ClientScope);

        // Entra mode is the one meant for Container Apps, so the marker does not refuse it, and the platform host name supplies the resource.
        var inAzure = VaultHostOptions.From(Config(good.Where(p => p.Item1 != VaultEntraOptions.ResourceUriKey).ToArray()),
            name => name switch { VaultHostOptions.ContainerAppsMarker => "ca-vault-dev-scus-01", VaultEntraOptions.ContainerAppsHostNameMarker => "CA-vault-dev-scus-01.proudsky-1a2b3c4d.southcentralus.azurecontainerapps.io", _ => null });
        Assert.Equal("https://ca-vault-dev-scus-01.proudsky-1a2b3c4d.southcentralus.azurecontainerapps.io/mcp", inAzure.Entra!.ResourceUri);

        // Indexed children work too (Vault__Auth__Entra__AllowedClientIds__0).
        var indexed = good.Where(p => p.Item1 != VaultEntraOptions.AllowedClientIdsKey).Append((VaultEntraOptions.AllowedClientIdsKey + ":0", AllowedClient)).Append((VaultEntraOptions.AllowedClientIdsKey + ":1", OtherClient)).ToArray();
        Assert.Equal(2, VaultHostOptions.From(Config(indexed), _ => null).Entra!.AllowedClientIds.Count);

        var bad = new (string Key, string? Value, string Expect)[]
        {
            (VaultEntraOptions.TenantIdKey, null, "TenantId"),
            (VaultEntraOptions.TenantIdKey, "contoso.onmicrosoft.com", "TenantId"),
            (VaultEntraOptions.TenantIdKey, Guid.Empty.ToString(), "TenantId"),
            (VaultEntraOptions.ClientIdKey, null, "ClientId"),
            (VaultEntraOptions.ClientIdKey, "api://" + ServerClient, "ClientId"),
            (VaultEntraOptions.AllowedClientIdsKey, null, "AllowedClientIds"),
            (VaultEntraOptions.AllowedClientIdsKey, AllowedClient + ",*", "AllowedClientIds entry 1"),
            (VaultEntraOptions.ResourceUriKey, null, "ResourceUri"),
            (VaultEntraOptions.ResourceUriKey, "http://ca-vault-dev-scus-01.example.azurecontainerapps.io/mcp", "ResourceUri"),
            (VaultEntraOptions.ResourceUriKey, "https://example.test/mcp?x=1", "ResourceUri"),
            (VaultEntraOptions.ResourceUriKey, "https://user:pw@example.test/mcp", "ResourceUri"),
            (VaultEntraOptions.RequiredRoleKey, "Vault Reader", "RequiredRole"),
            (VaultEntraOptions.RequiredRoleKey, "user_impersonation", "must differ"),
            (VaultEntraOptions.ScopeKey, "a b", "Scope"),
            (VaultEntraOptions.AuthorityHostKey, "https://login.microsoftonline.com", "AuthorityHost"),
        };
        foreach (var (key, value, expect) in bad)
        {
            var pairs = good.Where(p => p.Item1 != key).Append((key, value)).ToArray();
            var error = Assert.Throws<InvalidOperationException>(() => VaultHostOptions.From(Config(pairs), _ => null));
            Assert.Contains(expect, error.Message);
            if (value is not null && value.Length > 3) Assert.DoesNotContain(value, error.Message);   // the message names the rule, never the value
        }
    }

    private sealed class FailingKeys : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => throw new InvalidOperationException("IDX20803: Unable to obtain configuration (simulated).");
        public void RequestRefresh() { }
    }

    private static object InitializeParams() => new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "tests", version = "0" } };

    private static StringContent JsonRpc(string method, int? id, object? @params)
    {
        var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) message["id"] = id;
        if (@params is not null) message["params"] = @params;
        return new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            foreach (var line in body.Split('\n'))
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var doc = JsonDocument.Parse(line[5..].Trim());
                if (doc.RootElement.TryGetProperty("result", out var result)) return result.Clone();
                if (doc.RootElement.TryGetProperty("error", out var error)) throw new Xunit.Sdk.XunitException("JSON-RPC error: " + error);
            }
            throw new Xunit.Sdk.XunitException("no result in SSE body: " + body);
        }
        var json = JsonDocument.Parse(body).RootElement;
        if (json.TryGetProperty("error", out var err)) throw new Xunit.Sdk.XunitException("JSON-RPC error: " + err);
        return json.GetProperty("result").Clone();
    }

    public void Dispose()
    {
        _vault.Dispose();
        _key.Rsa.Dispose();
        _strangerKey.Rsa.Dispose();
    }
}
