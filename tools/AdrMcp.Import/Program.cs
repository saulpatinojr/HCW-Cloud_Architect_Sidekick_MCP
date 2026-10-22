// Loads a folder of existing ADR Markdown files into the ADR MCP server in two steps, so a human reviews before anything is written.
//
//   scan  Read-only. Parses every ADR file, applies the data-quality checks and writes a staging folder:
//         records/<ID>.json (what adr_import will receive), report.md (what to review), plan.json, overrides.json (your decisions).
//   load  Sends the staged records to the server's adr_import tool. Start with --dry-run.
//
// Usage:
//   dotnet run --project tools/AdrMcp.Import -- scan --source <adr-folder> --out <staging-folder> [--today yyyy-MM-dd] [--source-tag source:my-wiki]
//   dotnet run --project tools/AdrMcp.Import -- load --staging <staging-folder> --url http://localhost:7071/runtime/webhooks/mcp [--workspace default] [--dry-run]
// The function key (or Entra token) is read from ADR_MCP_KEY (or ADR_MCP_BEARER), never from the command line.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdrMcp.Analysis.Claims;
using AdrMcp.Application.Common;
using AdrMcp.Application.Import;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;

if (args.Length == 0 || args[0] is "-h" or "--help") return Usage();
var opts = ParseArgs(args.Skip(1).ToArray());
return args[0] switch
{
    "scan" => Scan(opts),
    "load" => await SafeLoadAsync(opts),
    _ => Usage()
};

static async Task<int> SafeLoadAsync(Dictionary<string, string> o)
{
    try { return await LoadAsync(o); }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
    {
        Console.Error.WriteLine($"Could not complete the load: {ex.Message}");
        Console.Error.WriteLine("Check that the server is running, the --url is right, and ADR_MCP_KEY (or ADR_MCP_BEARER) is set if the host needs one.");
        return 4;
    }
}

static int Usage()
{
    Console.WriteLine("""
        AdrMcp.Import
          scan --source <dir> --out <dir> [--overrides <file>] [--today yyyy-MM-dd] [--source-tag <tag>] [--overlap-cross 0.30] [--overlap-same 0.60]
          load --staging <dir> --url <mcp-url> [--workspace <id>] [--dry-run]
        Secrets: set ADR_MCP_KEY (function key) or ADR_MCP_BEARER (Entra access token) in the environment.
        """);
    return 1;
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        map[key] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    }
    return map;
}

