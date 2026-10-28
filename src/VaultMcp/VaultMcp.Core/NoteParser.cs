using System.Text.RegularExpressions;

namespace VaultMcp.Core;

/// <summary>Turns a Markdown file into a <see cref="VaultNote"/>: title, tags (front matter and inline), wiki links, body.</summary>
public static partial class NoteParser
{
    // [[Target]], [[Target|alias]], [[Target#heading]], [[folder/Target]]. Group 1 is the target.
    [GeneratedRegex(@"\[\[([^\[\]\|#]+)(?:#[^\[\]\|]*)?(?:\|[^\[\]]*)?\]\]")]
    private static partial Regex WikiLink();

    // #tag or #nested/tag, not preceded by a word character or '/' (so URLs and headings are not tags).
    [GeneratedRegex(@"(?<![\p{L}\p{N}_/#])#([\p{L}_][\p{L}\p{N}_/-]*)")]
    private static partial Regex InlineTag();

    [GeneratedRegex(@"^#\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading1();

    // Fenced blocks and inline code: tags and links inside code are not tags and links.
    [GeneratedRegex(@"```[\s\S]*?```|`[^`\n]*`")]
    private static partial Regex Code();

    public static VaultNote Parse(VaultFile file, string text)
    {
        var (front, body) = FrontMatter.Split(text);
        var prose = Code().Replace(body, " ");

        var title = front.Scalar("title");
        if (string.IsNullOrWhiteSpace(title))
        {
            var h1 = Heading1().Match(prose);
            title = h1.Success ? h1.Groups[1].Value : Path.GetFileNameWithoutExtension(file.Path);
        }

        var tags = new List<string>();
        foreach (var raw in front.List("tags").Concat(InlineTag().Matches(prose).Select(m => m.Groups[1].Value)))
        {
            var tag = NormalizeTag(raw);
            if (tag is not null && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }

        var links = new List<string>();
        foreach (Match m in WikiLink().Matches(prose))
        {
            var target = m.Groups[1].Value.Trim();
            if (target.Length > 0 && !links.Contains(target, StringComparer.OrdinalIgnoreCase)) links.Add(target);
        }

        return new VaultNote(file.Path, title, tags, links, file.ModifiedUtc, body);
    }

    /// <summary>Strips a leading '#' and whitespace; null for an empty tag.</summary>
    public static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('#').Trim();
        return t.Length == 0 ? null : t;
    }
}
