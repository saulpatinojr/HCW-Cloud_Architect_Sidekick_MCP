using System.Text.RegularExpressions;

namespace AdrMcp.Application.Common;

/// <summary>Deterministic keyword extraction for search and decision memory. No model involved.</summary>
public static partial class KeywordExtractor
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","of","to","in","for","on","with","by","is","are","was","were","be","as","at","this","that","it","we","will","from","our","their","its","use","using","used","not","no","all","any","can","should","must","may","have","has","had","than","then","so","if","into","over","per","via","also","which","who","what","when","where","how","do","does","done","adr","decision"
    };

    public static IReadOnlyList<string> Extract(string? text, int max = 32)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Token().Matches(text))
        {
            var t = m.Value.ToLowerInvariant();
            if (t.Length < 3 || t.Length > 40 || StopWords.Contains(t)) continue;
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(max).Select(kv => kv.Key).ToList();
    }

    public static IReadOnlyList<string> Tokens(string? text) => Extract(text, 256);

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+#.\-]{1,}")] private static partial Regex Token();
}
