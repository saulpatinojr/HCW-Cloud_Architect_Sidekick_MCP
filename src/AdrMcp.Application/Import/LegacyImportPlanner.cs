using System.Text;
using System.Text.RegularExpressions;
using AdrMcp.Application.Common;
using AdrMcp.Application.Tools;
using AdrMcp.Domain.Adrs;

namespace AdrMcp.Application.Import;

/// <summary>One Markdown file offered to the planner. <paramref name="GitDate"/> is the first commit date, used only when the file states no date.</summary>
public sealed record SourceFile(string Path, string Text, DateOnly? GitDate = null);

public enum IssueSeverity { Info, Warning, Error }

/// <summary>A finding about one ADR. Error blocks the import unless a human acknowledged it in the overrides file.</summary>
/// <param name="Related">For POSSIBLE_DUPLICATE across numbering schemes: the older ADR that <paramref name="AdrId"/> probably replaces.</param>
/// <param name="Score">Similarity behind <paramref name="Related"/>, 0..1.</param>
public sealed record ImportIssue(string AdrId, IssueSeverity Severity, string Code, string Message, bool Acknowledged = false, string? Related = null, double? Score = null);

public sealed record ImportSource(string Path, string Sha256, string Layout, string DateSource);

/// <summary>One record of the adr_import tool call, plus the provenance the report needs.</summary>
public sealed record ImportRecord(
    string Id,
    string Markdown,
    AdrFieldsDto Fields,
    string? DecidedOn,
    ImportSource Source,
    IReadOnlyList<string> Aliases);

public sealed record SkippedFile(string Path, string Reason);

public sealed record ImportPlan(
    IReadOnlyList<ImportRecord> Records,
    IReadOnlyList<ImportIssue> Issues,
    IReadOnlyList<SkippedFile> Skipped)
{
    public bool IsBlocked => Issues.Any(i => i.Severity == IssueSeverity.Error && !i.Acknowledged);
}

