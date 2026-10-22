using AdrMcp.Application.Common;
using AdrMcp.Application.Import;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Errors;
using AdrMcp.UnitTests.Support;

namespace AdrMcp.UnitTests.Application;

/// <summary>Parsing of the legacy ADR layouts, the import planner's judgement calls, and the adr_import tool.</summary>
public class LegacyImportTests
{
    private const string Tyree = """
        # ADR-204001: GitHub PATs and Tokens Management

        | Field | Value |
        |---|---|
        | **ID** | ADR-204001 |
        | **Status** | `Accepted` |
        | **Provider / Scope** | Microsoft Azure / Multi-Cloud |
        | **Discipline / Domain** | Security & Identity (`04`) |
        | **Author(s)** | Saul Patino / Enterprise Architecture Team |
        | **Date** | 2026-06-17 |
        | **Replaces / Supersedes** | ADF-014 |

        ---

        ## 1. Architectural Issue / Problem Statement

        Long-lived credentials are an attack surface.

        ## 2. Positions / Options Considered

        | Option / Position | Pros | Cons | Rejection Rationale |
        |---|---|---|---|
        | **Option A: Classic PATs** | Simple.<br>Universal. | Long-lived. | **Prohibited.** |
        | **Option B (Chosen): OIDC** | No stored secrets. | Needs setup. | **Selected.** |

        ## 3. Decision / Chosen Position

        Classic PATs must not be used in pipelines. Use OIDC federation.

        ## 4. Assumptions

        - Entra supports workload identity federation.

        ## 5. Constraints

        - No long-lived secrets.

        ## 6. Argument & Rationale

        Short-lived tokens remove the credential to steal.

        ## 7. Implications & Consequences

        ### 7.1 Positive Implications
        - No rotation toil.

        ## 9. Related Artifacts & Implementation Notes

        ```bash
        # this comment must not become a heading
        ## neither must this
        az login
        ```
        """;

    private const string LegacySections = """
        # ADR-RUNNER-001: Self-Hosted Runner Strategy

        ## Status

        Accepted

        ## Context

        CI workloads are frequent.

        ## Decision

        Use a self-hosted runner as the default.

        ## Consequences

        - Host becomes a single point of failure.
        """;

    // ------------------------------------------------------------------ parser

    [Fact]
    public void Tyree_layout_maps_metadata_sections_and_options_table()
    {
        var doc = AdrMarkdown.ParseDocument(Tyree);
        var c = doc.Content;
        Assert.Equal("ADR-204001", doc.DocumentId);
        Assert.Equal("GitHub PATs and Tokens Management", c.Title);
        Assert.Equal(AdrStatus.Accepted, c.Status);
        Assert.True(doc.StatusRecognized);
        Assert.Equal(new DateOnly(2026, 6, 17), doc.Date);
        Assert.Equal(new[] { "Saul Patino", "Enterprise Architecture Team" }, c.Owners);
        Assert.Contains("attack surface", c.Context);
        Assert.Contains("### Assumptions", c.Context);
        Assert.Contains("### Constraints", c.Context);
        Assert.Contains("Use OIDC federation", c.Decision);
        Assert.Contains("remove the credential", c.Rationale);
        Assert.Contains("7.1 Positive", c.Consequences);
        Assert.Equal(2, c.Alternatives.Count);
        Assert.StartsWith("Option A: Classic PATs", c.Alternatives[0]);
        Assert.Contains("Pros: Simple.; Universal.", c.Alternatives[0]);
        Assert.Contains("Rejection Rationale: Prohibited.", c.Alternatives[0]);
    }

    [Fact]
    public void Code_fences_are_never_parsed_as_headings()
    {
        var c = AdrMarkdown.Parse(Tyree);
        Assert.NotNull(c.HumanNotes);
        Assert.Contains("# this comment must not become a heading", c.HumanNotes);
        Assert.Contains("az login", c.HumanNotes);
        Assert.Equal("GitHub PATs and Tokens Management", c.Title);
    }

    [Fact]
    public void Legacy_status_section_layout_is_understood()
    {
        var doc = AdrMarkdown.ParseDocument(LegacySections);
        Assert.Equal("ADR-RUNNER-001", doc.DocumentId);
        Assert.Equal(AdrStatus.Accepted, doc.Content.Status);
        Assert.Null(doc.Date);
        Assert.Contains("single point of failure", doc.Content.Consequences);
    }

