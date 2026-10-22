# Importing existing ADRs

How to bring a folder of existing ADR Markdown files (for example the HCW wiki) into this server so that the content is **in one standard format**, the data is **correct and current**, and the MCP tools can **work with it afterwards**.

The method has two commands and one human review between them:

```
scan  (read-only)  ->  report.md + overrides.json  ->  you decide  ->  scan again  ->  load --dry-run  ->  load
```

| Goal | What provides it |
|---|---|
| 1. A standard format | `AdrMarkdown.ParseDocument` reads every layout found in the old repo into the server's fields (table below). The original file is kept verbatim as the source of record. |
| 2. Right, current data | `LegacyImportPlanner` runs data-quality gates and writes `report.md`. Nothing is rewritten silently; each judgement call is an issue you can read, accept or override. |
| 3. Usable through MCP | `adr_import` loads each ADR in its real status, with its real id and decision date, so `adr_list`, `adr_search`, `adr_supersede`, contradiction, safety and drift tools work on it. `review:*` tags make a work queue. |

## 1. The standard format

### What the server stores

Human-owned fields only change through a human action (see [CLAUDE.md](../CLAUDE.md)). The importer is that human action: it runs as an Approver, and the history shows `adr_import` as the source.

| Server field | Taken from (Tyree and Akerman layout) | Taken from (older HCW layouts) |
|---|---|---|
| `title` | `# ADR-204001: Title` (the id prefix is removed) | same |
| `status` | metadata table `Status` | metadata table, or a `## Status` section |
| `owners` | metadata `Author(s)`, split on ` / ` `,` `;` | metadata `Author` |
| `approvers` | metadata `Reviewed By` (when present) | metadata `Reviewed By` |
| `context` | `1. Architectural Issue / Problem Statement`, then `### Assumptions` (section 4) and `### Constraints` (section 5) | `## Context` |
| `decision` | `3. Decision / Chosen Position` | `## Decision` |
| `rationale` | `6. Argument & Rationale` | `## Drivers` / `## Decision Drivers` |
| `alternatives` | `2. Positions / Options Considered`: one entry per table row, cells labelled by column | `## Alternatives Considered` (table or bullets) |
| `consequences` | `7. Implications & Consequences` (7.1 to 7.3 kept) | `## Consequences` |
| `humanNotes` | an import header, then everything else in the file: related decisions, diagrams, specifications, references | same |
| `tags` | `provider:*` and `discipline:*` (from the id, or the metadata table), `group:*`, `imported`, `source:*`, `review:*` | same |
| `createdAt`, `acceptedAt` | metadata `Date` (the decision date, not the import date) | git first-commit date, flagged `review:date` |
| `supersedes`, `supersededBy` | only when unambiguous (see gate `REPLACES_ACTIVE_ADR`) or confirmed in `overrides.json` | same |

Code fences are copied through untouched, so a `# comment` in a script never becomes a heading. Unknown sections go to `humanNotes`; nothing is dropped.

### Ids

Ids are kept exactly as written (`ADR-204001`, `ADR-AZURE-001`) so wiki links and references stay valid. Two numbering schemes exist in the HCW wiki:

- New: `ADR-<provider digit><discipline 2 digits><sequence 3 digits>` (Governance-and-Lifecycle.md), Tyree and Akerman layout.
- Old: `ADR-<GROUP>-<NNN>` (`AZURE`, `GITHUB`, `CROSS`, `RUNNER`, `PLATFORM`), MADR-style layout.

The wiki's own `ADR-Creation-Runbook.md` still prescribes the old scheme and the old layout, while `Governance-and-Lifecycle.md` and `ADR-Template.md` prescribe the new one. **Pick one before you write more ADRs.** This repository's recommendation is the new scheme with the Tyree and Akerman layout, because 48 of 105 ADRs already use it, it carries assumptions and constraints (which the contradiction and safety tools read), and the governance guide names it as the standard.

### Writing new ADRs so they import cleanly

