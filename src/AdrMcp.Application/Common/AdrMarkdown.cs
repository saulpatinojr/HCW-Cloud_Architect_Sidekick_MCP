using System.Text;
using System.Text.RegularExpressions;
using AdrMcp.Domain.Adrs;

namespace AdrMcp.Application.Common;

/// <summary>What <see cref="AdrMarkdown.ParseDocument"/> learned from a document beyond the human-owned content itself.</summary>
/// <param name="Content">Human-owned fields mapped from the document.</param>
/// <param name="DocumentId">Id from the metadata table (ID row) or the "ADR-xxx:" prefix of the title; null when the document has none.</param>
/// <param name="Metadata">Rows of the leading metadata table, keys as written (without bold markers), values without backticks.</param>
/// <param name="RawStatus">Status text exactly as the document states it (null when absent).</param>
/// <param name="StatusRecognized">False when a status was stated but is not a lifecycle status; Content.Status is then Draft.</param>
/// <param name="StatusTail">Text after the status word, e.g. the "ADR-X" in "Superseded by ADR-X".</param>
/// <param name="Date">Decision date from the metadata table when it is an ISO date.</param>
public sealed record AdrMarkdownDocument(
    HumanOwnedContent Content,
    string? DocumentId,
    IReadOnlyDictionary<string, string> Metadata,
    string? RawStatus,
    bool StatusRecognized,
    string? StatusTail,
    DateOnly? Date);

