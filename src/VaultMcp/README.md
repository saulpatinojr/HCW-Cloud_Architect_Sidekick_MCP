# Obsidian vault MCP server (ADR-0010, phase 1)

A small, read-only MCP server that lets Claude Code and Claude Desktop search and read the notes in your Obsidian vault. It reads the vault folder directly: Obsidian does not need to be running and no Obsidian plugin is needed. It has **no dependency on the ADR server** and the ADR server has none on it; each works alone. Design and reasons: [ADR-0010](../../docs/adr/ADR-0010-obsidian-second-brain.md).

**What it never does:** write, create, move or delete a note; read a folder you did not allow-list; follow a symbolic link out of the vault.

## Tools

| Tool | What it answers |
|---|---|
| `vault_search` | Keyword search over the allow-listed folders: ranked notes, matched terms, a one-line excerpt, modified time |
| `vault_note_get` | One note by path or unique name: title, tags, wiki links, a truncated excerpt (`truncated: true` tells you it was cut) |
| `vault_backlinks` | Notes that link to a note with `[[...]]` |
| `vault_tags` | Notes carrying a tag (nested tags match their parent); without a tag, the context tag |

A refusal is answered as `{"error":{"code":"NOT_FOUND","message":"..."}}`. The message is the same whether a note does not exist or sits outside the allow-list, so nothing outside the list is confirmed to exist.

## 1. Put the settings note in your vault

Create `adr-sidekick.md` at the **root of the vault** (next to the `.obsidian` folder). Obsidian Sync carries it to every machine, so you do this once. Until it exists, **nothing is readable**.

```markdown
---
allow:
  - Work
  - Projects/Cloud
tag: adr-context
excerpt_chars: 1200
max_results: 10
---
Folders the ADR sidekick may read. Everything else stays private.
```

- `allow`: folders, relative to the vault root, matched exactly (case matters). Hidden folders and `..` are ignored. There is no "whole vault" entry on purpose.
- `tag`: the default for `vault_tags` (with or without `#`). Default `adr-context`.
- `excerpt_chars` (200 to 20000, default 1200) and `max_results` (1 to 50, default 10).

Changes on disk are picked up within 15 seconds.

## 2. Build once

From the repository root (needs the .NET 10 SDK):

```bash
dotnet build src/VaultMcp/VaultMcp.sln -c Release
```

You should see `Build succeeded` and the file `src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll`. The configuration below runs that file directly rather than `dotnet run`, because `dotnet run` prints build output to standard output, which would corrupt the MCP stream.

## 3. Connect a client

Replace `<repo>` with the full path of this repository and `<vault>` with the full path of your vault folder (the folder that contains `.obsidian`).

**Claude Code** (any terminal):

```bash
claude mcp add vault -- dotnet "<repo>/src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll" --vault "<vault>"
```

Then in a Claude Code session type `/mcp`: `vault` should show as connected with four tools.

**Claude Desktop**: Settings → Developer → Edit Config, add inside `mcpServers`:

```json
"vault": {
  "command": "dotnet",
  "args": ["<repo>/src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll", "--vault", "<vault>"]
}
```

On Windows write paths with doubled backslashes in JSON (`"C:\\Users\\me\\Vault"`). Restart Claude Desktop; the tools icon should list the four `vault_*` tools.

Instead of `--vault` you can set the environment variable `VAULT_PATH`.

To remove: `claude mcp remove vault`, or delete the JSON block.

## Tests

```bash
dotnet test src/VaultMcp/VaultMcp.sln
python3 src/VaultMcp/smoke-test.py --dll src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll
```

The smoke test starts the built server, speaks the MCP protocol to it the way a client does, and prints one `OK` line per check, ending in `ALL OK`. Add `--vault "<vault>"` to run it against your own vault (then it only checks the tool list and one search).

They run against a throw-away vault on disk and cover: nothing readable without the settings note; allow-listed folders only; path climbing and absolute paths refused; symbolic links skipped; hidden folders ignored; tags and links inside code ignored; truncation; backlinks by name, alias and path; and that the tool list is exactly the four read tools with `readOnly` set.

## Not in phase 1

- The phone. The Claude app on a phone reaches only remote servers, so the server and a synced copy of the allow-listed folders have to be in Azure first; see phase 2 in ADR-0010 (Obsidian's headless Sync client in a container is the recommended route).
- Uploading notes to the ADR server as evidence (level B in ADR-0010).
