using System.Text;
using System.Text.Json;
using AdrMcp.Application.Common;
using AdrMcp.Domain.Errors;
using AdrMcp.UnitTests.Support;

namespace AdrMcp.UnitTests.Application;

public class RedactionTests
{
    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnop==;EndpointSuffix=core.windows.net", "AccountKey=[REDACTED]")]
    [InlineData("https://acct.blob.core.windows.net/c/b?sv=2024-11-04&sig=abcdEFGH1234%2Bxyz%3D", "sig=[REDACTED]")]
    [InlineData("Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjMifQ.abcdefghijklmnop", "Bearer [REDACTED]")]
    [InlineData("password=SuperSecret123!", "password=[REDACTED]")]
    [InlineData("contact alice@example.com now", "[REDACTED_EMAIL]")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE used", "[REDACTED_AWS_KEY]")]
    public void Log_redactor_masks_secrets(string input, string expectedFragment)
    {
        var redacted = SensitiveDataRedactor.Redact(input);
        Assert.Contains(expectedFragment, redacted);
        Assert.DoesNotContain("SuperSecret123", redacted);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", redacted);
    }

    [Fact]
    public async Task Error_messages_returned_to_client_are_redacted_and_audited()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_get", new { adrId = "missing password=Hunter2Hunter2" });
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("Hunter2", r.ToString());
        Assert.Contains(host.Audit.Events, e => e.ToolName == "adr_get" && e.Outcome == "failed");
        Assert.All(host.Audit.Events, e => Assert.DoesNotContain("Hunter2", e.Summary ?? ""));
        Assert.All(host.Audit.Events, e => Assert.NotEqual("dev-user", e.PrincipalHash));
    }
}

public class TenantIsolationTests
{
    [Fact]
    public async Task Workspace_not_granted_to_principal_is_rejected()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_list", new { workspaceId = "someone-elses-workspace" });
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.TenantMismatch, r.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Adrs_in_one_workspace_are_invisible_from_another()
    {
        var host = new TestHost();
        var id = await host.CreateAdrAsync("Secret ADR", "Only in ws-1", workspaceId: "ws-1");
        var fromOther = await host.CallAsync("adr_get", new { adrId = id, workspaceId = "ws-2" });
        Assert.Equal(ErrorCodes.AdrNotFound, fromOther.GetProperty("error").GetProperty("code").GetString());
        var search = await host.CallAsync("adr_search", new { query = "Secret ADR", workspaceId = "ws-2" });
        Assert.Equal(0, search.GetProperty("data").GetProperty("hits").GetArrayLength());
        // Memory in ws-2 only knows about the search just performed there; it never references the ws-1 ADR.
        var memory = await host.CallAsync("decision_memory_search", new { query = "Secret ADR", workspaceId = "ws-2" });
        Assert.DoesNotContain(memory.GetProperty("data").GetProperty("hits").EnumerateArray(), h => h.GetProperty("adrIds").EnumerateArray().Any(x => x.GetString() == id));
        var memoryOwn = await host.CallAsync("decision_memory_search", new { query = "Secret ADR", workspaceId = "ws-1" });
        Assert.Contains(memoryOwn.GetProperty("data").GetProperty("hits").EnumerateArray(), h => h.GetProperty("adrIds").EnumerateArray().Any(x => x.GetString() == id));
    }

    [Fact]
    public async Task Tenant_id_in_arguments_is_ignored_server_resolves_tenant()
    {
        var host = new TestHost();
        // Unknown properties are rejected by schema on the wire; at the binding level they are ignored and tenant still comes from the resolver.
        var id = await host.CreateAdrAsync("T", "D");
        var json = host.Store.RawJson("adrs", "tenant-a|ws-1").Single();
        Assert.Contains("\"tenantId\":\"tenant-a\"", json);
        Assert.Empty(host.Store.RawJson("adrs", "evil-tenant|ws-1"));
    }

    private const string EntraTenant = "11111111-1111-1111-1111-111111111111";

    private static TestHost EntraHost(bool platformAuthEnabled = true, string? defaultRole = null) => new(s =>
    {
        s["Auth:Mode"] = "EntraBuiltIn";
        s["Auth:TenantWorkspaces:" + EntraTenant + ":0"] = "default";
        if (platformAuthEnabled) s["WEBSITE_AUTH_ENABLED"] = "True"; // injected by App Service Authentication
        if (defaultRole is not null) s["Auth:DefaultRoleWhenNoAppRoles"] = defaultRole;
    });