    [Theory]
    [InlineData("Accepted", AdrStatus.Accepted, null)]
    [InlineData("`Proposed`", AdrStatus.Proposed, null)]
    [InlineData("Under Review", AdrStatus.UnderReview, null)]
    [InlineData("Superseded by ADR-203001", AdrStatus.Superseded, "ADR-203001")]
    public void Status_text_parses_with_optional_successor(string raw, AdrStatus expected, string? tail)
    {
        Assert.True(AdrMarkdown.TryParseStatus(raw, out var status, out var parsedTail));
        Assert.Equal(expected, status);
        Assert.Equal(tail, parsedTail);
    }

    [Fact]
    public void Unknown_status_is_reported_not_silently_turned_into_a_draft()
    {
        var doc = AdrMarkdown.ParseDocument("# T\n\n- Status: Maybe\n\n## Context\nx\n\n## Decision\ny");
        Assert.False(doc.StatusRecognized);
        Assert.Equal("Maybe", doc.RawStatus);
    }

    [Fact]
    public void Existing_madr_style_documents_still_parse_the_same_way()
    {
        var c = AdrMarkdown.Parse("# Use Service Bus\n\n- Status: Proposed\n- Tags: messaging, orders\n\n## Context\nNeed async.\n\n## Decision\nUse Service Bus.\n\n## Considered Options\n- Event Hubs\n- RabbitMQ\n\n## Consequences\nVendor dependency.");
        Assert.Equal("Use Service Bus", c.Title);
        Assert.Equal(AdrStatus.Proposed, c.Status);
        Assert.Equal(new[] { "messaging", "orders" }, c.Tags);
        Assert.Equal(new[] { "Event Hubs", "RabbitMQ" }, c.Alternatives);
    }

    // ------------------------------------------------------------------ planner

    private static string Adr(string id, string title, string status = "Accepted", string? date = "2026-06-17", string replaces = "None", string decision = "We will use X. Y must not be used.", string extra = "")
        => $"""
            # {id}: {title}

            | Field | Value |
            |---|---|
            | **ID** | {id} |
            | **Status** | {status} |
            | **Provider** | Microsoft Azure |
            | **Discipline** | Security |
            | **Author** | Architecture Working Group |
            {(date is null ? "" : $"| **Date** | {date} |")}
            | **Replaces / Supersedes** | {replaces} |

            ## Context

            Because of reasons. {extra}

            ## Decision

            {decision}

            ## Drivers

            Reasons.

            ## Alternatives Considered

            - Option one

            ## Consequences

            Some consequences.
            """;

    private static PlanOptions Options(ImportOverrides? o = null) => new() { Today = new DateOnly(2026, 10, 22), SourceTag = "source:test", Overrides = o ?? new ImportOverrides() };

    [Fact]
    public void Clean_set_plans_without_issues_and_tags_each_record()
    {
        var plan = LegacyImportPlanner.Plan(new[]
        {
            new SourceFile("ADR-204001.md", Adr("ADR-204001", "Token management")),
            new SourceFile("ADR-Template.md", "# ADR-[ID]: x"),
            new SourceFile("Home.md", "# Home"),
        }, Options());
        Assert.False(plan.IsBlocked);
        var record = Assert.Single(plan.Records);
        Assert.Equal("ADR-204001", record.Id);
        Assert.Equal("2026-06-17", record.DecidedOn);
        Assert.Equal("Accepted", record.Fields.Status);
        Assert.Contains("provider:azure", record.Fields.Tags!);
        Assert.Contains("discipline:security", record.Fields.Tags!);
        Assert.Contains("imported", record.Fields.Tags!);
        Assert.Contains("source:test", record.Fields.Tags!);
        Assert.DoesNotContain(record.Fields.Tags!, t => t.StartsWith("review:"));
        Assert.Equal(2, plan.Skipped.Count);
        Assert.Equal(record.Source.Sha256, Hashing.Sha256Hex(record.Markdown));
    }

    [Fact]
    public void Replaces_that_names_an_active_adr_is_kept_as_a_note_not_a_supersession()
    {
        var plan = LegacyImportPlanner.Plan(new[]
        {
            new SourceFile("ADR-203001.md", Adr("ADR-203001", "FinOps workbook governance", replaces: "ADR-208007")),
            new SourceFile("ADR-208007.md", Adr("ADR-208007", "Workflow file naming")),
        }, Options());
        var record = plan.Records.Single(r => r.Id == "ADR-203001");
        Assert.Null(record.Fields.Supersedes);
        Assert.Contains("Earlier draft number ADR-208007", record.Fields.HumanNotes);
        Assert.Contains(plan.Issues, i => i.AdrId == "ADR-203001" && i.Code == "REPLACES_ACTIVE_ADR");
        Assert.Contains("review:links", record.Fields.Tags!);
        Assert.Equal("Accepted", plan.Records.Single(r => r.Id == "ADR-208007").Fields.Status);
    }