// ---------------------------------------------------------------------------------------------------------------- scan
static int Scan(Dictionary<string, string> o)
{
    if (!o.TryGetValue("source", out var source) || !o.TryGetValue("out", out var outDir)) return Usage();
    if (!Directory.Exists(source)) { Console.Error.WriteLine($"Source folder not found: {source}"); return 1; }
    Directory.CreateDirectory(Path.Join(outDir, "records"));

    var overridesPath = o.TryGetValue("overrides", out var ov) ? ov : Path.Join(outDir, "overrides.json");
    var overrides = File.Exists(overridesPath) ? AppJson.Deserialize<ImportOverrides>(File.ReadAllText(overridesPath)) ?? new ImportOverrides() : new ImportOverrides();
    var today = o.TryGetValue("today", out var t) ? DateOnly.Parse(t) : DateOnly.FromDateTime(DateTime.UtcNow);
    var defaults = new PlanOptions();
    var options = new PlanOptions
    {
        Today = today, SourceTag = o.GetValueOrDefault("source-tag", "source:legacy-import"), Overrides = overrides,
        CrossNumberingOverlap = o.TryGetValue("overlap-cross", out var oc) ? double.Parse(oc, System.Globalization.CultureInfo.InvariantCulture) : defaults.CrossNumberingOverlap,
        SameNumberingOverlap = o.TryGetValue("overlap-same", out var os) ? double.Parse(os, System.Globalization.CultureInfo.InvariantCulture) : defaults.SameNumberingOverlap,
    };

    var files = Directory.GetFiles(source, "*.md", SearchOption.TopDirectoryOnly)
        .Select(f => new SourceFile(Path.GetFileName(f), File.ReadAllText(f, Encoding.UTF8), FirstCommitDate(source, Path.GetFileName(f))))
        .ToList();
    var plan = LegacyImportPlanner.Plan(files, options);

    foreach (var old in Directory.GetFiles(Path.Join(outDir, "records"), "*.json")) File.Delete(old);
    var extractor = new RegexClaimExtractor();
    var claimCounts = new Dictionary<string, int>();
    foreach (var r in plan.Records)
    {
        var dto = new { id = r.Id, markdown = r.Markdown, fields = r.Fields, decidedOn = r.DecidedOn, sourcePath = r.Source.Path };
        File.WriteAllText(Path.Join(outDir, "records", r.Id + ".json"), JsonSerializer.Serialize(dto, new JsonSerializerOptions(AppJson.Options) { WriteIndented = true }));
        claimCounts[r.Id] = extractor.Extract(Preview(r)).Count;
    }
    File.WriteAllText(Path.Join(outDir, "plan.json"), JsonSerializer.Serialize(new
    {
        scannedAt = today.ToString("yyyy-MM-dd"), blocked = plan.IsBlocked, records = plan.Records.Count,
        issues = plan.Issues, skipped = plan.Skipped,
        sources = plan.Records.Select(r => new { r.Id, r.Source, r.Aliases }),
    }, new JsonSerializerOptions(AppJson.Options) { WriteIndented = true }));
    // Best older-numbering match per new ADR, as a starting point for the human to review and copy into overrides.json.
    var suggested = new ImportOverrides();
    foreach (var g in plan.Issues.Where(i => i.Code == "POSSIBLE_DUPLICATE" && i.Related is not null && !i.Acknowledged).GroupBy(i => i.AdrId))
        suggested.Supersedes[g.Key] = g.OrderByDescending(i => i.Score).First().Related!;
    File.WriteAllText(Path.Join(outDir, "overrides.suggested.json"), JsonSerializer.Serialize(suggested, new JsonSerializerOptions(AppJson.Options) { WriteIndented = true }));
    if (!File.Exists(overridesPath))
        File.WriteAllText(overridesPath, JsonSerializer.Serialize(new ImportOverrides(), new JsonSerializerOptions(AppJson.Options) { WriteIndented = true }));
    File.WriteAllText(Path.Join(outDir, "report.md"), Report(plan, claimCounts, options, source));

    var errors = plan.Issues.Count(i => i.Severity == IssueSeverity.Error && !i.Acknowledged);
    var warnings = plan.Issues.Count(i => i.Severity == IssueSeverity.Warning && !i.Acknowledged);
    Console.WriteLine($"Scanned {files.Count} files: {plan.Records.Count} ADRs ready to stage, {plan.Skipped.Count} skipped, {errors} blocking errors, {warnings} warnings.");
    Console.WriteLine($"Read {Path.Join(outDir, "report.md")} next. {(plan.IsBlocked ? "Blocked: fix the errors or record a decision in overrides.json, then scan again." : "No blocking errors.")}");
    return plan.IsBlocked ? 2 : 0;
}

/// <summary>An in-memory ADR built the way the server will build it, so claim extraction here matches what the analyzers will see.</summary>
static Adr Preview(ImportRecord r)
{
    var content = AdrMarkdown.Parse(r.Markdown);
    var tags = r.Fields.Tags ?? Array.Empty<string>();
    content = content with { Tags = tags, Status = Enum.Parse<AdrStatus>(r.Fields.Status ?? "Draft", true) };
    return new Adr
    {
        Id = r.Id, TenantId = "preview", WorkspaceId = "preview", AdrNumber = 0, Human = content,
        CreatedAt = DateTimeOffset.UnixEpoch, Provenance = Provenance.Human("preview", DateTimeOffset.UnixEpoch, "preview")
    };
}

static DateOnly? FirstCommitDate(string dir, string file)
{
    try
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "log", "--follow", "--diff-filter=A", "--format=%as", "--", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var last = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        p.WaitForExit();
        return p.ExitCode == 0 && DateOnly.TryParse(last, out var d) ? d : null;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
}

