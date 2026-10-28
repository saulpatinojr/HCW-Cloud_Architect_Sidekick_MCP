using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using VaultMcp.Http;
using Xunit;

namespace VaultMcp.Tests;

/// <summary>The HTTP host (ADR-0010 phase 2): locked by default, the four tools over the streamable HTTP transport when opened.</summary>
public sealed class HttpHostTests : IDisposable
{
    private readonly TempVault _vault = new();

    // UseSetting lands in the host configuration, which Program can read before Build(); ConfigureAppConfiguration would be too late.
    private WebApplicationFactory<Program> Host(string mode) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseSetting(VaultHostOptions.PathKey, _vault.Root).UseSetting(VaultHostOptions.ModeKey, mode));

    [Fact]
    public async Task Locked_is_the_default_and_refuses_the_mcp_endpoint_but_answers_health()
    {
        using var host = Host("Locked");
        using var client = host.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal("Locked", health.GetProperty("mode").GetString());
        Assert.True(health.GetProperty("settingsNote").GetBoolean());

        var refused = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("Bearer", refused.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Anonymous_serves_exactly_the_four_read_tools_over_http()
    {
        using var host = Host("Anonymous");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var init = await client.PostAsync("/mcp", JsonRpc("initialize", 1, InitializeParams()));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        var initResult = await ReadResultAsync(init);
        Assert.True(initResult.TryGetProperty("serverInfo", out _));
        if (init.Headers.TryGetValues("Mcp-Session-Id", out var ids))
        {
            client.DefaultRequestHeaders.Add("Mcp-Session-Id", ids.First());
        }

        var notified = await client.PostAsync("/mcp", JsonRpc("notifications/initialized", null, null));
        Assert.True(notified.IsSuccessStatusCode, notified.StatusCode.ToString());

        var list = await client.PostAsync("/mcp", JsonRpc("tools/list", 2, new { }));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var tools = (await ReadResultAsync(list)).GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(new[] { "vault_backlinks", "vault_note_get", "vault_search", "vault_tags" }, tools.Select(t => t.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(tools, t => Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), t.GetProperty("name").GetString()));

        var call = await client.PostAsync("/mcp", JsonRpc("tools/call", 3, new { name = "vault_note_get", arguments = new { path = "Personal/Diary.md" } }));
        var text = (await ReadResultAsync(call)).GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Equal("NOT_FOUND", JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Anonymous_is_refused_inside_container_apps_and_a_bad_mode_stops_the_host()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [VaultHostOptions.PathKey] = _vault.Root,
            [VaultHostOptions.ModeKey] = "Anonymous",
        }).Build();
        var inAzure = Assert.Throws<InvalidOperationException>(() => VaultHostOptions.From(config, name => name == VaultHostOptions.ContainerAppsMarker ? "ca-vault-dev-scus-01" : null));
        Assert.Contains("Container Apps", inAzure.Message);
        Assert.Equal(VaultAccessMode.Anonymous, VaultHostOptions.From(config, _ => null).Mode);

        // Names only: a number would parse as an enum value, and an undefined one would skip the Locked check.
        foreach (var value in new[] { "Open", "2", "0", "1", "Locked,Anonymous" })
        {
            var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [VaultHostOptions.PathKey] = _vault.Root, [VaultHostOptions.ModeKey] = value }).Build();
            Assert.Throws<InvalidOperationException>(() => VaultHostOptions.From(bad, _ => null));
        }
        var spaced = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [VaultHostOptions.PathKey] = _vault.Root, [VaultHostOptions.ModeKey] = " locked " }).Build();
        Assert.Equal(VaultAccessMode.Locked, VaultHostOptions.From(spaced, _ => null).Mode);

        var noPath = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => VaultHostOptions.From(noPath, _ => null));
        Assert.Equal(VaultAccessMode.Locked, VaultHostOptions.From(noPath, name => name == "VAULT_PATH" ? _vault.Root : null).Mode);
    }

    private static object InitializeParams() => new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "tests", version = "0" } };

    private static StringContent JsonRpc(string method, int? id, object? @params)
    {
        var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) message["id"] = id;
        if (@params is not null) message["params"] = @params;
        return new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json");
    }

    /// <summary>The streamable HTTP transport may answer with plain JSON or with an SSE stream; take the JSON-RPC result either way.</summary>
    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == "text/event-stream")
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

    public void Dispose() => _vault.Dispose();
}