    [Fact]
    public void Old_numbers_and_local_file_names_become_aliases_that_resolve_references()
    {
        var plan = LegacyImportPlanner.Plan(new[]
        {
            new SourceFile("ADR-107001.md", Adr("ADR-107001", "Bedrock access layer", replaces: "ADR-AWS-001")),
            new SourceFile("ADR-204006.md", Adr("ADR-204006", "Redirect URI management", replaces: "0003-entra-redirect-uri-management.md", extra: "See ADR-AWS-001 and ADR-AZURE-099.")),
        }, Options());
        var bedrock = plan.Records.Single(r => r.Id == "ADR-107001");
        Assert.Equal(new[] { "ADR-AWS-001" }, bedrock.Aliases);
        Assert.Contains("Formerly known as: ADR-AWS-001", bedrock.Fields.HumanNotes);
        Assert.Contains(plan.Issues, i => i.AdrId == "ADR-204006" && i.Code == "REFERENCE_VIA_ALIAS" && i.Message.Contains("ADR-AWS-001 -> ADR-107001"));
        Assert.Contains(plan.Issues, i => i.AdrId == "ADR-204006" && i.Code == "DANGLING_REFERENCE" && i.Message.Contains("ADR-AZURE-099"));
    }

    [Fact]
    public void Missing_date_falls_back_to_git_and_is_flagged_and_without_git_it_blocks()
    {
        var withGit = LegacyImportPlanner.Plan(new[] { new SourceFile("ADR-RUNNER-001.md", Adr("ADR-RUNNER-001", "Runner strategy", date: null), new DateOnly(2026, 6, 22)) }, Options());
        Assert.Equal("2026-06-22", withGit.Records[0].DecidedOn);
        Assert.Equal("git", withGit.Records[0].Source.DateSource);
        Assert.Contains(withGit.Issues, i => i.Code == "DATE_FROM_GIT");
        Assert.False(withGit.IsBlocked);

        var without = LegacyImportPlanner.Plan(new[] { new SourceFile("ADR-RUNNER-001.md", Adr("ADR-RUNNER-001", "Runner strategy", date: null)) }, Options());
        Assert.True(without.IsBlocked);
        Assert.Contains(without.Issues, i => i.Code == "DATE_MISSING");
    }

    [Fact]
    public void Blocking_errors_can_be_acknowledged_by_a_human_in_overrides()
    {
        var files = new[] { new SourceFile("ADR-204001.md", Adr("ADR-204001", "Token management", decision: "")) };
        Assert.True(LegacyImportPlanner.Plan(files, Options()).IsBlocked);
        var overrides = new ImportOverrides { Acknowledge = { ["ADR-204001"] = new List<string> { "EMPTY_DECISION" } } };
        Assert.False(LegacyImportPlanner.Plan(files, Options(overrides)).IsBlocked);
    }

    [Fact]
    public void A_secret_cannot_be_acknowledged_away()
    {
        var files = new[] { new SourceFile("ADR-204002.md", Adr("ADR-204002", "Bootstrap secrets", extra: "key ghp_abcdefghijklmnopqrstuvwxyz0123456789")) };
        var overrides = new ImportOverrides { Acknowledge = { ["ADR-204002"] = new List<string> { "SECRET_TOKEN" } } };
        Assert.True(LegacyImportPlanner.Plan(files, Options(overrides)).IsBlocked);
    }

    [Fact]
    public void Sensitive_values_are_reported_by_line_never_echoed()
    {
        const string guid = "3f2c9a1e-8b7d-4c11-9a52-1d6e7f0b4a33";
        var plan = LegacyImportPlanner.Plan(new[]
        {
            new SourceFile("ADR-204001.md", Adr("ADR-204001", "Token management", extra: $"Tenant {guid} and owner@example.com.")),
            new SourceFile("ADR-204002.md", Adr("ADR-204002", "Bootstrap secrets", extra: "key ghp_abcdefghijklmnopqrstuvwxyz0123456789")),
        }, Options());
        Assert.Contains(plan.Issues, i => i.Code == "SANITIZE_GUID" && i.Severity == IssueSeverity.Warning);
        Assert.Contains(plan.Issues, i => i.Code == "SANITIZE_EMAIL");
        Assert.Contains(plan.Issues, i => i.AdrId == "ADR-204002" && i.Code == "SECRET_TOKEN" && i.Severity == IssueSeverity.Error);
        Assert.True(plan.IsBlocked);
        Assert.All(plan.Issues, i => { Assert.DoesNotContain(guid, i.Message); Assert.DoesNotContain("ghp_", i.Message); Assert.DoesNotContain("owner@example.com", i.Message); });
    }

