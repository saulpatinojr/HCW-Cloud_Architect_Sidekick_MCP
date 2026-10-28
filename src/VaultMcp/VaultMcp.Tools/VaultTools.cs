using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using VaultMcp.Core;

namespace VaultMcp.Tools;

/// <summary>The four tools. All read-only; there is no write tool by the owner's decision (ADR-0010). Each answers compact JSON.</summary>
[McpServerToolType]
public static class VaultTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "vault_search", ReadOnly = true, Idempotent = true)]
    [Description("Keyword search over the allow-listed folders of the Obsidian vault. Returns ranked notes with a one-line excerpt. Read-only.")]
    public static Task<string> Search(
        VaultService vault,
        [Description("Search words, two or more characters each; up to ten are used.")] string query,
        [Description("How many hits to return, 1 to 50. Default: max_results from adr-sidekick.md (10).")] int? take = null,
        CancellationToken ct = default)
        => Run(() => vault.SearchAsync(query, take, ct));

    [McpServerTool(Name = "vault_note_get", ReadOnly = true, Idempotent = true)]
    [Description("One note from an allow-listed folder, by path (Work/Decision.md) or by its unique name. Returns title, tags, links and a truncated excerpt. Read-only.")]
    public static Task<string> NoteGet(
        VaultService vault,
        [Description("Path relative to the vault root, or the note name.")] string path,
        [Description("Excerpt length in characters, 100 to 20000. Default: excerpt_chars from adr-sidekick.md (1200).")] int? maxChars = null,
        CancellationToken ct = default)
        => Run(() => vault.GetNoteAsync(path, maxChars, ct));

    [McpServerTool(Name = "vault_backlinks", ReadOnly = true, Idempotent = true)]
    [Description("Allow-listed notes that link to the given note with a [[wiki link]]. Read-only.")]
    public static Task<string> Backlinks(
        VaultService vault,
        [Description("Path relative to the vault root, or the note name.")] string path,
        [Description("How many notes to return, 1 to 50.")] int? take = null,
        CancellationToken ct = default)
        => Run(() => vault.BacklinksAsync(path, take, ct));

    [McpServerTool(Name = "vault_tags", ReadOnly = true, Idempotent = true)]
    [Description("Allow-listed notes carrying a tag; nested tags match their parent. Without a tag, the context tag from adr-sidekick.md (adr-context). Read-only.")]
    public static Task<string> Tags(
        VaultService vault,
        [Description("Tag with or without '#'. Omit for the context tag.")] string? tag = null,
        [Description("How many notes to return, 1 to 50.")] int? take = null,
        CancellationToken ct = default)
        => Run(() => vault.TagsAsync(tag, take, ct));

    private static async Task<string> Run<T>(Func<Task<T>> operation)
    {
        try
        {
            return JsonSerializer.Serialize(await operation().ConfigureAwait(false), Json);
        }
        catch (VaultException e)
        {
            return JsonSerializer.Serialize(new { error = new { code = e.Code, message = e.Message } }, Json);
        }
    }
}
