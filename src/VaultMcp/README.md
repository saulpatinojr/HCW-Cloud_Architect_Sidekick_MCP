# Obsidian vault MCP server (ADR-0010)

A small, read-only MCP server that lets Claude Code and Claude Desktop search and read the notes in your Obsidian vault. It reads the vault folder directly: Obsidian does not need to be running and no Obsidian plugin is needed. It has **no dependency on the ADR server** and the ADR server has none on it; each works alone. Design and reasons: [ADR-0010](../../docs/adr/ADR-0010-obsidian-second-brain.md).

Two hosts share the same core and the same four tools: `VaultMcp.Stdio` for a laptop (phase 1, below) and `VaultMcp.Http` for a container in Azure (phase 2, Route A; [further down](#http-host-and-container-phase-2-route-a)).

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

## HTTP host and container (phase 2, Route A)

`VaultMcp.Http` serves the same four tools over the MCP streamable HTTP transport at `/mcp`, plus `GET /health`. It is meant for one container in Azure Container Apps that sits next to Obsidian's headless Sync client and reads the synced copy from a mounted share. Settings (environment variables; `__` stands for `:`):

| Setting | Default | Meaning |
|---|---|---|
| `Vault__Path` (or `VAULT_PATH`) | none, required | the folder that holds the vault copy (`/vault` in the image) |
| `Vault__Auth__Mode` | `Locked` | `Locked`: every call to `/mcp` is refused with 401 until a sign-in layer is configured (step 3 of phase 2). `Anonymous`: no sign-in, for a machine you control only; the host refuses to start in this mode when `CONTAINER_APP_NAME` is set, which Azure Container Apps always sets |

It fails closed on purpose: a deployment with no sign-in yet serves nothing but `/health`.

Run it on your machine:

```bash
dotnet build src/VaultMcp/VaultMcp.sln -c Release
Vault__Path="<vault>" Vault__Auth__Mode=Anonymous ASPNETCORE_URLS=http://127.0.0.1:8080 \
  dotnet src/VaultMcp/VaultMcp.Http/bin/Release/net10.0/VaultMcp.Http.dll
```

You should see a warning that the mode is Anonymous, then `Now listening on: http://127.0.0.1:8080`. `curl http://127.0.0.1:8080/health` answers `{"status":"ok","mode":"Anonymous","settingsNote":true}` (`settingsNote` says whether `adr-sidekick.md` was found). On Windows set the three variables with `$env:` first.

Build and run the container image (from the repository root, because the central build settings live there):

```bash
docker build -f src/VaultMcp/Dockerfile -t vault-mcp .
docker run --rm -p 8080:8080 -v "<vault>:/vault:ro" -e Vault__Auth__Mode=Anonymous vault-mcp
```

The image runs as the non-root `app` user on port 8080 and never writes to `/vault`, so a read-only mount is enough. The `vault-mcp` workflow builds this image on every change and drives it over HTTP with the smoke test below.

## Tests

```bash
dotnet test src/VaultMcp/VaultMcp.sln
python3 src/VaultMcp/smoke-test.py --dll src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll
python3 src/VaultMcp/smoke-test.py --url http://127.0.0.1:8080/mcp --private-note Personal/Diary.md
```

The smoke test speaks the MCP protocol the way a client does and prints one `OK` line per check, ending in `ALL OK`. With `--dll` it starts the stdio host itself (a throw-away vault is created unless you add `--vault "<vault>"`); with `--url` it talks to a running HTTP host, and `--private-note` names one path outside your allow-list that must be refused.

The unit tests run against a throw-away vault on disk and cover: nothing readable without the settings note; allow-listed folders only; path climbing and absolute paths refused; symbolic links refused; named pipes never opened; hidden folders ignored; tags and links inside code ignored; truncation; backlinks by name, alias and path; the tool list is exactly the four read tools with `readOnly` set; and the HTTP host locked by default, serving the tools in Anonymous mode, and refusing Anonymous under Container Apps.

## Not yet

- Sign-in for the HTTP host: it stays `Locked` until step 3 of phase 2 adds the Entra layer ([ADR-0008](../../docs/adr/ADR-0008-entra-oauth-sign-in-for-mcp-clients.md) pattern, the vault's own app registration).
- The headless Sync container next to it (step 2) and the Bicep that deploys both (step 1b).
- Uploading notes to the ADR server as evidence (level B in ADR-0010).