    [Fact]
    public void Superseded_status_with_an_external_successor_is_recorded_as_text()
    {
        var text = Adr("ADR-AZURE-030", "SQL platform", status: "Superseded").Replace("| **Replaces / Supersedes** | None |", "| **Superseded By** | Local ADR `0001-sql-first.md` |");
        var plan = LegacyImportPlanner.Plan(new[] { new SourceFile("ADR-AZURE-030.md", text) }, Options());
        var record = Assert.Single(plan.Records);
        Assert.Equal("Superseded", record.Fields.Status);
        Assert.Equal("0001-sql-first.md", record.Fields.SupersededBy);
        Assert.Contains(plan.Issues, i => i.Code == "SUPERSEDED_BY_EXTERNAL");
    }

    [Fact]
    public void Confirmed_supersession_in_overrides_links_both_adrs()
    {
        var overrides = new ImportOverrides { Supersedes = { ["ADR-204005"] = "ADR-204001" } };
        var plan = LegacyImportPlanner.Plan(new[]
        {
            new SourceFile("ADR-204001.md", Adr("ADR-204001", "Static keys")),
            new SourceFile("ADR-204005.md", Adr("ADR-204005", "Managed identity")),
        }, Options(overrides));
        var oldRecord = plan.Records.Single(r => r.Id == "ADR-204001");
        var newRecord = plan.Records.Single(r => r.Id == "ADR-204005");
        Assert.Equal("Superseded", oldRecord.Fields.Status);
        Assert.Equal("ADR-204005", oldRecord.Fields.SupersededBy);
        Assert.Equal("ADR-204001", newRecord.Fields.Supersedes);
    }

    [Fact]
    public void Missing_rationale_alternatives_and_consequences_are_warnings_and_trigger_needs_review()
    {
        var plan = LegacyImportPlanner.Plan(new[] { new SourceFile("ADR-RUNNER-001.md", LegacySections, new DateOnly(2026, 6, 22)) }, Options());
        Assert.False(plan.IsBlocked);
        Assert.Contains(plan.Issues, i => i.Code == "EMPTY_RATIONALE");
        Assert.Contains(plan.Issues, i => i.Code == "NO_ALTERNATIVES");
        Assert.Contains("review:thin", plan.Records[0].Fields.Tags!);
        Assert.DoesNotContain("provider:runner", plan.Records[0].Fields.Tags!);
        Assert.Contains("group:runner", plan.Records[0].Fields.Tags!);
    }

    [Fact]
    public void Same_decision_under_old_and_new_numbering_is_flagged_with_a_ready_to_paste_override()
    {
        var plan = LegacyImportPlanner.Plan(new[]
        {
            new SourceFile("ADR-AZURE-020.md", Adr("ADR-AZURE-020", "Private endpoint approval automation", decision: "Approve private endpoints with an Event Grid triggered Function.")),
            new SourceFile("ADR-205003.md", Adr("ADR-205003", "Private endpoint approval automation", decision: "Approve private endpoints with an Event Grid triggered Function.")),
            new SourceFile("ADR-205004.md", Adr("ADR-205004", "Virtual network flow logs", decision: "Enable VNet flow logs with traffic analytics.")),
        }, Options());
        var dup = Assert.Single(plan.Issues, i => i.Code == "POSSIBLE_DUPLICATE");
        Assert.Equal("ADR-205003", dup.AdrId);
        Assert.Contains("\"ADR-205003\": \"ADR-AZURE-020\"", dup.Message);
        Assert.Contains("review:links", plan.Records.Single(r => r.Id == "ADR-AZURE-020").Fields.Tags!);
        Assert.DoesNotContain("review:links", plan.Records.Single(r => r.Id == "ADR-205004").Fields.Tags!);
    }

    // ------------------------------------------------------------------ adr_import tool