/// <summary>
/// Parses and renders ADR Markdown. Deterministic. Understands MADR-style documents, the HCW legacy layouts and the
/// Tyree and Akerman layout (numbered headings, a metadata table, an options table). Unknown sections are preserved in HumanNotes.
/// </summary>
public static partial class AdrMarkdown
{
    private static readonly Dictionary<string, string> SectionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["context"] = "context", ["context and problem statement"] = "context", ["problem"] = "context",
        ["architectural issue / problem statement"] = "context", ["problem statement"] = "context",
        ["decision"] = "decision", ["decision outcome"] = "decision", ["chosen option"] = "decision", ["decision / chosen position"] = "decision",
        ["rationale"] = "rationale", ["decision drivers"] = "rationale", ["drivers"] = "rationale", ["justification"] = "rationale", ["argument & rationale"] = "rationale",
        ["alternatives"] = "alternatives", ["considered options"] = "alternatives", ["considered alternatives"] = "alternatives", ["options"] = "alternatives",
        ["alternatives considered"] = "alternatives", ["alternatives evaluated"] = "alternatives", ["positions / options considered"] = "alternatives",
        ["consequences"] = "consequences", ["implications & consequences"] = "consequences",
        ["assumptions"] = "assumptions", ["constraints"] = "constraints",
        ["status"] = "status",
    };

    /// <summary>Metadata keys (case-insensitive) that carry the owners and the approvers.</summary>
    private static readonly string[] OwnerKeys = { "Author", "Author(s)", "Owner", "Owners" };
    private static readonly string[] ApproverKeys = { "Reviewed By", "Approver", "Approvers", "Approved By" };

    public static HumanOwnedContent Parse(string markdown) => ParseDocument(markdown).Content;

    public static AdrMarkdownDocument ParseDocument(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("Markdown is empty.", nameof(markdown));
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        string title = "Untitled";
        string? documentId = null;
        var sections = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        string current = "preamble";
        sections[current] = new StringBuilder();
        string? rawStatus = null;
        var tags = new List<string>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inFence = false;
        var seenSection = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            // Code fences are copied through untouched so a "# comment" or "## x" inside a script never becomes a heading.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; sections[current].AppendLine(line); continue; }
            if (inFence) { sections[current].AppendLine(line); continue; }

            var h1 = H1().Match(line);
            if (h1.Success && title == "Untitled")
            {
                title = h1.Groups[1].Value.Trim();
                var prefix = IdPrefix().Match(title);
                if (prefix.Success) { documentId = prefix.Groups[1].Value; title = prefix.Groups[2].Value.Trim(); }
                continue;
            }
            var h2 = H2().Match(line);
            if (h2.Success)
            {
                seenSection = true;
                var name = h2.Groups[1].Value.Trim();
                var plain = NumberPrefix().Replace(name, "").Trim();
                current = SectionAliases.TryGetValue(plain, out var canonical) ? canonical : "other:" + name;
                if (!sections.ContainsKey(current)) sections[current] = new StringBuilder();
                continue;
            }
            if (!seenSection)
            {
                var row = MetadataRow().Match(line);
                if (row.Success)
                {
                    var key = row.Groups[1].Value.Trim().Trim('*').Trim();
                    var value = row.Groups[2].Value.Trim().Trim('`').Trim();
                    if (key.Length > 0 && !key.Equals("Field", StringComparison.OrdinalIgnoreCase) && !SeparatorCell().IsMatch(key)) metadata[key] = value;
                    continue;
                }
                var statusLine = StatusLine().Match(line);
                if (statusLine.Success) { rawStatus = statusLine.Groups[1].Value.Trim(); continue; }
                var tagLine = TagsLine().Match(line);
                if (tagLine.Success) { tags.AddRange(tagLine.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); continue; }
            }
            if (line == "---") continue;
            sections[current].AppendLine(line);
        }

        if (metadata.TryGetValue("ID", out var metaId) && metaId.Length > 0) documentId = metaId;
        if (metadata.TryGetValue("Status", out var metaStatus) && metaStatus.Length > 0) rawStatus = metaStatus;
        if (sections.TryGetValue("status", out var statusSection) && statusSection.ToString().Trim().Length > 0) rawStatus = FirstLine(statusSection.ToString());

        var status = AdrStatus.Draft;
        var recognized = true;
        string? tail = null;
        if (rawStatus is not null) recognized = TryParseStatus(rawStatus, out status, out tail);

        var others = sections.Where(kv => kv.Key.StartsWith("other:", StringComparison.Ordinal))
            .Select(kv => $"## {kv.Key[6..]}\n{kv.Value.ToString().Trim()}").ToList();

        // Assumptions and constraints have no field of their own; they are part of the context the decision was made in.
        var context = new StringBuilder(Get(sections, "context"));
        foreach (var (key, heading) in new[] { ("assumptions", "Assumptions"), ("constraints", "Constraints") })
        {
            var body = Get(sections, key);
            if (body.Length == 0) continue;
            if (context.Length > 0) context.Append("\n\n");
            context.Append($"### {heading}\n{body}");
        }

        DateOnly? date = metadata.TryGetValue("Date", out var d) && DateOnly.TryParseExact(d, "yyyy-MM-dd", out var parsed) ? parsed : null;

        var content = new HumanOwnedContent
        {
            Title = title,
            Context = context.ToString(),
            Decision = Get(sections, "decision"),
            Rationale = Get(sections, "rationale"),
            Alternatives = ParseAlternatives(Get(sections, "alternatives")),
            Consequences = Get(sections, "consequences"),
            Status = status,
            Owners = SplitPeople(FirstValue(metadata, OwnerKeys)),
            Approvers = SplitPeople(FirstValue(metadata, ApproverKeys)),
            Tags = tags,
            HumanNotes = others.Count == 0 ? null : string.Join("\n\n", others)
        };
        return new AdrMarkdownDocument(content, documentId, metadata, rawStatus, recognized, tail, date);
    }

    /// <summary>Parses "Accepted", "`Accepted`", "Under Review" and "Superseded by ADR-X". False when the leading words are not a lifecycle status.</summary>
    public static bool TryParseStatus(string raw, out AdrStatus status, out string? tail)
    {
        status = AdrStatus.Draft; tail = null;
        var text = raw.Trim().Trim('`', '*').Trim();
        foreach (var candidate in Enum.GetValues<AdrStatus>().OrderByDescending(s => s.ToString().Length))
        {
            var name = candidate.ToString();
            var spaced = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
            foreach (var form in new[] { spaced, name })
            {
                if (!text.StartsWith(form, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = text[form.Length..];
                if (rest.Length > 0 && char.IsLetter(rest[0])) continue;
                status = candidate;
                var t = rest.Trim().Trim('`', '*', ':', '-', ' ');
                tail = t.StartsWith("by ", StringComparison.OrdinalIgnoreCase) ? t[3..].Trim().Trim('`') : (t.Length == 0 ? null : t);
                return true;
            }
        }
        return false;
    }

    public static string Render(Adr adr)
    {
        var h = adr.Human;
        var sb = new StringBuilder();
        sb.AppendLine($"# {h.Title}");
        sb.AppendLine();
        sb.AppendLine($"- Status: {h.Status}");
        sb.AppendLine($"- ADR: {adr.Id} (#{adr.AdrNumber})");
        sb.AppendLine($"- Created: {adr.CreatedAt:yyyy-MM-dd}");
        if (h.AcceptedAt is not null) sb.AppendLine($"- Accepted: {h.AcceptedAt:yyyy-MM-dd}");
        if (h.Owners.Count > 0) sb.AppendLine($"- Owners: {string.Join(", ", h.Owners)}");
        if (h.Approvers.Count > 0) sb.AppendLine($"- Approvers: {string.Join(", ", h.Approvers)}");
        if (h.Tags.Count > 0) sb.AppendLine($"- Tags: {string.Join(", ", h.Tags)}");
        if (h.Supersedes is not null) sb.AppendLine($"- Supersedes: {h.Supersedes}");
        if (h.SupersededBy is not null) sb.AppendLine($"- Superseded by: {h.SupersededBy}");
        sb.AppendLine($"- Human revision: {adr.HumanRevision}; Observed revision: {adr.ObservedRevision}");
        sb.AppendLine();
        Section(sb, "Context", h.Context);
        Section(sb, "Decision", h.Decision);
        Section(sb, "Rationale", h.Rationale);
        if (h.Alternatives.Count > 0) Section(sb, "Considered Alternatives", string.Join("\n", h.Alternatives.Select(a => $"- {a}")));
        Section(sb, "Consequences", h.Consequences);
        if (h.Claims.Count > 0) Section(sb, "Declared Claims", string.Join("\n", h.Claims.Select(c => $"- [{c.Kind}] {c.Subject} {c.Predicate} {c.Object}".TrimEnd())));
        if (!string.IsNullOrWhiteSpace(h.HumanNotes)) Section(sb, "Notes", h.HumanNotes!);
        if (adr.Observed.DetectedTechnologies.Count > 0)
        {
            sb.AppendLine("## Observed Technologies (system-managed)");
            sb.AppendLine();
            foreach (var t in adr.Observed.DetectedTechnologies.OrderBy(t => t.NormalizedName))
                sb.AppendLine($"- {t.NormalizedName} {t.DetectedVersion} ({t.Category}, {t.Status}, confidence {t.Confidence.Score:0.00}) — {t.SourceFile}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string name, string body)
    {
        sb.AppendLine($"## {name}");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(body) ? "_Not provided._" : body.Trim());
        sb.AppendLine();
    }

    private static string Get(Dictionary<string, StringBuilder> s, string key) => s.TryGetValue(key, out var sb) ? sb.ToString().Trim() : string.Empty;

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static string? FirstValue(Dictionary<string, string> metadata, string[] keys)
        => keys.Select(k => metadata.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static IReadOnlyList<string> SplitPeople(string? value)
        => value is null ? Array.Empty<string>() : value.Split(new[] { " / ", ";", "," }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.Length > 0).ToList();

    /// <summary>Alternatives from a table (one entry per data row, cells labelled by the header) or from a bullet list.</summary>
    private static IReadOnlyList<string> ParseAlternatives(string body)
    {
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!lines.Any(l => l.StartsWith('|'))) return ParseList(body);
        var rows = lines.Where(l => l.StartsWith('|') && !SeparatorRow().IsMatch(l)).Select(SplitRow).ToList();
        if (rows.Count < 2) return ParseList(body);
        var header = rows[0];
        var result = new List<string>();
        foreach (var row in rows.Skip(1))
        {
            var parts = new List<string>();
            for (var i = 1; i < row.Count && i < header.Count; i++)
                if (row[i].Length > 0) parts.Add($"{header[i]}: {row[i]}");
            result.Add(parts.Count == 0 ? row[0] : $"{row[0]} — {string.Join(" | ", parts)}");
        }
        return result.Where(r => r.Length > 0).ToList();
    }

    private static List<string> SplitRow(string row)
        => row.Trim().Trim('|').Split('|').Select(c => HtmlBreak().Replace(c.Trim(), "; ").Replace("**", "").Replace("`", "").Trim()).ToList();

    private static IReadOnlyList<string> ParseList(string body)
        => body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', ' ').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    [GeneratedRegex(@"^#\s+(.+)$")] private static partial Regex H1();
    [GeneratedRegex(@"^##\s+(.+)$")] private static partial Regex H2();
    [GeneratedRegex(@"^(ADR[-A-Za-z0-9]*\d)\s*[:：—–-]\s*(.+)$")] private static partial Regex IdPrefix();
    [GeneratedRegex(@"^\d+(\.\d+)*\.?\s+")] private static partial Regex NumberPrefix();
    [GeneratedRegex(@"^\|\s*(\**[^|]+?\**)\s*\|\s*(.*?)\s*\|$")] private static partial Regex MetadataRow();
    [GeneratedRegex(@"^:?-{2,}:?$")] private static partial Regex SeparatorCell();
    [GeneratedRegex(@"^\|[\s:|-]+\|?$")] private static partial Regex SeparatorRow();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex HtmlBreak();
    [GeneratedRegex(@"^\s*[-*]?\s*\**Status\**\s*[:：]\s*\**([A-Za-z ]+)\**\s*$", RegexOptions.IgnoreCase)] private static partial Regex StatusLine();
    [GeneratedRegex(@"^\s*[-*]?\s*\**Tags\**\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase)] private static partial Regex TagsLine();
}
