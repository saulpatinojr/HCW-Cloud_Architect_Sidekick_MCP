# ADR-0010: An Obsidian vault as optional context for the ADR server, through a standalone local MCP server

- Status: Proposed (drafted 2026-10-28 at the owner's request; the owner answered the four questions in "Owner decisions" the same day; the owner moves the status to Accepted after the gates below pass)
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
- The vault is synced with Obsidian Sync (Obsidian's own end-to-end encrypted sync service) and used on more than one machine. Obsidian Sync carries ordinary files in the vault; it does not carry hidden folders other than `.obsidian` unless told to, so a setting that must follow the vault belongs in an ordinary file.

## Decision
Two pieces, each useful alone, joined only by a setting that is off by default.

1. **A standalone vault MCP server (`obsidian-vault-mcp`)**, a small local process that reads the vault folder directly. It needs neither Obsidian running nor an Obsidian plugin. Read-only tools: `vault_search` (keywords over a folder allow-list), `vault_note_get` (one note, truncated excerpt), `vault_backlinks` (notes that link to a note), `vault_tags` (notes with a tag). **It has no write tool, by the owner's decision**; it never creates, edits or deletes a file in the vault. It has no dependency on this repository's code and ships with its own tests. It lives in a separate repository, or in `tools/` here only if the owner prefers one repository; either way the ADR server's build and tests never reference it.
   - **Clients:** Claude Code and Claude Desktop (owner's decision). Both run on the owner's machines and speak to the server over stdio. Other local clients (VS Code, Codex, Kiro) can use it too, but are not tested; Claude.ai in a browser is out of scope.
   - **Settings that follow the vault:** the folder allow-list, the context tag and the excerpt limit live in one ordinary Markdown note at the vault root, `adr-sidekick.md`, as front matter, so Obsidian Sync carries them to every machine. The only per-machine setting is the vault path, given in the client's MCP configuration. A machine with no `adr-sidekick.md` gets an empty allow-list: nothing is readable until the note exists.
2. **Integration level A (no server change):** the client connects to both servers, and the `adr-author` and `drift-investigator` agents may cite vault notes in the Context and Rationale they draft. The citation names the note path; the ADR server never sees the vault.
3. **Integration level B (opt-in, server-side):** notes the owner tags (default tag `#adr-context`) inside allow-listed folders are uploaded as evidence through the existing upload path, by a small sync command (not by the vault server, which stays read-only and has no network access to the ADR server), so the existing secret scanner, size caps and tenant isolation apply unchanged. Evidence kind `note`. The server treats such evidence like any other: detectors may read it; the result is `Inferred`, confidence ≤ 0.6. With the chosen clients, level A already covers the stated need; level B is built only if the owner later wants server-side analysis to see notes.
4. **The switch.** Level A is on when the client's MCP configuration lists the vault server, and off when it does not. Level B is on only when the sync command is run with an explicit allow-list and the ADR server URL; the ADR server has no setting for it and does not know whether a vault exists. With both off, both pieces behave exactly as before.
5. **Never:** a vault note writes an ADR field. A change the owner wants from a note goes through `adr_propose_change` and a human approval, like every other change.

## Rationale
- Reading the vault locally keeps personal material on the machine by default and makes the vault tool useful to any MCP client, with or without this server (requirement 2).
- Reusing the upload path for level B means no new trust boundary, no new scanner and no new storage: the vault is just another evidence source (requirement 1), and `HumanOwnedFieldProtectionTests` keep applying.
- A separate process with no shared code is the simplest way to guarantee that neither piece can break the other.

## Considered alternatives
- **The ADR server reads the vault through Obsidian Sync, OneDrive or a git remote.** Rejected: it would put a copy of the whole vault, personal notes included, within reach of a cloud service, and adds credentials to rotate.
- **An Obsidian community plugin that calls the ADR server.** Rejected for now: it depends on Obsidian running, on plugin API changes, and it would be the only MCP client that cannot sign in with Entra ID the way the others do ([ADR-0008](ADR-0008-entra-oauth-sign-in-for-mcp-clients.md)).
- **One combined server.** Rejected: it breaks requirement 2 in both directions.
- **A write tool (for example, drop a note into an `Inbox/` folder when an ADR is accepted).** Rejected by the owner on 2026-10-28: the vault stays read-only. Revisit as its own ADR if the need appears.
- **Per-machine configuration files for the allow-list.** Rejected: two machines would drift apart silently. A note inside the vault is synced by Obsidian Sync with the rest.

## Consequences
- A second server to install and update on each machine; `.mcp.json` and `.vscode/mcp.json` gain an optional entry.
- Claude.ai in a browser gets no vault access (level A needs a local server). It still benefits from level B evidence.
- Level B uploads count against the evidence size cap and the storage cost like any file.
- Level B only: a new evidence kind `note` and, if the owner wants it, a small `IEvidenceSetProvider` filter so drift detectors can be told to ignore notes.
- The vault server is read-only, so a note it shows can be stale by the time Obsidian Sync catches up on another machine; the excerpt carries the file's modified time so the reader can tell.

## Owner decisions (2026-10-28)
1. **Standalone in both directions:** the vault server works without the ADR server, and the ADR server works without the vault.
2. **Clients:** Claude Code and Claude Desktop.
3. **Sync:** Obsidian Sync, more than one machine. Hence the in-vault settings note.
4. **Read-only.** No write tool.

## Gates before this ADR can be Accepted
1. A prototype of the vault server passes its own tests with the ADR server stopped, and its tool list shows only the four read tools.
2. `dotnet test AdrMcp.sln` is green with no reference to the vault server.
3. On a second machine with the vault synced, the same `adr-sidekick.md` allow-list applies without any local setup beyond the vault path.
4. A note outside the allow-list is not returned by `vault_search` or `vault_note_get`.
5. Level B, if built: a note holding a fake secret is refused by the existing scanner on upload.

## Rollback
Remove the optional `.mcp.json` entry and stop the vault server. Level B evidence is removed with `evidence_remove` like any other evidence. No ADR field was ever written by the integration, so nothing else changes.
