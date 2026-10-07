using System.Text;
using System.Text.RegularExpressions;
using AdrMcp.Domain.Adrs;

namespace AdrMcp.Application.Common;

/// <summary>Parses and renders MADR-style Markdown. Deterministic; unknown sections are preserved in HumanNotes.</summary>
public static partial class AdrMarkdown
{
    private static readonly Dictionary<string, string> SectionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["context"] = "context", ["context and problem statement"] = "context", ["problem"] = "context",
        ["decision"] = "decision", ["decision outcome"] = "decision", ["chosen option"] = "decision",
        ["rationale"] = "rationale", ["decision drivers"] = "rationale", ["justification"] = "rationale",
        ["alternatives"] = "alternatives", ["considered options"] = "alternatives", ["considered alternatives"] = "alternatives", ["options"] = "alternatives",
        ["consequences"] = "consequences",
        ["status"] = "status",
    };

    public static HumanOwnedContent Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("Markdown is empty.", nameof(markdown));
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        string title = "Untitled";
        var sections = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        string current = "preamble";
        sections[current] = new StringBuilder();
        var status = AdrStatus.Draft;
        var tags = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var h1 = H1().Match(line);
            if (h1.Success && title == "Untitled") { title = h1.Groups[1].Value.Trim(); continue; }
            var h2 = H2().Match(line);
            if (h2.Success)
            {
                var name = h2.Groups[1].Value.Trim();
                current = SectionAliases.TryGetValue(name, out var canonical) ? canonical : "other:" + name;
                if (!sections.ContainsKey(current)) sections[current] = new StringBuilder();
                continue;
            }
            var statusLine = StatusLine().Match(line);
            if (statusLine.Success && Enum.TryParse<AdrStatus>(statusLine.Groups[1].Value.Replace(" ", ""), true, out var st)) { status = st; continue; }
            var tagLine = TagsLine().Match(line);
            if (tagLine.Success) { tags.AddRange(tagLine.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); continue; }
            sections[current].AppendLine(line);
        }

        if (sections.TryGetValue("status", out var statusSection) && Enum.TryParse<AdrStatus>(statusSection.ToString().Trim().Replace(" ", ""), true, out var s2)) status = s2;

        var others = sections.Where(kv => kv.Key.StartsWith("other:", StringComparison.Ordinal))
            .Select(kv => $"## {kv.Key[6..]}\n{kv.Value.ToString().Trim()}").ToList();

        return new HumanOwnedContent
        {
            Title = title,
            Context = Get(sections, "context"),
            Decision = Get(sections, "decision"),
            Rationale = Get(sections, "rationale"),
            Alternatives = ParseList(Get(sections, "alternatives")),
            Consequences = Get(sections, "consequences"),
            Status = status,
            Tags = tags,
            HumanNotes = others.Count == 0 ? null : string.Join("\n\n", others)
        };
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

    private static IReadOnlyList<string> ParseList(string body)
        => body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', ' ').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    [GeneratedRegex(@"^#\s+(.+)$")] private static partial Regex H1();
    [GeneratedRegex(@"^##\s+(.+)$")] private static partial Regex H2();
    [GeneratedRegex(@"^\s*[-*]?\s*\**Status\**\s*[:：]\s*\**([A-Za-z ]+)\**\s*$", RegexOptions.IgnoreCase)] private static partial Regex StatusLine();
    [GeneratedRegex(@"^\s*[-*]?\s*\**Tags\**\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase)] private static partial Regex TagsLine();
}