Use `ADR-Template.md` from the wiki. The parts the server needs: a title heading, a metadata table with `ID`, `Status` and `Date` (`yyyy-MM-dd`), and sections for the problem, the options, the decision, the rationale and the consequences. Put rollback and monitoring notes under consequences (the safety review looks for them). State rules in plain "must" / "must not" sentences in the decision; the claim extractor reads those.

## 2. Data-quality gates

`scan` reports every issue with a code. **Error** blocks `load` until fixed or acknowledged. **Warning** needs a look. **Info** is a note.

| Code | Severity | Meaning and what to do |
|---|---|---|
| `DUPLICATE_ID`, `ID_MISMATCH` | Error | Two files share an id, or the id inside differs from the file name. Fix the source. |
| `STATUS_MISSING`, `STATUS_UNKNOWN` | Error | No usable lifecycle status. Fix the source or set `status` in `overrides.json`. A bad status is never turned into Draft silently. |
| `TITLE_MISSING`, `EMPTY_CONTEXT`, `EMPTY_DECISION` | Error | Required content is empty. |
| `FIELD_TOO_LONG` | Error | A field is over 20,000 characters. |
| `DATE_MISSING`, `DATE_IN_FUTURE` | Error | No decision date and no git history, or a date after the import day. |
| `SECRET_PRIVATE_KEY`, `SECRET_TOKEN`, `SECRET_CONNECTION_STRING` | Error | Looks like a secret. Remove it from the source file; do not acknowledge it. |
| `DATE_FROM_GIT` | Warning | No date in the file; the first commit date was used. It is when the file was committed, not necessarily when the decision was made. Confirm or set `decidedOn` in `overrides.json`. Tag `review:date`. |
| `EMPTY_RATIONALE`, `NO_ALTERNATIVES`, `EMPTY_CONSEQUENCES` | Warning | The ADR is thin; `adr_safety_review` will flag it. Tag `review:thin`. |
| `REPLACES_ACTIVE_ADR` | Warning | The file says it replaces another ADR that is still active in the same set. In the HCW wiki these are renumbering leftovers (for example ADR-208004 "replaces" ADR-208007, which is a different, live ADR). Kept as a note, not a supersession. Tag `review:links`. |
| `REPLACES_SELF` | Warning | The file says it replaces itself. Ignored. |
| `POSSIBLE_DUPLICATE` | Warning | An old-numbering and a new-numbering ADR of the same provider read alike (TF-IDF similarity of title and decision text). Probably the same decision recorded twice. If you agree, add the pair to `supersedes` in `overrides.json`: the older one is imported as Superseded and linked. `overrides.suggested.json` holds the best match per ADR. A prompt for a human, never a verdict. |
| `DANGLING_REFERENCE` | Warning | Mentions an ADR that is not in the import. |
| `SANITIZE_GUID`, `SANITIZE_EMAIL` | Warning | A possible tenant, subscription or person detail. The wiki runbook says to remove these. Only line numbers are reported, never the value. Tag `review:sanitize`. |
| `REVIEW_CURRENCY` | Info | Mentions previews, end-of-life, deprecation or years 2027 and later. Check against current vendor documentation. Tag `review:currency`. |
| `REFERENCE_VIA_ALIAS` | Info | Mentions an old number that now has a new id (the report lists the mapping). |
| `SANITIZE_PRIVATE_IP`, `SUPERSEDED_BY_EXTERNAL` | Info | Private IPs in examples; a successor that lives outside the import (kept as text). |

### `overrides.json`

Only humans put decisions here. It is read by `scan`; keep it next to the staging output (or pass `--overrides`).

```json
{
  "status":      { "ADR-XXXX": "Proposed" },
  "decidedOn":   { "ADR-RUNNER-001": "2026-06-20" },
  "supersedes":  { "ADR-203001": "ADR-AZURE-043" },
  "skip":        [ "ADR-AZURE-045" ],
  "acknowledge": { "ADR-004001": [ "SANITIZE_EMAIL" ] }
}
```

