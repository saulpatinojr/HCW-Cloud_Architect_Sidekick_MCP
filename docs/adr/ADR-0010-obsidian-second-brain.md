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

1. **A standalone vault MCP server (`obsidian-vault-mcp`)**, a small local process that reads the vault folder directly. It needs neither Obsidian running nor an Obsidian plugin. Read-only tools: `vault_search` (keywords over a folder allow-list), `vault_note_get` (one note, truncated excerpt), `vault_backlinks` (notes that link to a note), `vault_tags` (notes with a tag). **It has no write tool, by the owner's decision**; it never creates, edits or deletes a file in the vault. It has no dependency on this repository's code and ships with its own tests. It lives in this repository under `src/VaultMcp/` with its own solution file (`VaultMcp.sln`), because phase 2 shares the Bicep modules, naming tests and CI shape; `AdrMcp.sln` never references it, so the ADR server's build and tests are unaffected.
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
2. **Clients:** Claude Code and Claude Desktop, and (added later the same day) **the Claude app on the phone**. The phone is phase 2 below.
3. **Sync:** Obsidian Sync, more than one machine. Hence the in-vault settings note.
4. **Read-only.** No write tool.

## Phase 2: the phone (cloud lane)
The Claude app on a phone talks only to remote MCP servers, through the same connectors as Claude.ai in a browser. So for the phone the vault server must run in Azure, and the notes must reach Azure. Obsidian Sync cannot carry them there: it is end-to-end encrypted, has no server-side read API and no headless client. A device that holds the decrypted vault has to push a plain copy of the allow-listed folders. On a phone that means an Obsidian community plugin that syncs to cloud storage (`Remotely Save` is the usual one; whether it writes to Azure Blob Storage directly, and in its free tier, is **to verify** against the plugin's current backend list; the fallback is OneDrive in the same tenant, read by the Function through Microsoft Graph).

Design, kept standalone:
1. **One codebase, two storage backends.** The vault server's core (allow-list, search, excerpts, backlinks, tags) reads an abstract file store. Phase 1 binds it to a local folder over stdio; phase 2 binds the same core to a Blob container over HTTP, hosted as its own Azure Functions app with the MCP extension, the same stack as the ADR server. Still no write tool in either mode.
2. **Its own resources, named by [ADR-0009](ADR-0009-azure-naming-convention.md)** with workload `vault` (for example `func-vault-dev-scus-01`, `stvaultdevscus01`): a Function app, a storage account with one private container, managed identity, Key Vault only if a plugin credential is needed. It shares nothing with the ADR server at runtime; it shares the Bicep modules, the naming tests and the CI shape, so it lives in this repository under its own solution file that `AdrMcp.sln` never references.
3. **Sign-in:** [ADR-0008](ADR-0008-entra-oauth-sign-in-for-mcp-clients.md) applies unchanged: App Service Authentication with Entra ID, the same pre-registered client apps, a second server app registration with its own `user_impersonation` scope and one app role (`Vault.Reader`). The owner is the only assignment.
4. **The allow-list still lives in the vault** (`adr-sidekick.md`). The Blob copy carries it, so phone and laptops read the same list. The sync plugin pushes the folders it is told to; the server additionally refuses anything outside the allow-list, so a plugin misconfiguration leaks nothing through the tools. It can still leave a plain copy in the container, which is the owner's private storage, encrypted at rest and readable only by the Function's identity.
5. **Freshness:** a note is as fresh as the last push from a device; every excerpt carries the file's modified time.

**Prerequisites before phase 2 can be used:** checklist item F3 in [docs/12](../12-entra-sign-in-checklist.md) (Claude.ai and Desktop sign in through Entra) must pass, because the phone uses the same connector; and the sync plugin's backend and its mobile behaviour must be verified on the owner's phone. Neither is done. Whether the Claude mobile app shows custom connectors added on claude.ai is also **to verify** on the owner's phone.

**Why not instead:** a cloud-only vault with no Obsidian Sync (rejected: the owner has Sync and uses it between machines; the cloud lane is an export, not a replacement); the ADR server reading the container itself (rejected: breaks standalone in both directions, and mixes personal notes into the ADR server's trust boundary; the existing evidence upload path remains the way notes reach the ADR server, level B); Obsidian Publish (rejected: it makes notes public).

## Gates before this ADR can be Accepted
1. A prototype of the vault server passes its own tests with the ADR server stopped, and its tool list shows only the four read tools.
2. `dotnet test AdrMcp.sln` is green with no reference to the vault server.
3. On a second machine with the vault synced, the same `adr-sidekick.md` allow-list applies without any local setup beyond the vault path.
4. A note outside the allow-list is not returned by `vault_search` or `vault_note_get`.
5. Level B, if built: a note holding a fake secret is refused by the existing scanner on upload.
6. Phase 2: the same tests pass against the Blob backend (Azurite locally); an unauthenticated call to the cloud vault server gets 401; the phone signs in and lists the four tools.

## Rollback
Remove the optional `.mcp.json` entry and stop the vault server. Level B evidence is removed with `evidence_remove` like any other evidence. No ADR field was ever written by the integration, so nothing else changes.