/// <summary>Human decisions that the planner cannot make for itself. Loaded from overrides.json next to the staging output.</summary>
public sealed record ImportOverrides
{
    /// <summary>ADR id to lifecycle status, for ids whose source status is missing or wrong.</summary>
    public Dictionary<string, string> Status { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ADR id to ISO decision date.</summary>
    public Dictionary<string, string> DecidedOn { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Confirmed supersessions: the new ADR id to the id it replaces. The old ADR is imported as Superseded.</summary>
    public Dictionary<string, string> Supersedes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ids left out of the import.</summary>
    public List<string> Skip { get; init; } = new();
    /// <summary>ADR id to issue codes a human reviewed and accepted.</summary>
    public Dictionary<string, List<string>> Acknowledge { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PlanOptions
{
    /// <summary>Import day. Passed in so the output is reproducible.</summary>
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    /// <summary>Tag marking where the records came from, e.g. "source:hcw-wiki".</summary>
    public string SourceTag { get; init; } = "source:legacy-import";
    /// <summary>Cosine similarity at which an old-numbering and a new-numbering ADR of the same provider are flagged as possible copies.</summary>
    public double CrossNumberingOverlap { get; init; } = 0.25;
    /// <summary>Cosine similarity at which two ADRs numbered the same way are flagged.</summary>
    public double SameNumberingOverlap { get; init; } = 0.60;
    public ImportOverrides Overrides { get; init; } = new();
}

/// <summary>
/// Turns the old ADR files into reviewed import records. Pure and deterministic: no I/O, no clock, no network.
/// Nothing is rewritten silently: the original Markdown travels with every record and every judgement call is an issue.
/// </summary>
public static partial class LegacyImportPlanner
{
    private static readonly string[] Disciplines = { "", "operations", "reliability", "finops", "security", "networking", "compute", "app-ai", "governance", "tools" };
    private static readonly string[] Providers = { "cross-cloud", "aws", "azure", "gcp" };
    private const int MaxFieldLength = 20_000;

    public static ImportPlan Plan(IEnumerable<SourceFile> files, PlanOptions options)
    {
        var issues = new List<ImportIssue>();
        var skipped = new List<SkippedFile>();
        var parsed = new List<Parsed>();

        foreach (var file in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(file.Path);
            if (!AdrStem().IsMatch(stem)) { skipped.Add(new SkippedFile(file.Path, "Not an ADR file name (expected ADR-NNNNNN or ADR-GROUP-NNN).")); continue; }
            if (options.Overrides.Skip.Contains(stem, StringComparer.OrdinalIgnoreCase)) { skipped.Add(new SkippedFile(file.Path, "Skipped by overrides.json.")); continue; }
            parsed.Add(new Parsed(stem, file, AdrMarkdown.ParseDocument(file.Text)));
        }

        foreach (var dup in parsed.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new ImportIssue(dup.Key, IssueSeverity.Error, "DUPLICATE_ID", $"Id appears in {dup.Count()} files: {string.Join(", ", dup.Select(d => d.File.Path))}."));

        var ids = parsed.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statusById = parsed.ToDictionary(p => p.Id, p => StatusOf(p, options), StringComparer.OrdinalIgnoreCase);

        // Alias map: old numbers that the files say they replaced, so references to them can be resolved.
        var aliasOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var perRecord = new Dictionary<string, RecordWork>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parsed)
        {
            var w = new RecordWork();
            perRecord[p.Id] = w;
            ClassifyReplaces(p, ids, statusById, issues, w);
            foreach (var a in w.Aliases) aliasOwner.TryAdd(a, p.Id);
        }

        // Confirmed supersessions from overrides.json.
        foreach (var (newId, oldId) in options.Overrides.Supersedes)
        {
            if (!ids.Contains(newId) || !ids.Contains(oldId)) { issues.Add(new ImportIssue(newId, IssueSeverity.Error, "OVERRIDE_UNKNOWN_ID", $"overrides.json supersedes {newId} -> {oldId}, but one of them is not in the import set.")); continue; }
            perRecord[newId].Supersedes = oldId;
            perRecord[oldId].SupersededBy = newId;
            statusById[oldId] = AdrStatus.Superseded;
        }

        var records = new List<ImportRecord>();
        foreach (var p in parsed.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var work = perRecord[p.Id];
            var record = BuildRecord(p, work, statusById[p.Id], ids, aliasOwner, options, issues);
            if (record is not null) records.Add(record);
        }

        var overlapping = FlagPossibleOverlaps(records, options, issues);
        for (var i = 0; i < records.Count; i++)
            if (overlapping.Contains(records[i].Id) && !records[i].Fields.Tags!.Contains("review:links"))
                records[i] = records[i] with { Fields = records[i].Fields with { Tags = records[i].Fields.Tags!.Append("review:links").ToList() } };
        var acknowledged = Acknowledge(issues, options.Overrides);
        return new ImportPlan(records, acknowledged, skipped);
    }

    // ------------------------------------------------------------------ record building

    private static ImportRecord? BuildRecord(Parsed p, RecordWork work, AdrStatus status, HashSet<string> ids, Dictionary<string, string> aliasOwner, PlanOptions options, List<ImportIssue> issues)
    {
        var doc = p.Doc;
        var c = doc.Content;
        void Add(IssueSeverity s, string code, string msg) => issues.Add(new ImportIssue(p.Id, s, code, msg));
        var review = new SortedSet<string>(StringComparer.Ordinal);

        if (doc.DocumentId is not null && !string.Equals(doc.DocumentId, p.Id, StringComparison.OrdinalIgnoreCase))
            Add(IssueSeverity.Error, "ID_MISMATCH", $"File name says {p.Id} but the document says {doc.DocumentId}.");
        if (doc.RawStatus is null && !options.Overrides.Status.ContainsKey(p.Id)) Add(IssueSeverity.Error, "STATUS_MISSING", "The document states no status. Set one in overrides.json.");
        else if (!doc.StatusRecognized && !options.Overrides.Status.ContainsKey(p.Id)) Add(IssueSeverity.Error, "STATUS_UNKNOWN", $"Status '{doc.RawStatus}' is not a lifecycle status ({string.Join(", ", Enum.GetNames<AdrStatus>())}).");
        if (string.IsNullOrWhiteSpace(c.Title) || c.Title == "Untitled") Add(IssueSeverity.Error, "TITLE_MISSING", "No title heading found.");
        if (string.IsNullOrWhiteSpace(c.Context)) Add(IssueSeverity.Error, "EMPTY_CONTEXT", "No context or problem statement. Analyzers and search rely on it.");
        if (string.IsNullOrWhiteSpace(c.Decision)) Add(IssueSeverity.Error, "EMPTY_DECISION", "No decision text. Drift and contradiction analysis read it.");
        if (string.IsNullOrWhiteSpace(c.Rationale)) { Add(IssueSeverity.Warning, "EMPTY_RATIONALE", "No rationale or drivers; add them so reviewers can see why."); review.Add("review:thin"); }
        if (c.Alternatives.Count == 0) { Add(IssueSeverity.Warning, "NO_ALTERNATIVES", "No alternatives recorded; adr_safety_review flags a decision with no alternatives considered."); review.Add("review:thin"); }
        if (string.IsNullOrWhiteSpace(c.Consequences)) { Add(IssueSeverity.Warning, "EMPTY_CONSEQUENCES", "No consequences; adr_safety_review looks for rollback and monitoring notes here."); review.Add("review:thin"); }
        foreach (var (name, text) in new[] { ("context", c.Context), ("decision", c.Decision), ("rationale", c.Rationale), ("consequences", c.Consequences) })
            if (text.Length > MaxFieldLength) Add(IssueSeverity.Error, "FIELD_TOO_LONG", $"{name} is {text.Length} characters; the limit is {MaxFieldLength}.");

        // Decision date: the document wins, then git history, then a human override.
        string? decidedOn = null; var dateSource = "document";
        if (options.Overrides.DecidedOn.TryGetValue(p.Id, out var overrideDate)) { decidedOn = overrideDate; dateSource = "override"; }
        else if (doc.Date is { } docDate) decidedOn = docDate.ToString("yyyy-MM-dd");
        else if (p.File.GitDate is { } gitDate) { decidedOn = gitDate.ToString("yyyy-MM-dd"); dateSource = "git"; Add(IssueSeverity.Warning, "DATE_FROM_GIT", $"No decision date in the document; used the first commit date {decidedOn}. Confirm or set it in overrides.json."); review.Add("review:date"); }
        else { dateSource = "none"; Add(IssueSeverity.Error, "DATE_MISSING", "No decision date in the document and no git history to fall back on."); }
        if (decidedOn is not null && DateOnly.TryParse(decidedOn, out var when) && when > options.Today) Add(IssueSeverity.Error, "DATE_IN_FUTURE", $"Decision date {decidedOn} is after the import date {options.Today:yyyy-MM-dd}.");

        // Sanitisation and currency are scanned on the whole file, not just the parsed fields.
        if (!ScanSensitive(p, issues)) review.Add("review:sanitize");
        if (ScanCurrency(p, options, issues)) review.Add("review:currency");
        if (CheckReferences(p, ids, aliasOwner, issues)) review.Add("review:links");
        if (work.NeedsReview) review.Add("review:links");

        // Status tail for Superseded ("Superseded by ADR-X" or a Superseded By row).
        string? supersededBy = work.SupersededBy;
        if (status == AdrStatus.Superseded && supersededBy is null)
        {
            var text = doc.Metadata.TryGetValue("Superseded By", out var row) ? row : doc.StatusTail;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var target = AdrIdToken().Matches(text).Select(m => m.Value).FirstOrDefault(ids.Contains);
                supersededBy = target ?? CleanExternal(text);
                if (target is null) Add(IssueSeverity.Info, "SUPERSEDED_BY_EXTERNAL", $"Superseded by something outside this import: '{supersededBy}'. Recorded as text in supersededBy.");
            }
            else { Add(IssueSeverity.Warning, "SUPERSEDED_WITHOUT_SUCCESSOR", "Status is Superseded but no successor is named."); review.Add("review:links"); }
        }

        var tags = BuildTags(p, doc, options, review);
        var notes = BuildNotes(p, doc, work, dateSource, options);
        var layout = Layout(p);
        var sha = Hashing.Sha256Hex(p.File.Text);

        var fields = new AdrFieldsDto(
            Title: null, Context: null, Decision: null, Rationale: null, Alternatives: null, Consequences: null,
            Status: status.ToString(), Owners: null, Approvers: null, ApprovalResult: null,
            HumanNotes: notes, Tags: tags, Claims: null, Supersedes: work.Supersedes, SupersededBy: supersededBy);
        return new ImportRecord(p.Id, p.File.Text, fields, decidedOn, new ImportSource(p.File.Path, sha, layout, dateSource), work.Aliases);
    }

    private static AdrStatus StatusOf(Parsed p, PlanOptions options)
    {
        if (options.Overrides.Status.TryGetValue(p.Id, out var s) && Enum.TryParse<AdrStatus>(s, true, out var forced)) return forced;
        return p.Doc.Content.Status;
    }

    private static string Layout(Parsed p)
        => p.File.Text.Contains("## 1. Architectural Issue", StringComparison.Ordinal) ? "tyree-akerman"
         : p.Doc.Metadata.Count > 0 ? "madr-table"
         : p.Doc.RawStatus is not null ? "madr-sections" : "other";

    // ------------------------------------------------------------------ supersession and aliases

    /// <summary>
    /// "Replaces / Supersedes" in these files mixes real supersession with renumbering history (old drafts, old numbering schemes,
    /// local repository file names). A reference becomes a supersession link only when it is unambiguous; everything else is kept as an alias.
    /// </summary>
    private static void ClassifyReplaces(Parsed p, HashSet<string> ids, Dictionary<string, AdrStatus> statusById, List<ImportIssue> issues, RecordWork w)
    {
        if (!p.Doc.Metadata.TryGetValue("Replaces / Supersedes", out var value) && !p.Doc.Metadata.TryGetValue("Supersedes", out value)) return;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase) || value == "-") return;
        foreach (var token in ReplacesToken().Matches(value).Select(m => m.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (token.Equals(p.Id, StringComparison.OrdinalIgnoreCase)) { issues.Add(new ImportIssue(p.Id, IssueSeverity.Warning, "REPLACES_SELF", "Says it replaces itself; ignored.")); w.NeedsReview = true; continue; }
            if (ids.Contains(token))
            {
                if (statusById[token] == AdrStatus.Superseded) { w.Supersedes = token; continue; }
                issues.Add(new ImportIssue(p.Id, IssueSeverity.Warning, "REPLACES_ACTIVE_ADR",
                    $"Says it replaces {token}, but {token} is a different ADR in this import with status {statusById[token]}. Treated as an earlier draft number, not a supersession. To confirm a real supersession add \"{p.Id}\": \"{token}\" under supersedes in overrides.json."));
                w.Notes.Add($"Earlier draft number {token} (that number now belongs to a different ADR).");
                w.NeedsReview = true;
                continue;
            }
            w.Aliases.Add(token);
        }
    }

    // ------------------------------------------------------------------ tags and notes

    private static List<string> BuildTags(Parsed p, AdrMarkdownDocument doc, PlanOptions options, IEnumerable<string> review)
    {
        var tags = new List<string>(doc.Content.Tags);
        var (provider, discipline, group) = DeriveScope(p, doc);
        if (provider is not null) tags.Add($"provider:{provider}");
        if (discipline is not null) tags.Add($"discipline:{discipline}");
        if (group is not null) tags.Add($"group:{group}");
        tags.Add("imported");
        tags.Add(options.SourceTag);
        tags.AddRange(review);
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (string? Provider, string? Discipline, string? Group) DeriveScope(Parsed p, AdrMarkdownDocument doc)
    {
        string? provider = null, discipline = null, group_ = null;
        var m = SixDigit().Match(p.Id);
        if (m.Success)
        {
            var pi = int.Parse(m.Groups[1].Value); var di = int.Parse(m.Groups[2].Value);
            if (pi < Providers.Length) provider = Providers[pi];
            if (di > 0 && di < Disciplines.Length) discipline = Disciplines[di];
        }
        if (provider is null && (doc.Metadata.TryGetValue("Provider", out var pv) || doc.Metadata.TryGetValue("Provider / Scope", out pv))) provider = ProviderSlug(pv);
        if (discipline is null && (doc.Metadata.TryGetValue("Discipline", out var dv) || doc.Metadata.TryGetValue("Discipline / Domain", out dv))) discipline = Slug(Regex.Replace(dv, @"\(.*?\)|`", ""));
        if (provider is null)
        {
            var group = LegacyGroup().Match(p.Id);
            if (group.Success)
            {
                // Groups such as RUNNER or PLATFORM are topics, not providers; they stay searchable as a group tag.
                provider = group.Groups[1].Value.ToLowerInvariant() switch { "azure" => "azure", "aws" => "aws", "gcp" => "gcp", "cross" => "cross-cloud", "github" => "github", _ => null };
                group_ = provider is null ? group.Groups[1].Value.ToLowerInvariant() : null;
            }
        }
        return (provider, discipline, group_);
    }

    private static string ProviderSlug(string value)
    {
        var v = value.ToLowerInvariant();
        if (v.Contains("cross")) return "cross-cloud";
        if (v.Contains("azure")) return "azure";
        if (v.Contains("amazon") || v.Contains("aws")) return "aws";
        if (v.Contains("google") || v.Contains("gcp")) return "gcp";
        if (v.Contains("github")) return "github";
        return Slug(value);
    }

    private static string Slug(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private static string BuildNotes(Parsed p, AdrMarkdownDocument doc, RecordWork work, string dateSource, PlanOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Imported on {options.Today:yyyy-MM-dd} from {System.IO.Path.GetFileName(p.File.Path)} ({Layout(p)} layout); decision date from {dateSource}.");
        if (work.Aliases.Count > 0) sb.AppendLine($"Formerly known as: {string.Join(", ", work.Aliases)}.");
        foreach (var n in work.Notes) sb.AppendLine(n);
        if (doc.Metadata.TryGetValue("Source Draft", out var draft) && !string.IsNullOrWhiteSpace(draft)) sb.AppendLine($"Source draft: {draft}.");
        if (!string.IsNullOrWhiteSpace(doc.Content.HumanNotes)) { sb.AppendLine(); sb.Append(doc.Content.HumanNotes); }
        return sb.ToString().Trim();
    }

    private static string CleanExternal(string text) => Regex.Replace(text.Replace("`", "").Replace("Local ADR", "", StringComparison.OrdinalIgnoreCase), @"\s+", " ").Trim().TrimEnd('.');

    // ------------------------------------------------------------------ scans

    /// <summary>Returns true when nothing needs a human look (notes such as private IP addresses do not count).</summary>
    private static bool ScanSensitive(Parsed p, List<ImportIssue> issues)
    {
        var clean = true;
        var lines = p.File.Text.Replace("\r\n", "\n").Split('\n');
        foreach (var (code, severity, rx, what) in SensitiveRules)
        {
            var hits = lines.Select((l, i) => (l, i)).Where(t => rx.IsMatch(t.l)).Select(t => t.i + 1).ToList();
            if (hits.Count == 0) continue;
            if (severity >= IssueSeverity.Warning) clean = false;
            // Only counts and line numbers are reported, never the matched text.
            issues.Add(new ImportIssue(p.Id, severity, code, $"{what} on line(s) {string.Join(", ", hits.Take(8))}{(hits.Count > 8 ? $" and {hits.Count - 8} more" : "")}. The ADR runbook says to remove these before publishing."));
        }
        return clean;
    }

    private static bool ScanCurrency(Parsed p, PlanOptions options, List<ImportIssue> issues)
    {
        var hits = CurrencyTerms().Matches(p.File.Text).Select(m => m.Value.ToLowerInvariant()).Distinct().Take(6).ToList();
        if (hits.Count == 0) return false;
        issues.Add(new ImportIssue(p.Id, IssueSeverity.Info, "REVIEW_CURRENCY", $"Mentions time-sensitive terms ({string.Join(", ", hits)}). Check against current vendor documentation; imported on {options.Today:yyyy-MM-dd}."));
        return true;
    }

    private static bool CheckReferences(Parsed p, HashSet<string> ids, Dictionary<string, string> aliasOwner, List<ImportIssue> issues)
    {
        var refs = AdrIdToken().Matches(p.File.Text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !r.Equals(p.Id, StringComparison.OrdinalIgnoreCase) && !ids.Contains(r)).ToList();
        if (refs.Count == 0) return false;
        var resolved = refs.Where(aliasOwner.ContainsKey).ToList();
        var missing = refs.Except(resolved, StringComparer.OrdinalIgnoreCase).ToList();
        if (resolved.Count > 0)
            issues.Add(new ImportIssue(p.Id, IssueSeverity.Info, "REFERENCE_VIA_ALIAS", $"References old numbers that now live under new ids: {string.Join(", ", resolved.Select(r => $"{r} -> {aliasOwner[r]}"))}. The text is kept as written."));
        if (missing.Count > 0)
            issues.Add(new ImportIssue(p.Id, IssueSeverity.Warning, "DANGLING_REFERENCE", $"References ADRs that are not in this import: {string.Join(", ", missing)}. Import them, or accept the gap."));
        return missing.Count > 0;
    }

    /// <summary>
    /// Finds decisions that look like the same decision recorded twice. Similarity is TF-IDF cosine over title and decision text.
    /// An older-numbering ADR and a new-numbering ADR from the same provider are compared at a lower bar, because that is how
    /// the wiki's renumbering produced copies. This is a prompt for a human, never a verdict. Returns the ids that were flagged.
    /// </summary>
    private static HashSet<string> FlagPossibleOverlaps(List<ImportRecord> records, PlanOptions options, List<ImportIssue> issues)
    {
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contents = records.ToDictionary(r => r.Id, r => AdrMarkdown.ParseDocument(r.Markdown).Content, StringComparer.OrdinalIgnoreCase);
        var counts = records.ToDictionary(r => r.Id, r => { var c = contents[r.Id]; return WordCounts($"{c.Title} {c.Title} {c.Decision}"); }, StringComparer.OrdinalIgnoreCase);
        var docFreq = new Dictionary<string, int>();
        foreach (var words in counts.Values) foreach (var w in words.Keys) docFreq[w] = docFreq.GetValueOrDefault(w) + 1;
        var n = records.Count;
        var vectors = counts.ToDictionary(kv => kv.Key,
            kv => kv.Value.ToDictionary(w => w.Key, w => (1 + Math.Log(w.Value)) * (Math.Log((n + 1.0) / (docFreq[w.Key] + 1)) + 1)), StringComparer.OrdinalIgnoreCase);
        string? ProviderOf(ImportRecord r) => r.Fields.Tags?.FirstOrDefault(t => t.StartsWith("provider:", StringComparison.Ordinal));
        bool IsOldNumbering(ImportRecord r) => LegacyGroup().IsMatch(r.Id);

        for (var i = 0; i < records.Count; i++)
            for (var j = i + 1; j < records.Count; j++)
            {
                var a = records[i]; var b = records[j];
                var provider = ProviderOf(a);
                if (provider is null || provider != ProviderOf(b)) continue;
                var crossNumbering = IsOldNumbering(a) != IsOldNumbering(b);
                var sim = Cosine(vectors[a.Id], vectors[b.Id]);
                if (sim < (crossNumbering ? options.CrossNumberingOverlap : options.SameNumberingOverlap)) continue;
                var (older, newer) = crossNumbering ? (IsOldNumbering(a) ? (a, b) : (b, a)) : (a, b);
                issues.Add(new ImportIssue(newer.Id, IssueSeverity.Warning, "POSSIBLE_DUPLICATE", crossNumbering
                    ? $"Reads {sim:P0} like {older.Id}, an older-numbering ADR for the same provider. If it is the same decision recorded twice, mark the older one superseded: add \"{newer.Id}\": \"{older.Id}\" under supersedes in overrides.json."
                    : $"Reads {sim:P0} like {older.Id}. If one replaces the other, confirm it in overrides.json.",
                    Related: crossNumbering ? older.Id : null, Score: Math.Round(sim, 3)));
                flagged.Add(a.Id); flagged.Add(b.Id);
            }
        return flagged;
    }

    private static Dictionary<string, int> WordCounts(string text)
        => Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+").Where(t => t.Length > 2 && !Stop.Contains(t)).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        var dot = a.Where(kv => b.ContainsKey(kv.Key)).Sum(kv => kv.Value * b[kv.Key]);
        var na = Math.Sqrt(a.Values.Sum(x => x * x)); var nb = Math.Sqrt(b.Values.Sum(x => x * x));
        return na == 0 || nb == 0 ? 0 : dot / (na * nb);
    }

    private static readonly HashSet<string> Stop = new()
    {
        "the", "and", "for", "with", "from", "into", "over", "that", "this", "are", "not", "use", "using", "all", "any", "per", "each", "when", "which", "their", "than",
        "also", "but", "can", "may", "its", "has", "have", "been", "between", "within", "across", "such", "more", "most", "only", "both", "other", "these", "those", "will", "must",
        "should", "shall", "mandates", "mandate", "enterprise", "architecture", "standard", "strategy", "azure", "aws", "gcp", "github", "platform"
    };

    private static List<ImportIssue> Acknowledge(List<ImportIssue> issues, ImportOverrides overrides)
        // A secret in the source is never acceptable: remove it from the file. Everything else a human may accept.
        => issues.Select(i => !i.Code.StartsWith("SECRET_", StringComparison.Ordinal) && overrides.Acknowledge.TryGetValue(i.AdrId, out var codes) && codes.Contains(i.Code, StringComparer.OrdinalIgnoreCase) ? i with { Acknowledged = true } : i)
                 .OrderBy(i => i.AdrId, StringComparer.OrdinalIgnoreCase).ThenByDescending(i => i.Severity).ThenBy(i => i.Code, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------ helpers and patterns

    private sealed record Parsed(string Id, SourceFile File, AdrMarkdownDocument Doc);

    private sealed class RecordWork
    {
        public List<string> Aliases { get; } = new();
        public List<string> Notes { get; } = new();
        public string? Supersedes { get; set; }
        public string? SupersededBy { get; set; }
        public bool NeedsReview { get; set; }
    }

    private static readonly (string Code, IssueSeverity Severity, Regex Pattern, string What)[] SensitiveRules =
    {
        ("SECRET_PRIVATE_KEY", IssueSeverity.Error, new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled), "A private key block"),
        ("SECRET_TOKEN", IssueSeverity.Error, new Regex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})\b", RegexOptions.Compiled), "A token-shaped value (GitHub, AWS or JWT)"),
        ("SECRET_CONNECTION_STRING", IssueSeverity.Error, new Regex(@"(?:AccountKey|SharedAccessSignature|sig)=[A-Za-z0-9%+/=_-]{16,}", RegexOptions.Compiled | RegexOptions.IgnoreCase), "A storage key or SAS signature"),
        ("SANITIZE_GUID", IssueSeverity.Warning, new Regex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "A GUID (possible tenant, subscription or app id)"),
        ("SANITIZE_EMAIL", IssueSeverity.Warning, new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled), "An email address"),
        ("SANITIZE_PRIVATE_IP", IssueSeverity.Info, new Regex(@"\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled), "A private IP address"),
    };

    [GeneratedRegex(@"^ADR-(?:\d{6}|[A-Za-z]+-\d{3})$")] private static partial Regex AdrStem();
    [GeneratedRegex(@"\bADR-(?:\d{6}|[A-Z]+-\d{3})\b")] private static partial Regex AdrIdToken();
    [GeneratedRegex(@"ADR-(?:\d{6}|[A-Z]+-\d{3})|ADF-\d+|\d{4}-[A-Za-z0-9-]+(?:\.md)?")] private static partial Regex ReplacesToken();
    [GeneratedRegex(@"^ADR-(\d)(\d\d)\d{3}$")] private static partial Regex SixDigit();
    [GeneratedRegex(@"^ADR-([A-Za-z]+)-\d{3}$")] private static partial Regex LegacyGroup();
    [GeneratedRegex(@"\b(?:public preview|private preview|preview|deprecat\w*|retir\w+|end[- ]of[- ]life|EOL|sunset\w*|(?:until|by|through) 20\d\d|20(?:2[7-9]|3\d))\b", RegexOptions.IgnoreCase)] private static partial Regex CurrencyTerms();
}