`supersedes` maps the new ADR to the one it replaces. `acknowledge` lowers a reviewed error to an accepted one. It is ignored for `SECRET_*` codes: remove the secret from the source file.

## 3. The steps

You need: the .NET 10 SDK, the server running (see [Local development](05-local-dev-and-deploy.md)), and an identity with the **Approver** role (the local `local.settings.json.example` grants Author and Approver). Locally the store is in memory, so stopping the host throws a trial import away. For a deployed environment, load into a separate workspace first (add its id under `Auth__TenantWorkspaces` and pass `--workspace <id>`).

1. **Scan.** From the repo root:
   ```bash
   dotnet run --project tools/AdrMcp.Import -- scan --source ../Proj-ADR_LLMs.wiki --out ./.import/hcw --source-tag source:hcw-wiki
   ```
   Expect: a line such as `Scanned 120 files: 105 ADRs ready to stage ... 0 blocking errors, 87 warnings`. `./.import/hcw` holds `report.md`, `records/`, `plan.json`, `overrides.json` and `overrides.suggested.json`. (`.import/` is not tracked; do not commit staged output, it contains the full ADR text.)
2. **Read `report.md`.** Start with "Issues by code" and "Blocking errors". Fix source files for errors, or record a decision in `overrides.json`.
3. **Decide the supersessions.** Open `overrides.suggested.json`; copy the pairs you agree with into `overrides.json`.
4. **Scan again** (same command). Repeat until the result line says `Ready for a dry run`.
5. **Dry run.** Nothing is written:
   ```bash
   dotnet run --project tools/AdrMcp.Import -- load --staging ./.import/hcw --url http://localhost:7071/runtime/webhooks/mcp --dry-run
   ```
   Expect: `Dry run finished for 105 staged ADRs: would-create=105`. The function key, if the host needs one, is read from the `ADR_MCP_KEY` environment variable; never put it on the command line.
6. **Load.** Same command without `--dry-run`. Expect `created=105`. Running it again must give `unchanged=105`; any `conflict` means the id already exists with different content (imports never overwrite).
7. **Verify** with the checks in section 4.

To undo a local trial: stop the host (press Ctrl+C in its terminal). There is no delete tool by design; a mistaken ADR in a real workspace is archived with `adr_archive` (reason required) and kept in the history.

## 4. Using the MCP tools after the import

Checked on the HCW wiki (2026-10-22, in-memory store, same tool pipeline as the Functions host): 105 loaded, 105 unchanged on re-run, and the tools below returned sensible results.

| Want to | Call | What to expect |
|---|---|---|
| See what came in | `adr_list` with `status` or `tag` | `Accepted` 89, `Proposed` 4, `Superseded` 12 once the suggested supersessions are applied (11 older ADRs plus ADR-AZURE-030); without them 100 / 4 / 1. Paged at 50. |
| Work through review queues | `adr_list` with `tag: review:thin`, `review:currency`, `review:links`, `review:date`, `review:sanitize` | One queue per kind of problem. Counts on the HCW wiki, in that order for the first four: 23, 21, 28 (with the suggestions applied), 13. |
| Find by topic | `adr_search` | Ranked hits; the title and tags weigh most. Superseded ADRs still appear, so read the status. |
| Check one decision | `adr_get`, `adr_history`, `adr_export` | Original decision date as `createdAt`; history first entry `imported`; export is the server's canonical Markdown. |
| Find conflicts | `adr_check_contradictions` | Medium findings with `needsSemanticReview: true`, for example "ADR-208002 uses entra-id vs ADR-204005 managed-identity". These are inferred (confidence 0.4); a person or the `contradiction-reviewer` agent classifies them with `finding_review`. |
| Find weak ADRs | `adr_safety_review` with `adrId` | Missing rollback, rationale or alternatives. Matches the `review:thin` queue. |
| Check against reality | `adr_detect_drift` | **`NoClaims` until you declare claims and upload evidence** (next section). |

