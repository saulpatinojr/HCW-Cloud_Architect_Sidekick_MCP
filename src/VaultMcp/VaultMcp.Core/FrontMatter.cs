using System.Globalization;

namespace VaultMcp.Core;

/// <summary>
/// The small subset of YAML that Obsidian front matter uses: <c>key: value</c>, <c>key: [a, b]</c> and
/// <c>key:</c> followed by <c>- item</c> lines. Anything else is ignored rather than guessed at.
/// </summary>
public sealed class FrontMatter
{
    private readonly Dictionary<string, string> _scalars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _lists = new(StringComparer.OrdinalIgnoreCase);

    public static FrontMatter Empty { get; } = new();

    public string? Scalar(string key) => _scalars.TryGetValue(key, out var s) ? s : null;

    public int? Int(string key) =>
        int.TryParse(Scalar(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    /// <summary>A list value; a scalar written as <c>a, b</c> or <c>[a, b]</c> counts as a list too.</summary>
    public IReadOnlyList<string> List(string key)
    {
        if (_lists.TryGetValue(key, out var list)) return list;
        if (_scalars.TryGetValue(key, out var scalar)) return SplitInline(scalar);
        return Array.Empty<string>();
    }

    /// <summary>Splits a note into its front matter (if the text starts with a <c>---</c> line) and the body after it.</summary>
    public static (FrontMatter Front, string Body) Split(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r').Trim() != "---") return (Empty, text);
        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "---") { end = i; break; }
        }
        if (end < 0) return (Empty, text);
        var front = Parse(lines.AsSpan(1, end - 1));
        var body = string.Join('\n', lines, end + 1, lines.Length - end - 1);
        return (front, body);
    }

    private static FrontMatter Parse(ReadOnlySpan<string> lines)
    {
        var fm = new FrontMatter();
        string? openList = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var trimmed = line.TrimStart();
            // YAML allows the items of a block list to sit flush with the key, so indentation is not required.
            if (openList is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var item = Unquote(trimmed[2..]);
                if (item.Length > 0) fm._lists[openList].Add(item);
                continue;
            }
            openList = null;
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[0] is ' ' or '\t') continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0) continue;
            if (value.Length == 0)
            {
                openList = key;
                fm._lists[key] = new List<string>();
                fm._scalars.Remove(key);
            }
            else
            {
                fm._scalars[key] = Unquote(value);
                fm._lists.Remove(key);
            }
        }
        return fm;
    }

    private static List<string> SplitInline(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('[') && v.EndsWith(']')) v = v[1..^1];
        var result = new List<string>();
        foreach (var part in v.Split(','))
        {
            var item = Unquote(part);
            if (item.Length > 0) result.Add(item);
        }
        return result;
    }

    private static string Unquote(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) v = v[1..^1];
        return v.Trim();
    }
}
