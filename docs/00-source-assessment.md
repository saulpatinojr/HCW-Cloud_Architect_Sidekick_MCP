# A. Assumptions and source assessment

_Date-stamped 2026-10-07. Everything marked **[VERIFY]** should be re-checked against the linked page before production use._

## A.1 Sources inspected

| Source | What it says (as of inspection) | Date on page |
|---|---|---|
| [Tutorial: Host an MCP server on Azure Functions](https://learn.microsoft.com/azure/azure-functions/functions-mcp-tutorial) | Two hosting options: **MCP extension server** (bindings-based, all Functions languages) and **self-hosted server** (official MCP SDK project run as a custom handler, **preview**, needs `AzureWebJobsFeatureFlags=EnableMcpCustomHandlerPreview`). Endpoint for the extension is `/runtime/webhooks/mcp` (Streamable HTTP). Default protection is the `mcp_extension` **system key** in `x-functions-key`; **built-in MCP authentication** (App Service Authentication implementing the MCP authorization spec with Entra ID and protected resource metadata) is **preview** and switches the webhook level to `Anonymous`. Hosting plan in the tutorial: **Flex Consumption**. | ms.date 2026-08-19, updated 2026-09-25 |
| [MCP bindings overview](https://learn.microsoft.com/azure/azure-functions/functions-bindings-mcp) | NuGet `Microsoft.Azure.Functions.Worker.Extensions.Mcp` (isolated worker only). Requires `Microsoft.Azure.Functions.Worker` >= 2.1.0, Core Tools >= 4.0.7030. `host.json` `extensions.mcp` supports `instructions`, `serverName`, `serverVersion`, `encryptClientState`, `messageOptions`, `system.webhookAuthorizationLevel` (System \| Anonymous). SSE transport is deprecated by newer protocol versions; Streamable HTTP is the default. SSE relies on Queue storage in `AzureWebJobsStorage` (identity needs Storage Queue Data Contributor + Message Processor). | ms.date 2026-08-25 |
| [MCP tool trigger reference](https://learn.microsoft.com/azure/azure-functions/functions-bindings-mcp-tool-trigger) | `[McpToolTrigger(toolName, description)]` binds to `ToolInvocationContext` (Name, Arguments, SessionId, Transport) or a POCO. Tool properties can be declared with `[McpToolProperty]`, a POCO with `[Description]`/`required`, or `builder.ConfigureMcpTool(name).WithProperty(...)`. Properties are optional by default since `1.0.0-preview.7`. | ms.date 2025-08-29, updated 2026-06-12 |
| NuGet (queried live) | `Microsoft.Azure.Functions.Worker.Extensions.Mcp` latest stable **1.6.0** (targets net8.0, depends on Worker 2.51.0). GA versions since 1.1.0. The companion `Microsoft.Azure.Functions.Worker.Extensions.Mcp.Sdk` (rich `CallToolResult` types) is still **1.0.0-preview.4** and is **not used** here. `WithInputSchema`/`WithOutputSchema` on the tool builder exist in the core extension (>= 1.5.0) and are used to advertise exact JSON Schemas. | 2026-10-07 |
| [Azure-Samples/remote-mcp-functions-dotnet](https://github.com/Azure-Samples/remote-mcp-functions-dotnet) | Sample reads `ToolInvocationContext.Arguments` (values are `JsonElement`), uses `HttpTransport.Headers` to read `X-MS-CLIENT-PRINCIPAL` / `X-MS-TOKEN-AAD-ACCESS-TOKEN` injected by built-in auth, and provisions Flex Consumption with identity-based `AzureWebJobsStorage__*` settings. Sample targets net10.0; .NET 8 is still supported by Functions v4 (end of support 2026-11-10) **[VERIFY before Nov 2026]**. | main branch, 2026-10-07 |
| [Configure MCP server authorization (App Service)](https://learn.microsoft.com/azure/app-service/configure-authentication-mcp) | Built-in auth validates tokens before the MCP `initialize` request; PRM is exposed via `WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES` (**preview**). Entra ID does **not** support Dynamic Client Registration: clients need a pre-registered client id and should be pre-authorized on the app registration. Authorization is **server-level**, not per tool. | ms.date 2025-11-04 |
| [mcp-adr-analysis-server](https://github.com/tosin2013/mcp-adr-analysis-server) + [Your First MCP ADR Analysis](https://raw.githubusercontent.com/tosin2013/mcp-adr-analysis-server/main/docs/tutorials/01-first-steps.md) | Node.js 20 / TypeScript, **MIT** license ("Copyright (c) 2025 MCP ADR Analysis Server"). ~63 tools; concepts: ecosystem analysis, ADR discovery/suggestion/generation, validation against implementation (drift), content masking, keyword-scored session memory, confidence scoring (0–1), "CE-MCP" mode where tools return orchestration directives for the host LLM instead of calling a model themselves. Dependencies: tree-sitter (MIT), ripgrep (MIT), fast-glob (MIT). Local-filesystem, single-user, stdio server. | main branch, 2026-10-07 |
| [Claude Code MCP docs](https://code.claude.com/docs/en/mcp) | `claude mcp add --transport http <name> <url> [--header "k: v"]`; OAuth via `/mcp` or `claude mcp login`; `--client-id/--client-secret` for pre-registered OAuth clients; `.mcp.json` project scope with `${VAR}` expansion; SSE deprecated. | 2026-10-07 |
| [Claude custom connectors (remote MCP)](https://support.claude.com/en/articles/11175166-getting-started-with-custom-connectors-using-remote-mcp) | Claude.ai, Claude Desktop, Cowork and mobile support **remote MCP custom connectors**; auth options: OAuth (Claude's published identity, automatic registration, or custom OAuth client), **fixed request headers** (API keys / bearer), or none. Claude connects **from Anthropic's cloud**, so the endpoint must be publicly reachable (or allow-listed). Local stdio servers in `claude_desktop_config.json` are a separate mechanism. | 2026-10-07 |

## A.2 Version-sensitive / preview features (explicit)

| Feature | Status 2026-10-07 | Impact on this design |
|---|---|---|
| Azure Functions MCP extension (`Worker.Extensions.Mcp` 1.6.0) | GA | Core of the server. |
| `.Mcp.Sdk` rich result types | Preview (1.0.0-preview.4) | Not used; tools return JSON strings. Upgrade path noted in ADR-0001. |
| Built-in MCP authentication / PRM | **Preview** | Supported via `Auth:Mode=EntraBuiltIn`; default deployment uses the GA system key. |
| Self-hosted MCP SDK server on Functions | **Preview** | Rejected (ADR-0001). |
| `WithInputSchema/WithOutputSchema` | Available in >= 1.5.0 (GA package) | Used; verified by compiling against 1.6.0 and by the local host smoke test. |
| Flex Consumption `functionAppConfig` | GA | Bicep uses `Microsoft.Web/sites@2024-04-01`. **[VERIFY]** API version drift. |
| Microsoft Defender for Storage malware scanning (blob index tag `Malware Scanning scan result`) | GA; tag name **[VERIFY]** against the Defender docs | Optional, gated by `Uploads:RequireDefenderScanResult`. |
| Cosmos DB `UseSystemTextJsonSerializerWithOptions` | GA (SDK 3.63.2) | Used so one JSON configuration serves MCP payloads and persistence. |
| .NET 8 isolated worker | Supported; .NET 8 LTS ends 2026-11-10 | Target as requested; moving to .NET 10 is a one-line change (`TargetFramework`). |

## A.3 Assumptions

1. **Claude is the only LLM in the loop.** The server never calls a model itself in the MVP. Semantic judgement over deterministic candidates is delegated back to the MCP client (Claude) and recorded by a human through `finding_review`. This mirrors the source project's CE-MCP mode and removes any need for model API keys on the server.
2. One Function App serves one organization (one Entra tenant) but supports many **tenants/workspaces** logically. Cross-organization multi-tenancy would add per-tenant storage accounts; it is a later-phase item.
3. Authentication happens at the edge (system key or built-in auth). Authorization (roles → permissions, workspace membership) happens inside the server from the injected principal, never from tool arguments.
4. Evidence files are untrusted. They are never executed, never fetched from arbitrary URLs, and only their **sanitized** text is handed to analyzers or returned (as truncated excerpts) to the client.
5. Repository scanning of live Git hosts is **later phase** (`file_import_from_repository` is specified, not implemented): the MVP ingests files pushed by the client through direct-to-Blob uploads.
6. Reasonable defaults are chosen where the prompt allowed: 50 MiB upload cap, 15-minute upload grants, 24 h idempotency window, 30-day job retention, 180-day memory retention, 365-day audit retention.

## A.4 Reuse vs port vs re-implement (relative to mcp-adr-analysis-server)

| Capability in the source project | Decision | Why |
|---|---|---|
| ADR Markdown conventions (MADR-style sections, status) | **Reused as a format**; parser re-implemented in C# (`AdrMarkdown`) | Format is not code; MIT would allow copying but no code was copied. |
| Confidence scoring 0–1 with high/medium/low bands | **Reused as a concept**; implemented as `Confidence` with an explicit basis (Observed / Declared / Inferred / Uncertain) and an inferred ceiling of 0.6 | Adds provenance the source lacks. |
| Content masking (secrets/PII) | **Re-implemented** as `CompositeContentSafetyService` with deterministic patterns, validation (Luhn, IBAN mod-97), allow-list fingerprints and prompt-injection neutralization | Needed server-side, pre-storage and pre-LLM; source masks only in LLM prompts. |
| Session/tool-usage tracker with keyword scoring | **Ported in spirit** to `DecisionMemoryService` over Cosmos DB with tenant isolation, TTL, principal hashing and explainable scores | Source is a local JSON file. |
| Technology/ecosystem detection | **Re-implemented** as deterministic parser plugins (`ITechnologyDetector`) | Source relies on tree-sitter/ripgrep native binaries and the host LLM; native binaries are not a fit for Flex Consumption and LLM output would be inferred. |
| Drift / "validate ADR against implementation" | **Re-implemented** as claims + `IDriftDetector` plugins with history | Source returns LLM prompts; this design produces evidence-backed deterministic results. |
| Knowledge graph / aggregator SaaS sync | **Not ported** | Proprietary SaaS tier; out of scope. |
| Tree-sitter / ripgrep AST & text search | **Not ported** (cannot run safely/reliably in Functions) | Native modules, cold-start cost, and no sandbox for untrusted repos. A later phase can run them in a container job. |
| Local filesystem project path (`PROJECT_PATH`) | **Rejected** | A remote multi-tenant server must never take filesystem paths from the client. |
| `generate_adr_from_decision`, `suggest_adrs` | **Transformed**: Claude drafts; `adr_create`/`adr_propose_change` persist with human ownership | Keeps generation on the client side (no server-side model). |

## A.5 Licensing and attribution

- **mcp-adr-analysis-server**: MIT (Copyright (c) 2025 MCP ADR Analysis Server). **No source code was copied.** Concepts and tool semantics were used as a functional baseline; see `THIRD-PARTY-NOTICES.md` for the courtesy attribution and the full MIT text. If any code is later ported verbatim, the copyright line and permission notice must be retained in that file.
- **Microsoft.Azure.Functions.Worker.Extensions.Mcp**: MIT (Microsoft). NuGet consumption; no source copied.
- **Azure-Samples/remote-mcp-functions-dotnet**: MIT. Used only as API reference; no code copied.
- **Newtonsoft.Json 13.0.4**: MIT; required explicitly by the Cosmos SDK build check; serialization uses System.Text.Json. Pinned above CVE-2024-21907.
- **Test libraries**: xunit (Apache-2.0), NSubstitute (BSD-3). FluentAssertions was deliberately **not** used (v8 moved to a paid license for commercial use).
- This repository is licensed under MIT (`LICENSE`).
