# ADR-0001: Azure Functions MCP extension instead of a self-hosted MCP SDK server

- Status: Accepted
- Date: 2026-10-07
- Owners: Platform architecture
- Tags: mcp, hosting, azure-functions

## Context
Azure Functions offers two ways to host a remote MCP server (tutorial dated 2026-08-19): the **MCP extension** (GA, `Microsoft.Azure.Functions.Worker.Extensions.Mcp` 1.6.0, bindings-based tool triggers, platform-managed `/runtime/webhooks/mcp` endpoint, system-key or built-in Entra auth) and a **self-hosted** server built with the official MCP SDK and run as a custom handler (**preview**, requires `AzureWebJobsFeatureFlags=EnableMcpCustomHandlerPreview`). The requirement prefers the extension but asks us to confirm it does not block essential functionality: exact JSON Schema contracts, access to the authenticated principal, structured errors, large uploads, long-running analysis.

## Decision
Use the **Azure Functions MCP extension** with the .NET 8 isolated worker. Every tool is an `[McpToolTrigger]` function bound to `ToolInvocationContext`; arguments are bound by our own pipeline; exact input/output JSON Schemas are advertised with `ConfigureMcpTool(name).WithInputSchema(...).WithOutputSchema(...)` (available in the GA package since 1.5.0). Results are returned as JSON text (`ToolEnvelope`). The preview `.Mcp.Sdk` package (rich `CallToolResult`) is not used.

## Rationale
- Every essential capability is available: headers (`HttpTransport.Headers`) expose the built-in-auth principal; schemas are explicit; errors are structured in the envelope; uploads bypass MCP by design; long work is queued.
- GA runtime, platform-managed transport (Streamable HTTP, SSE fallback), keys/auth handled by the platform, and the same deployment/monitoring story as any Function App.
- The self-hosted path is preview, adds a custom-handler process boundary, and would reimplement auth, scaling and session handling that the extension already provides.

## Considered alternatives
1. Self-hosted MCP SDK server as a Functions custom handler — rejected: preview, more moving parts, no binding model for queue/timer in the same app.
2. MCP SDK server on Azure Container Apps — viable, but violates the Functions constraint and loses consumption billing for bursty MCP traffic.
3. Hybrid: extension for tools + container job for heavy scans — kept as a **later-phase option** for tree-sitter/ripgrep-class workloads.

## Consequences
- Tool results are JSON strings; clients parse the envelope (documented in F). When `.Mcp.Sdk` reaches GA we can return `CallToolResult.StructuredContent` without changing handlers (the envelope already matches the output schema).
- Per-tool authorization is implemented inside the pipeline because platform auth is server-wide.
- We depend on extension behaviour verified on 2026-10-07 (local host smoke test): tools/list advertises our schemas, `Arguments` values are `JsonElement`.

## Rollback
Switch the Functions project to the self-hosted template and reuse `ToolHandlers` + `ToolCatalog` unchanged (the handlers are transport-agnostic and already tested without the host).