static string Report(ImportPlan plan, Dictionary<string, int> claims, PlanOptions options, string source)
{
    var sb = new StringBuilder();
    var live = plan.Issues.Where(i => !i.Acknowledged).ToList();
    sb.AppendLine("# ADR import report");
    sb.AppendLine();
    sb.AppendLine($"- Source folder: `{source}`");
    sb.AppendLine($"- Scanned on: {options.Today:yyyy-MM-dd}");
    sb.AppendLine($"- ADRs staged: **{plan.Records.Count}**; files skipped: {plan.Skipped.Count}");
    sb.AppendLine($"- Blocking errors: **{live.Count(i => i.Severity == IssueSeverity.Error)}**; warnings: {live.Count(i => i.Severity == IssueSeverity.Warning)}; notes: {live.Count(i => i.Severity == IssueSeverity.Info)}");
    sb.AppendLine($"- Result: **{(plan.IsBlocked ? "BLOCKED - fix the errors below before loading" : "Ready for a dry run")}**");
    sb.AppendLine();

    sb.AppendLine("## What was found");
    sb.AppendLine();
    sb.AppendLine("| Layout | Count |"); sb.AppendLine("|---|---|");
    foreach (var g in plan.Records.GroupBy(r => r.Source.Layout).OrderByDescending(g => g.Count())) sb.AppendLine($"| {g.Key} | {g.Count()} |");
    sb.AppendLine();
    sb.AppendLine("| Status | Count |"); sb.AppendLine("|---|---|");
    foreach (var g in plan.Records.GroupBy(r => r.Fields.Status).OrderByDescending(g => g.Count())) sb.AppendLine($"| {g.Key} | {g.Count()} |");
    sb.AppendLine();
    sb.AppendLine("| Decision date source | Count |"); sb.AppendLine("|---|---|");
    foreach (var g in plan.Records.GroupBy(r => r.Source.DateSource)) sb.AppendLine($"| {g.Key} | {g.Count()} |");
    sb.AppendLine();

    sb.AppendLine("## Issues by code");
    sb.AppendLine();
    sb.AppendLine("| Code | Severity | ADRs | What it means |"); sb.AppendLine("|---|---|---|---|");
    foreach (var g in live.GroupBy(i => (i.Code, i.Severity)).OrderByDescending(g => g.Key.Severity).ThenBy(g => g.Key.Code))
        sb.AppendLine($"| `{g.Key.Code}` | {g.Key.Severity} | {g.Select(i => i.AdrId).Distinct().Count()} | {Cell(Meaning(g.Key.Code))} |");
    sb.AppendLine();

    foreach (var severity in new[] { IssueSeverity.Error, IssueSeverity.Warning })
    {
        var list = live.Where(i => i.Severity == severity).ToList();
        if (list.Count == 0) continue;
        sb.AppendLine(severity == IssueSeverity.Error ? "## Blocking errors (fix or acknowledge in overrides.json)" : "## Warnings (review; none of these block the load)");
        sb.AppendLine();
        foreach (var g in list.GroupBy(i => i.AdrId)) foreach (var i in g) sb.AppendLine($"- **{i.AdrId}** `{i.Code}`: {i.Message}");
        sb.AppendLine();
    }

    var infos = live.Where(i => i.Severity == IssueSeverity.Info).ToList();
    if (infos.Count > 0)
    {
        sb.AppendLine("## Notes");
        sb.AppendLine();
        foreach (var i in infos) sb.AppendLine($"- **{i.AdrId}** `{i.Code}`: {i.Message}");
        sb.AppendLine();
    }

    sb.AppendLine("## Staged ADRs");
    sb.AppendLine();
    sb.AppendLine("Inferred claims = statements the server's analyzers can read from the decision text without anyone declaring them (they feed drift and contradiction checks at confidence 0.6 or lower). 0 means the decision text has no 'must / must not' style rule to test.");
    sb.AppendLine();
    sb.AppendLine("| ADR | Title | Status | Decided | Layout | Inferred claims | Issues | Tags |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|");
    foreach (var r in plan.Records)
    {
        var title = AdrMarkdown.ParseDocument(r.Markdown).Content.Title;
        var n = live.Count(i => i.AdrId == r.Id && i.Severity != IssueSeverity.Info);
        sb.AppendLine($"| {r.Id} | {Cell(title)} | {r.Fields.Status} | {r.DecidedOn} | {r.Source.Layout} | {claims[r.Id]} | {n} | {Cell(string.Join(", ", r.Fields.Tags ?? Array.Empty<string>()))} |");
    }
    sb.AppendLine();

    var aliased = plan.Records.Where(r => r.Aliases.Count > 0).ToList();
    if (aliased.Count > 0)
    {
        sb.AppendLine("## Old numbers (aliases)");
        sb.AppendLine();
        sb.AppendLine("| Now | Formerly |"); sb.AppendLine("|---|---|");
        foreach (var r in aliased) sb.AppendLine($"| {r.Id} | {Cell(string.Join(", ", r.Aliases))} |");
        sb.AppendLine();
    }

    if (plan.Skipped.Count > 0)
    {
        sb.AppendLine("## Files skipped");
        sb.AppendLine();
        foreach (var s in plan.Skipped) sb.AppendLine($"- `{s.Path}`: {s.Reason}");
        sb.AppendLine();
    }

    sb.AppendLine("## Next steps");
    sb.AppendLine();
    sb.AppendLine("1. Fix or acknowledge every blocking error (edit the source file, or record your decision in `overrides.json`), then run `scan` again.");
    sb.AppendLine("2. Read the warnings. `REPLACES_ACTIVE_ADR`, `POSSIBLE_DUPLICATE` and `DATE_FROM_GIT` need a human decision. `overrides.suggested.json` lists the older ADRs that look like copies of a newer one; copy the pairs you agree with into `overrides.json` and scan again.");
    sb.AppendLine("3. Run `load --dry-run`, then `load`. See `docs/10-legacy-adr-import.md`.");
    return sb.ToString();
}

