using System.Text.RegularExpressions;

namespace VaultMcp.Core;

/// <summary>Short pieces of a note for the client's context window. Never the whole vault.</summary>
public static partial class Excerpt
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The first <paramref name="maxChars"/> characters, cut at a whitespace boundary where one is near.</summary>
    public static (string Text, bool Truncated) Head(string body, int maxChars)
    {
        var text = body.Trim();
        if (text.Length <= maxChars) return (text, false);
        var cut = text.LastIndexOf(' ', maxChars - 1, Math.Min(maxChars, 80));
        if (cut < maxChars / 2) cut = maxChars;
        return (text[..cut].TrimEnd() + " …", true);
    }

    /// <summary>A one-line window of about <paramref name="width"/> characters around <paramref name="index"/> (or the start when negative).</summary>
    public static string Around(string body, int index, int width)
    {
        if (body.Length == 0) return string.Empty;
        var start = index < 0 ? 0 : Math.Max(0, index - width / 3);
        var length = Math.Min(width, body.Length - start);
        var window = Whitespace().Replace(body.Substring(start, length), " ").Trim();
        if (start > 0) window = "… " + window;
        if (start + length < body.Length) window += " …";
        return window;
    }
}