### Making drift detection work (the part an import cannot do for you)

Drift and contradiction analysis compare *claims* with *evidence*. The old files contain prose, not declared claims. The server's regex extractor found inferred claims in only about a dozen of the 105 decisions, so `adr_detect_drift` reports `NoClaims` for most of them. To get value:

1. Pick the ADRs that state something checkable in code or infrastructure (Key Vault network rules, workflow naming, forbidden token types, managed identity).
2. Draft claims for each, through proposals so a person approves: the `adr-author` agent (or you) calls `adr_propose_change` with `field: "claims"` and `proposerType: "agent"`; an Approver confirms with `adr_approve_change`. Agent proposals are capped at confidence 0.6 by design.
3. Upload the real repositories' Terraform, Bicep and workflow files as evidence (`file_upload_request`, upload, `file_register_upload`, `evidence_attach`).
4. Run `technology_refresh` and `adr_detect_drift`; the `drift-investigator` agent explains each Drifted or Uncertain claim.

## 5. What the first scan found in the HCW wiki

Facts from the scan of `Proj-ADR_LLMs.wiki` on 2026-10-22 (105 ADRs; 15 other pages skipped: the template, runbook, index, landing pages and prompts):

- **Three layouts:** 48 Tyree and Akerman, 44 MADR with a metadata table, 13 MADR with a `## Status` section (all `ADR-PLATFORM-*` and `ADR-RUNNER-*`).
- **Status:** 100 Accepted, 4 Proposed, 1 Superseded. The Superseded one (`ADR-AZURE-030`) points at two ADRs in another repository.
- **13 ADRs have no date** (the PLATFORM and RUNNER files). They fall back to the git date 2026-06-30, which is when the files were committed.
- **Possible copies:** 12 new-numbering ADRs read like an old-numbering ADR of the same provider (for example ADR-203001 and ADR-AZURE-043, same title). Importing both as Accepted would double every finding, so decide these first.
- **Renumbering leftovers:** 7 files say they replace an ADR number that is now a different live ADR; 3 say they replace themselves.
- **Missing ADRs:** `adr-index.md` lists ADR-AZURE-008, 016 and 017 as Proposed, and other files cite 008, 009, 010, 016 and 017, but no such files exist in the folder (the Azure sequence has no files for 008 to 010 and 016 to 022).
- **Thin ADRs:** 22 have no rationale and 23 list no alternatives, mostly the older and PLATFORM/RUNNER files.
- **Sanitising:** ADR-203001 contains what looks like an enterprise tenant ID (twice); ADR-004001 contains an email address in an example; three networking ADRs contain private IP ranges. The wiki runbook says to remove these. Fix them in the source before loading if the wiki is shared.
- **Approvers:** the old files name a body ("Architecture Review Board"), not people, so `approvers` is empty for the Tyree layout and a body name for the others. If you need named approvers, set them after import with `adr_update` (Proposed) or `adr_propose_change`.

## 6. Design notes

- **Why a new tool?** `adr_create` accepts only Draft or Proposed (a new decision must go through review). Loading 100 already-Accepted decisions that way would stamp them with today's date and Approver names that never approved them. `adr_import` keeps the id, status and decision date and is limited to the Approver role.
- **Why it is safe to re-run.** Same id with the same content is `unchanged`; same id with different content is `conflict`. It never overwrites, so an imported Accepted ADR keeps its immutability rule (change it by superseding).
- **Where the control is.** `adr_import` is a deliberate exception to "new ADRs start as Draft or Proposed". It needs `Permission.AdrImport` (Approver and Admin), writes an audit record, and every ADR it creates carries provenance `adr_import`. If you do not want that exception in production, give the role to nobody there (Entra app role mapping) and use it only in a trial workspace.
- **Limits.** 25 records per call, 200,000 characters per Markdown document, 20,000 per field.