static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

static string Meaning(string code) => code switch
{
    "DUPLICATE_ID" => "Two files claim the same id.",
    "ID_MISMATCH" => "The id inside the file differs from the file name.",
    "STATUS_MISSING" or "STATUS_UNKNOWN" => "No usable lifecycle status.",
    "TITLE_MISSING" => "No title heading.",
    "EMPTY_CONTEXT" or "EMPTY_DECISION" => "A required section is empty.",
    "EMPTY_RATIONALE" or "NO_ALTERNATIVES" or "EMPTY_CONSEQUENCES" => "The ADR is thin; safety review will flag it.",
    "FIELD_TOO_LONG" => "A field is over the 20,000 character limit.",
    "DATE_MISSING" => "No decision date anywhere.",
    "DATE_FROM_GIT" => "Date taken from the first git commit; confirm it.",
    "DATE_IN_FUTURE" => "Decision date is after the import date.",
    "REPLACES_SELF" => "The file says it replaces itself.",
    "REPLACES_ACTIVE_ADR" => "Says it replaces an ADR that still exists and is active; kept as a note, not a supersession.",
    "SUPERSEDED_BY_EXTERNAL" => "Successor lives outside this import; kept as text.",
    "SUPERSEDED_WITHOUT_SUCCESSOR" => "Superseded but no successor named.",
    "OVERRIDE_UNKNOWN_ID" => "overrides.json names an id that is not in the import.",
    "REFERENCE_VIA_ALIAS" => "Mentions an old number that now has a new id.",
    "DANGLING_REFERENCE" => "Mentions an ADR that is not in the import.",
    "POSSIBLE_DUPLICATE" => "Reads like another ADR from the same provider; may be the same decision recorded twice.",
    "REVIEW_CURRENCY" => "Mentions dates, previews or end-of-life terms; verify it is still true.",
    "SECRET_PRIVATE_KEY" or "SECRET_TOKEN" or "SECRET_CONNECTION_STRING" => "Looks like a secret. Remove it from the source file.",
    "SANITIZE_GUID" or "SANITIZE_EMAIL" or "SANITIZE_PRIVATE_IP" => "Possible tenant, person or network detail; the ADR runbook says to remove these.",
    _ => ""
};