    private static object Record(string id = "ADR-204001", string? date = "2026-06-17", string status = "Accepted")
        => new { id, markdown = Adr(id, "Token management"), decidedOn = date, sourcePath = id + ".md", fields = new { status, tags = new[] { "imported" } } };

    [Fact]
    public async Task Import_creates_an_accepted_adr_keeping_id_and_decision_date()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_import", new { records = new[] { Record() }, idempotencyKey = "t1" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.Equal("created", r.GetProperty("data").GetProperty("results")[0].GetProperty("outcome").GetString());

        var get = await host.CallAsync("adr_get", new { adrId = "ADR-204001" });
        var adr = get.GetProperty("data").GetProperty("adr");
        Assert.Equal("Accepted", adr.GetProperty("summary").GetProperty("status").GetString());
        Assert.StartsWith("2026-06-17", adr.GetProperty("createdAt").GetString());
        Assert.StartsWith("2026-06-17", adr.GetProperty("acceptedAt").GetString());
        Assert.Equal("adr_import", adr.GetProperty("provenance").GetProperty("source").GetString());
        Assert.Equal("human", adr.GetProperty("provenance").GetProperty("actorType").GetString());

        var history = await host.CallAsync("adr_history", new { adrId = "ADR-204001" });
        Assert.Equal("imported", history.GetProperty("data").GetProperty("versions")[0].GetProperty("changeKind").GetString());
    }

    [Fact]
    public async Task Import_is_idempotent_and_never_overwrites()
    {
        var host = new TestHost();
        await host.CallAsync("adr_import", new { records = new[] { Record() }, idempotencyKey = "a" });
        var again = await host.CallAsync("adr_import", new { records = new[] { Record() }, idempotencyKey = "b" });
        Assert.Equal("unchanged", again.GetProperty("data").GetProperty("results")[0].GetProperty("outcome").GetString());

        var changed = await host.CallAsync("adr_import", new { records = new[] { new { id = "ADR-204001", markdown = Adr("ADR-204001", "Token management", decision: "A different decision."), decidedOn = "2026-06-17", fields = new { status = "Accepted", tags = new[] { "imported" } } } }, idempotencyKey = "c" });
        Assert.Equal("conflict", changed.GetProperty("data").GetProperty("results")[0].GetProperty("outcome").GetString());
        var get = await host.CallAsync("adr_get", new { adrId = "ADR-204001" });
        Assert.Contains("We will use X", get.GetProperty("data").GetProperty("adr").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_import", new { records = new[] { Record() }, dryRun = true, idempotencyKey = "d" });
        Assert.Equal("would-create", r.GetProperty("data").GetProperty("results")[0].GetProperty("outcome").GetString());
        var get = await host.CallAsync("adr_get", new { adrId = "ADR-204001" });
        Assert.Equal(ErrorCodes.AdrNotFound, get.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Imported_accepted_adr_keeps_its_decision_immutable_and_is_searchable()
    {
        var host = new TestHost();
        await host.CallAsync("adr_import", new { records = new[] { Record() }, idempotencyKey = "e" });
        var update = await host.CallAsync("adr_update", new { adrId = "ADR-204001", fields = new { decision = "Rewrite history." } });
        Assert.False(update.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.Conflict, update.GetProperty("error").GetProperty("code").GetString());

        var search = await host.CallAsync("adr_search", new { query = "token" });
        Assert.Equal("ADR-204001", search.GetProperty("data").GetProperty("hits")[0].GetProperty("adr").GetProperty("id").GetString());
        var list = await host.CallAsync("adr_list", new { tag = "imported" });
        Assert.Single(list.GetProperty("data").GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Import_needs_the_approver_role()
    {
        var host = new TestHost(s => s.Remove("Auth:StaticRoles:1"));
        var r = await host.CallAsync("adr_import", new { records = new[] { Record() }, idempotencyKey = "f" });
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.Forbidden, r.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_records_are_reported_per_record_without_failing_the_batch()
    {
        var host = new TestHost();
        var r = await host.CallAsync("adr_import", new { records = new object[] { Record(date: "2999-01-01"), Record("ADR-204002", date: "17/06/2026"), Record("ADR-204003") }, idempotencyKey = "g" });
        var results = r.GetProperty("data").GetProperty("results");
        Assert.Equal("invalid", results[0].GetProperty("outcome").GetString());
        Assert.Equal("invalid", results[1].GetProperty("outcome").GetString());
        Assert.Equal("created", results[2].GetProperty("outcome").GetString());
    }
}
