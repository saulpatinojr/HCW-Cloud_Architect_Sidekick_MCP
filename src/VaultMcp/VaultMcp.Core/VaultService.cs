namespace VaultMcp.Core;

public sealed record SearchHit(string Path, string Title, double Score, IReadOnlyList<string> MatchedTerms, string Excerpt, DateTimeOffset ModifiedUtc);
public sealed record SearchResult(string Query, int Total, IReadOnlyList<SearchHit> Hits, string? Note);
public sealed record NoteResult(string Path, string Title, IReadOnlyList<string> Tags, IReadOnlyList<string> Links, DateTimeOffset ModifiedUtc, string Excerpt, bool Truncated, int TotalChars);
public sealed record NoteSummary(string Path, string Title, IReadOnlyList<string> Tags, DateTimeOffset ModifiedUtc);
public sealed record ListResult(string Subject, int Total, IReadOnlyList<NoteSummary> Notes);

/// <summary>The four read-only operations. Everything works on the index, which holds allow-listed notes only, so nothing else can be returned.</summary>
public sealed class VaultService
{
    private const int MaxTake = 50;
    private const int MaxTerms = 10;
    private readonly VaultIndex _index;

    public VaultService(VaultIndex index) => _index = index;

    /// <summary>Keyword search: title matches score 3, tag matches 2, body occurrences up to 5 at 0.5 each plus 1; all terms present multiplies by 1.5.</summary>
    public async Task<SearchResult> SearchAsync(string? query, int? take, CancellationToken ct)
    {
        var terms = Terms(query);
        if (terms.Count == 0) throw new VaultException("INVALID_QUERY", "Give at least one search word of two or more characters.");
        var s = await _index.GetAsync(ct).ConfigureAwait(false);
        var n = Math.Clamp(take ?? s.Config.MaxResults, 1, MaxTake);

        var hits = new List<SearchHit>();
        foreach (var note in s.Notes)
        {
            double score = 0;
            var matched = new List<string>();
            var firstIndex = -1;
            foreach (var term in terms)
            {
                var inTitle = note.Title.Contains(term, StringComparison.OrdinalIgnoreCase);
                var inTag = note.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));
                var count = Count(note.Body, term, out var index);
                if (!inTitle && !inTag && count == 0) continue;
                matched.Add(term);
                score += (inTitle ? 3 : 0) + (inTag ? 2 : 0) + (count > 0 ? 1 + Math.Min(count, 5) * 0.5 : 0);
                if (index >= 0 && (firstIndex < 0 || index < firstIndex)) firstIndex = index;
            }
            if (matched.Count == 0) continue;
            if (matched.Count == terms.Count && terms.Count > 1) score *= 1.5;
            hits.Add(new SearchHit(note.Path, note.Title, Math.Round(score, 2), matched, Excerpt.Around(note.Body, firstIndex, 240), note.ModifiedUtc));
        }

        var ordered = hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.ModifiedUtc)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .ToList();
        var advice = s.Config.AllowedFolders.Count == 0
            ? $"No folder is allow-listed. Add an `allow:` list to the front matter of {VaultConfig.FileName} at the vault root."
            : null;
        return new SearchResult(string.Join(' ', terms), ordered.Count, ordered.Take(n).ToList(), advice);
    }

    /// <summary>One note by path, or by name when the name is unique among allow-listed notes.</summary>
    public async Task<NoteResult> GetNoteAsync(string? pathOrName, int? maxChars, CancellationToken ct)
    {
        var s = await _index.GetAsync(ct).ConfigureAwait(false);
        var note = Find(s, pathOrName);
        var limit = Math.Clamp(maxChars ?? s.Config.ExcerptChars, 100, 20_000);
        var (text, truncated) = Excerpt.Head(note.Body, limit);
        return new NoteResult(note.Path, note.Title, note.Tags, note.Links, note.ModifiedUtc, text, truncated, note.Body.Length);
    }

    /// <summary>Allow-listed notes that link to the given note with a wiki link, by file name or by path.</summary>
    public async Task<ListResult> BacklinksAsync(string? pathOrName, int? take, CancellationToken ct)
    {
        var s = await _index.GetAsync(ct).ConfigureAwait(false);
        var target = Find(s, pathOrName);
        var linking = s.Notes
            .Where(n => n.Path != target.Path && n.Links.Any(l => LinksTo(l, target)))
            .Select(Summary)
            .OrderBy(n => n.Path, StringComparer.Ordinal)
            .ToList();
        return new ListResult(target.Path, linking.Count, linking.Take(Math.Clamp(take ?? MaxTake, 1, MaxTake)).ToList());
    }

    /// <summary>Allow-listed notes carrying a tag (nested tags included: <c>azure</c> matches <c>azure/cosmos</c>). No tag means the context tag.</summary>
    public async Task<ListResult> TagsAsync(string? tag, int? take, CancellationToken ct)
    {
        var s = await _index.GetAsync(ct).ConfigureAwait(false);
        var t = NoteParser.NormalizeTag(tag) ?? s.Config.ContextTag;
        var tagged = s.Notes
            .Where(n => n.Tags.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase) || x.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(Summary)
            .OrderByDescending(n => n.ModifiedUtc)
            .ThenBy(n => n.Path, StringComparer.Ordinal)
            .ToList();
        return new ListResult("#" + t, tagged.Count, tagged.Take(Math.Clamp(take ?? MaxTake, 1, MaxTake)).ToList());
    }

    private static VaultNote Find(VaultIndex.Snapshot s, string? pathOrName)
    {
        var normalized = VaultPath.Normalize(pathOrName);
        if (normalized is null) throw VaultException.NotFound();
        var withExtension = normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".md";

        var byPath = s.Notes.FirstOrDefault(n => n.Path.Equals(withExtension, StringComparison.Ordinal))
                  ?? s.Notes.FirstOrDefault(n => n.Path.Equals(withExtension, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null) return byPath;

        var byName = s.Notes.Where(n => LinksTo(normalized, n)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
        {
            throw new VaultException("AMBIGUOUS", "Several notes share that name; give the path: " + string.Join(", ", byName.Select(n => n.Path).Take(5)));
        }
        throw VaultException.NotFound();
    }

    /// <summary>Obsidian resolves <c>[[Name]]</c> by file name, case-insensitively, and <c>[[folder/Name]]</c> by path.</summary>
    private static bool LinksTo(string link, VaultNote target)
    {
        var l = link.Replace('\\', '/').Trim().TrimEnd('/');
        if (l.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) l = l[..^3];
        if (l.Length == 0) return false;
        var pathNoExt = target.Path[..^3];
        return l.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
            || l.Equals(pathNoExt, StringComparison.OrdinalIgnoreCase)
            || pathNoExt.EndsWith("/" + l, StringComparison.OrdinalIgnoreCase);
    }

    private static NoteSummary Summary(VaultNote n) => new(n.Path, n.Title, n.Tags, n.ModifiedUtc);

    private static List<string> Terms(string? query)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return terms;
        foreach (var raw in query.Split(' ', '\t', '\n', '\r', ',', ';', '.', '"', '\'', '(', ')', '[', ']'))
        {
            var term = raw.Trim().ToLowerInvariant();
            if (term.Length < 2 || terms.Contains(term)) continue;
            terms.Add(term);
            if (terms.Count == MaxTerms) break;
        }
        return terms;
    }

    private static int Count(string body, string term, out int firstIndex)
    {
        var count = 0;
        firstIndex = -1;
        var at = 0;
        while ((at = body.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (firstIndex < 0) firstIndex = at;
            count++;
            at += term.Length;
            if (count >= 100) break;
        }
        return count;
    }
}
