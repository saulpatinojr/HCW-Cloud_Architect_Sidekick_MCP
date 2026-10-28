using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using VaultMcp.Core;
using VaultMcp.Stdio;
using Xunit;

namespace VaultMcp.Tests;

/// <summary>ADR-0010 gate 1: the tool list shows exactly the four read tools and nothing that writes.</summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected = ["vault_search", "vault_note_get", "vault_backlinks", "vault_tags"];

    private static IReadOnlyList<McpServerToolAttribute> Tools() =>
        typeof(VaultTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

    [Fact]
    public void Exactly_the_four_read_tools_are_declared()
    {
        var names = Tools().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(Expected.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public void Every_tool_is_marked_read_only_and_none_sounds_like_a_write()
    {
        foreach (var tool in Tools())
        {
            Assert.True(tool.ReadOnly, tool.Name);
            Assert.DoesNotMatch("write|create|update|delete|remove|append|move|rename", tool.Name!);
        }
    }

    [Fact]
    public async Task A_refusal_is_answered_as_an_error_object_not_an_exception()
    {
        using var vault = new TempVault();
        var json = await VaultTools.NoteGet(vault.Service(), "Personal/Diary.md", null, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_hit_is_answered_in_camel_case_json()
    {
        using var vault = new TempVault();
        var json = await VaultTools.Search(vault.Service(), "cosmos", 1, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("hits").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("hits")[0].TryGetProperty("matchedTerms", out _));
        Assert.False(doc.RootElement.TryGetProperty("note", out _), "null fields are omitted");
    }
}