// ---------------------------------------------------------------------------------------------------------------- load
static async Task<int> LoadAsync(Dictionary<string, string> o)
{
    if (!o.TryGetValue("staging", out var staging) || !o.TryGetValue("url", out var url)) return Usage();
    var dryRun = o.ContainsKey("dry-run");
    var planPath = Path.Join(staging, "plan.json");
    if (!File.Exists(planPath)) { Console.Error.WriteLine("plan.json not found. Run scan first."); return 1; }
    if (JsonNode.Parse(File.ReadAllText(planPath))?["blocked"]?.GetValue<bool>() == true) { Console.Error.WriteLine("The last scan was blocked. Fix the errors and scan again before loading."); return 2; }

    var records = Directory.GetFiles(Path.Join(staging, "records"), "*.json").OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => (Node: JsonNode.Parse(File.ReadAllText(f))!.AsObject(), Size: new FileInfo(f).Length)).ToList();
    if (records.Count == 0) { Console.Error.WriteLine("No staged records."); return 1; }

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var key = Environment.GetEnvironmentVariable("ADR_MCP_KEY");
    var bearer = Environment.GetEnvironmentVariable("ADR_MCP_BEARER");
    if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Add("x-functions-key", key);
    if (!string.IsNullOrEmpty(bearer)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");

    var init = await Post(http, url, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "adr-import", version = "1" } } });
    if (init.Session is not null) http.DefaultRequestHeaders.Add("mcp-session-id", init.Session);
    await Post(http, url, new { jsonrpc = "2.0", method = "notifications/initialized" });

    var counts = new Dictionary<string, int>();
    var problems = new List<string>();
    // A fresh key per run: the server replays a stored answer for a repeated key for 24 hours, which would show a stale "created"
    // on a second run. adr_import is idempotent by itself (same content is "unchanged"), so nothing is lost.
    var runId = Guid.NewGuid().ToString("N")[..8];
    var batch = new List<JsonObject>(); long bytes = 0; var n = 0;
    async Task Flush()
    {
        if (batch.Count == 0) return;
        var ids = string.Join(",", batch.Select(b => b["id"]!.GetValue<string>()));
        var hash = Hashing.Sha256Hex(ids + string.Join("", batch.Select(b => Hashing.Sha256Hex(b.ToJsonString()))))[..16];
        var args = new JsonObject { ["records"] = new JsonArray(batch.Select(b => b.DeepClone()).ToArray()), ["dryRun"] = dryRun, ["idempotencyKey"] = $"import-{hash}-{runId}{(dryRun ? "-dry" : "")}" };
        if (o.TryGetValue("workspace", out var ws)) args["workspaceId"] = ws;
        var reply = await Post(http, url, new { jsonrpc = "2.0", id = 10 + ++n, method = "tools/call", @params = new { name = "adr_import", arguments = args } });
        var text = reply.Body?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var env = text is null ? null : JsonNode.Parse(text);
        if (env?["ok"]?.GetValue<bool>() != true) { problems.Add($"batch {n}: {env?["error"]?["code"]} {env?["error"]?["message"] ?? reply.Raw}"); batch.Clear(); bytes = 0; return; }
        foreach (var r in env["data"]!["results"]!.AsArray())
        {
            var outcome = r!["outcome"]!.GetValue<string>();
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
            if (outcome is "invalid" or "conflict") problems.Add($"{r["id"]} {outcome}: {r["message"]}");
        }
        batch.Clear(); bytes = 0;
    }
    foreach (var (node, size) in records)
    {
        if (batch.Count >= 25 || bytes + size > 1_000_000) await Flush();
        batch.Add(node); bytes += size;
    }
    await Flush();

    Console.WriteLine($"{(dryRun ? "Dry run" : "Load")} finished for {records.Count} staged ADRs: " + string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
    foreach (var p in problems) Console.WriteLine("  ! " + p);
    return problems.Count == 0 ? 0 : 3;
}

static async Task<(string? Session, JsonNode? Body, string Raw)> Post(HttpClient http, string url, object payload)
{
    using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    using var resp = await http.PostAsync(url, content);
    var raw = await resp.Content.ReadAsStringAsync();
    resp.Headers.TryGetValues("mcp-session-id", out var sids);
    if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 202) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} from the MCP server: {Truncate(raw)}");
    JsonNode? body = null;
    var trimmed = raw.Trim();
    if (trimmed.StartsWith('{')) body = JsonNode.Parse(trimmed);
    else
        foreach (var line in trimmed.Split('\n'))
            if (line.StartsWith("data:") && line[5..].Trim() is { Length: > 0 } chunk && chunk.StartsWith('{')) { var m = JsonNode.Parse(chunk); if (m?["result"] is not null || m?["error"] is not null) body = m; }
    return (sids?.FirstOrDefault(), body, Truncate(raw));
}

static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
