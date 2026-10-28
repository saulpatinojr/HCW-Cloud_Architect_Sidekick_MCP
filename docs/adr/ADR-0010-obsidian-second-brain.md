# ADR-0010: An Obsidian vault as optional context for the ADR server, through a standalone local MCP server

- Status: Proposed (drafted 2026-10-28 at the owner's request; four questions in "Open questions" must be answered by the owner before this can move to Accepted)
- Date: 2026-10-28
- Owners: Platform architecture
- Tags: obsidian, context, mcp, evidence, local

## Context
The owner keeps a "second brain" in Obsidian (a note-taking app whose vault is a folder of Markdown files on the owner's machine) and wants its notes available as context when working with the ADR server ("Architecture Sidekick"). Two requirements were stated on 2026-10-28:

1. There must be an option to use the vault together with the ADR server.
2. The vault integration must also work on its own, well, without depending on the ADR server; and the ADR server must keep working without the vault.

**Facts that shape the design:**
- The ADR server runs on Azure Functions and reaches only what is in Cosmos DB, Blob Storage and the MCP request. It cannot read a folder on a laptop, and [docs/04](../04-security-threat-model.md) says it should not be able to.
- A vault holds personal material next to work notes. Anything that leaves the machine must be chosen deliberately.
- [CLAUDE.md](../../CLAUDE.md): human-owned ADR fields are never written by analyzers; anything inferred is capped at confidence 0.6 and labelled `Inferred`; tenant and workspace come from the principal, never from arguments.
- The server already has an evidence path (`file_upload_request`, `file_register_upload`, `evidence_attach`) with secret scanning, tenant isolation and a size cap, and a memory path (`decision_memory_search`) over `IMemoryRepository`. [ADR-0006](ADR-0006-deterministic-first-analysis.md) keeps analysis deterministic first.
- MCP clients can connect to several servers at once. Claude Code, Claude Desktop and VS Code can reach a server on the same machine (stdio); Claude.ai in a browser cannot.

## Decision
Two pieces, each useful alone, joined only by a setting that is off by default.

1. **A standalone vault MCP server (`obsidian-vault-mcp`)**, a small local process that reads the vault folder directly. It needs neither Obsidian running nor an Obsidian plugin. Read-only tools: `vault_search` (keywords over a folder allow-list), `vault_note_get` (one note, truncated excerpt), `vault_backlinks` (notes that link to a note), `vault_tags` (notes with a tag). It has no dependency on this repository's code and ships with its own tests. It lives in a separate repository, or in `tools/` here only if the owner prefers one repository; either way the ADR server's build and tests never reference it.
2. **Integration level A (no server change):** the client connects to both servers, and the `adr-author` and `drift-investigator` agents may cite vault notes in the Context and Rationale they draft. The citation names the note path; the ADR server never sees the vault.
3. **Integration level B (opt-in, server-side):** notes the owner tags (default tag `#adr-context`) inside allow-listed folders are uploaded as evidence through the existing upload path, by the vault server or a small sync command, so the existing secret scanner, size caps and tenant isolation apply unchanged. Evidence kind `note`. The server treats such evidence like any other: detectors may read it; the result is `Inferred`, confidence ≤ 0.6.
4. **The switch.** Level B is on only when the vault server is started with an explicit allow-list and the ADR server URL; the ADR server has no setting for it and does not know whether a vault exists. With the switch off, both pieces behave exactly as before.
5. **Never:** a vault note writes an ADR field. A change the owner wants from a note goes through `adr_propose_change` and a human approval, like every other change.

## Rationale
- Reading the vault locally keeps personal material on the machine by default and makes the vault tool useful to any MCP client, with or without this server (requirement 2).
- Reusing the upload path for level B means no new trust boundary, no new scanner and no new storage: the vault is just another evidence source (requirement 1), and `HumanOwnedFieldProtectionTests` keep applying.
- A separate process with no shared code is the simplest way to guarantee that neither piece can break the other.

## Considered alternatives
- **The ADR server reads the vault through Obsidian Sync, OneDrive or a git remote.** Rejected: it would put a copy of the whole vault, personal notes included, within reach of a cloud service, and adds credentials to rotate.
- **An Obsidian community plugin that calls the ADR server.** Rejected for now: it depends on Obsidian running, on plugin API changes, and it would be the only MCP client that cannot sign in with Entra ID the way the others do ([ADR-0008](ADR-0008-entra-oauth-sign-in-for-mcp-clients.md)).
- **One combined server.** Rejected: it breaks requirement 2 in both directions.

## Consequences
- A second server to install and update on each machine; `.mcp.json` and `.vscode/mcp.json` gain an optional entry.
- Claude.ai in a browser gets no vault access (level A needs a local server). It still benefits from level B evidence.
- Level B uploads count against the evidence size cap and the storage cost like any file.
- A new evidence kind `note` and, if the owner wants it, a small `IEvidenceSetProvider` filter so drift detectors can be told to ignore notes.

## Open questions (owner)
1. "Standalone" covers both directions above. Confirm, or say which one direction was meant.
2. Which clients must have the vault: Claude Code and Desktop and VS Code (local), or also Claude.ai (browser, level B only)?
3. Is the vault synced (Obsidian Sync, git, OneDrive), and used on more than one machine? This decides where the allow-list lives.
4. Read-only, or may the vault server also write (for example, drop a note into an `Inbox/` folder when an ADR is accepted)?

## Gates before this ADR can be Accepted
1. The four questions answered.
2. A prototype of the vault server passes its own tests with the ADR server stopped.
3. `dotnet test AdrMcp.sln` is green with no reference to the vault server.
4. Level B: a note holding a fake secret is refused by the existing scanner on upload.

## Rollback
Remove the optional `.mcp.json` entry and stop the vault server. Level B evidence is removed with `evidence_remove` like any other evidence. No ADR field was ever written by the integration, so nothing else changes.