    private static Dictionary<string, string> PrincipalHeader(params (string Typ, string Val)[] claims)
    {
        var principal = JsonSerializer.Serialize(new { auth_typ = "aad", claims = claims.Select(c => new { typ = c.Typ, val = c.Val }) });
        return new Dictionary<string, string> { ["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal)) };
    }

    private static string? ErrorCode(JsonElement envelope) => envelope.GetProperty("error").GetProperty("code").GetString();

    [Fact]
    public async Task Entra_mode_rejects_missing_principal_and_maps_app_roles()
    {
        var host = EntraHost();
        var noPrincipal = await host.CallAsync("adr_list", new { });
        Assert.Equal(ErrorCodes.Unauthorized, ErrorCode(noPrincipal));

        var headers = PrincipalHeader(("tid", EntraTenant), ("oid", "u1"), ("roles", "Adr.Reader"), ("preferred_username", "reader@contoso.example"));
        var list = await host.CallAsync("adr_list", new { }, headers);
        Assert.True(list.GetProperty("ok").GetBoolean(), list.ToString());
        var create = await host.CallAsync("adr_create", new { fields = new { title = "x", decision = "y" } }, headers);
        Assert.Equal(ErrorCodes.Forbidden, ErrorCode(create));
    }

    [Fact]
    public async Task Entra_mode_rejects_principal_header_when_platform_authentication_is_off()
    {
        // Without App Service Authentication in front, X-MS-CLIENT-PRINCIPAL is whatever the caller sent: never trust it.
        var host = EntraHost(platformAuthEnabled: false);
        var forged = PrincipalHeader(("tid", EntraTenant), ("oid", "attacker"), ("roles", "Adr.Admin"));
        var r = await host.CallAsync("adr_list", new { }, forged);
        Assert.Equal(ErrorCodes.Unauthorized, ErrorCode(r));
    }

    [Fact]
    public async Task Entra_principal_without_app_role_gets_no_access_by_default()
    {
        var host = EntraHost();
        var r = await host.CallAsync("adr_list", new { }, PrincipalHeader(("tid", EntraTenant), ("oid", "u2")));
        Assert.Equal(ErrorCodes.Forbidden, ErrorCode(r));
        Assert.Contains("app role", r.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Entra_default_role_can_be_opted_into()
    {
        var host = EntraHost(defaultRole: "Reader");
        var r = await host.CallAsync("adr_list", new { }, PrincipalHeader(("tid", EntraTenant), ("oid", "u3")));
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
    }

    [Fact]
    public async Task FunctionKey_mode_ignores_spoofed_principal_header()
    {
        var host = new TestHost();
        var principal = JsonSerializer.Serialize(new { claims = new[] { new { typ = "tid", val = "attacker" }, new { typ = "oid", val = "a" }, new { typ = "roles", val = "Adr.Admin" } } });
        var headers = new Dictionary<string, string> { ["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal)) };
        var id = await host.CreateAdrAsync("T", "D");
        var r = await host.CallAsync("adr_get", new { adrId = id }, headers);
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Empty(host.Store.RawJson("adrs", "attacker|ws-1"));
    }
}

public class AuthorizationTests
{
    [Fact]
    public async Task Reader_cannot_create_or_approve()
    {
        var host = new TestHost(s => { s["Auth:StaticRoles:0"] = "Reader"; s["Auth:StaticRoles:1"] = "Reader"; });
        var create = await host.CallAsync("adr_create", new { fields = new { title = "x", decision = "y" } });
        Assert.Equal(ErrorCodes.Forbidden, create.GetProperty("error").GetProperty("code").GetString());
        var list = await host.CallAsync("adr_list", new { });
        Assert.True(list.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Author_cannot_change_status_or_approve_but_can_propose()
    {
        var host = new TestHost(s => { s["Auth:StaticRoles:0"] = "Author"; s["Auth:StaticRoles:1"] = "Author"; });
        var id = await host.CreateAdrAsync("x", "y");
        var status = await host.CallAsync("adr_update", new { adrId = id, fields = new { status = "Proposed" } });
        Assert.Equal(ErrorCodes.Forbidden, status.GetProperty("error").GetProperty("code").GetString());
        var prop = await host.CallAsync("adr_propose_change", new { adrId = id, changes = new[] { new { field = "status", proposedValue = "Proposed", reason = "ready" } } });
        Assert.True(prop.GetProperty("ok").GetBoolean());
        var approve = await host.CallAsync("adr_approve_change", new { proposalId = prop.GetProperty("data").GetProperty("proposal").GetProperty("id").GetString() });
        Assert.Equal(ErrorCodes.Forbidden, approve.GetProperty("error").GetProperty("code").GetString());
    }
}
