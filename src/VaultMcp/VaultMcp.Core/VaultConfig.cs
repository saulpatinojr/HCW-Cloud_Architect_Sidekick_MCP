namespace VaultMcp.Core;

/// <summary>
/// Settings read from the note <c>adr-sidekick.md</c> at the vault root, so Obsidian Sync carries them to every machine (ADR-0010).
/// A missing or unreadable note gives the empty configuration: nothing is readable until the note exists.
/// </summary>
/// <example>
/// ---
/// allow:
///   - Work
///   - Projects/Cloud
/// tag: adr-context
/// excerpt_chars: 1200
/// max_results: 10
/// ---
/// </example>
public sealed record VaultConfig(IReadOnlyList<string> AllowedFolders, string ContextTag, int ExcerptChars, int MaxResults)
{
    public const string FileName = "adr-sidekick.md";

    public static VaultConfig Empty { get; } = new(Array.Empty<string>(), "adr-context", 1200, 10);

    /// <summary>Folder match is exact and case sensitive: <c>Work</c> and <c>work</c> are two folders on a case-sensitive disk, and the list must not widen by accident.</summary>
    public bool IsAllowed(string normalizedPath)
    {
        foreach (var folder in AllowedFolders)
        {
            if (normalizedPath.StartsWith(folder + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static VaultConfig Parse(string? text)
    {
        if (text is null) return Empty;
        var (front, _) = FrontMatter.Split(text);
        var folders = new List<string>();
        foreach (var raw in front.List("allow"))
        {
            var folder = VaultPath.Normalize(raw);
            if (folder is null || VaultPath.IsHidden(folder) || folders.Contains(folder, StringComparer.Ordinal)) continue;
            folders.Add(folder);
        }
        var tag = NoteParser.NormalizeTag(front.Scalar("tag")) ?? Empty.ContextTag;
        var excerpt = Math.Clamp(front.Int("excerpt_chars") ?? Empty.ExcerptChars, 200, 20_000);
        var max = Math.Clamp(front.Int("max_results") ?? Empty.MaxResults, 1, 50);
        return new VaultConfig(folders, tag, excerpt, max);
    }
}
