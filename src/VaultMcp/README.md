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

`VaultMcp.Http` serves the same four tools over the MCP streamable HTTP transport at `/mcp`, plus `GET /health`. It is meant for one container in Azure Container Apps that sits next to Obsidian's headless Sync client and reads the synced copy from a mounted share. The Azure side is [below](#deploy-the-http-host-to-azure-phase-2-step-1b). Settings (environment variables; `__` stands for `:`):

| Setting | Default | Meaning |
|---|---|---|
| `Vault__Path` (or `VAULT_PATH`) | none, required | the folder that holds the vault copy (`/vault` in the image) |
| `Vault__Auth__Mode` | `Locked` | `Locked`: every call to `/mcp` is refused with 401. `Anonymous`: no sign-in, for a machine you control only; the host refuses to start in this mode when `CONTAINER_APP_NAME` is set, which Azure Container Apps always sets. `Entra`: Microsoft Entra ID sign-in, the mode for Azure ([step 3 below](#sign-in-with-entra-phase-2-step-3)); needs the settings in that section |

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

## Deploy the HTTP host to Azure (phase 2, step 1b)

The vault server has its own azd project (`src/VaultMcp/azure.yaml`) and its own template (`infra/vault/main.bicep`), separate from the ADR server's at the repository root. One `azd provision` creates one resource group with [ADR-0009](../../docs/adr/ADR-0009-azure-naming-convention.md) names, workload `vault`:

| Resource | Name (defaults, South Central US) | Job |
|---|---|---|
| Resource group | `rg-vault-dev-scus-01` | holds everything below |
| Container app | `ca-vault-dev-scus-01` | the HTTP host: one replica, HTTPS on the internet, **Locked** (401 on `/mcp`) until you turn the sign-in on in step 3 |
| Container Apps environment | `cae-vault-dev-scus-01` | runs it; mounts the share read-only with its managed identity |
| Container registry | `crvaultdevscus01` | holds the image; `azd deploy` builds it there, so you need no Docker |
| Storage account, file shares | `stvaultdevscus01`, shares `vault` and `syncstate` | the synced copy of the allow-listed folders (written by step 2), and the Sync client's own state |
| Key Vault | `kv-vault-dev-scus-01` | the step 2 secrets (vault password, optionally the Sync sign-in), put there by you |
| Log Analytics, managed identity | `log-vault-dev-scus-01`, `id-vault-dev-scus-01` | logs; the identity that pulls the image, mounts the share and reads Key Vault |

No key, connection string or secret appears in the template or its outputs: the image is pulled and the share is mounted with the identity (the share mount uses "Managed Identity for SMB", a preview feature; `azd env set VAULT_SHARE_AUTH AccountKey` is the fallback if your region does not have it yet, and it is the only setting that turns shared-key access on for that storage account).

**You need:** the Azure CLI and the Azure Developer CLI, signed in to a subscription where you can create resource groups and role assignments (Owner, or Contributor plus Role Based Access Control Administrator). Reference: [Install azd](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd).

1. Open a terminal in `src/VaultMcp` (the ADR server's azd project is at the repository root; do not mix the two).
2. Run `azd auth login`, then `azd env new vault-dev`. The name must end with `-dev`, the environment part of every resource name. You should see `.azure/vault-dev/` appear (it is git-ignored).
3. Run `azd env set AZURE_LOCATION southcentralus` (or another region in `infra/region-codes.json`). Optional, for step 2: `azd env set VAULT_DEPLOYER_PRINCIPAL_ID "$(az ad signed-in-user show --query id -o tsv)"` gives you Key Vault Secrets Officer on the new vault, so you can put the Sync secrets there later. It is a separate, opt-in variable on purpose (azd fills `AZURE_PRINCIPAL_ID` on every provision; nobody should get that role by accident). Leave it unset in production.
4. Run `azd provision`. Expect 5 to 10 minutes and, at the end, a list of outputs that includes `VAULT_MCP_ENDPOINT`. The app runs Microsoft's ASP.NET Core sample image as a placeholder until the next step (it listens on the same port 8080, so the revision is healthy). If it stops at the file share link with an authorization error, wait a minute and run it again: a new role assignment can take that long to apply.
5. Run `azd deploy vault`. Expect lines about building the image in the registry, then `Deploying service vault` and the endpoint. The placeholder is replaced by the vault server.
6. Recommended: run `azd provision` once more. The first provision could only declare TCP probes (the placeholder has no `/health`); this one switches to HTTP probes on `GET /health`. It keeps the image you deployed: azd sets `SERVICE_VAULT_RESOURCE_EXISTS=true` once the app exists, `main.parameters.json` passes it as `vaultExists`, and the template then reads the running image instead of the placeholder.
7. Check: `curl -i "$(azd env get-value VAULT_HEALTH_URL)"` answers `200` with `{"status":"ok","mode":"Locked","settingsNote":false}` (`settingsNote` turns true once step 2 syncs `adr-sidekick.md` into the share), and `curl -i "$(azd env get-value VAULT_MCP_ENDPOINT)"` answers `401` with a `WWW-Authenticate: Bearer` header. Nothing is readable yet, by design.

Settings (`azd env set NAME VALUE`, all optional):

| Variable | Default | Meaning |
|---|---|---|
| `VAULT_WORKLOAD_NAME`, `VAULT_ENVIRONMENT_SHORT` | `vault`, `dev` | the words in every resource name (ADR-0009); the workload name must start with `vault` (the ADR server's template refuses such names, so the two can never share a resource); the azd environment name must end with `-<VAULT_ENVIRONMENT_SHORT>` |
| `VAULT_NAME_SUFFIX` | empty | appended to the three globally unique names (registry, Key Vault, storage account) when one is taken, for example a Key Vault still soft-deleted |
| `VAULT_SHARE_AUTH` | `ManagedIdentity` | `AccountKey` only as the fallback described above |
| `VAULT_SHARE_QUOTA_GIB` | `5` | size limit of the share |
| `VAULT_DEPLOYER_PRINCIPAL_ID` | empty | object id of the person who may write the step 2 secrets (Key Vault Secrets Officer); opt-in |

To undo: `azd down` from the same folder deletes the resource group. The Key Vault keeps its name reserved for 90 days (purge protection, as for the ADR server); set `VAULT_NAME_SUFFIX` if you create the environment again within that time. Preview first with `azd provision --preview`.

## Sync the vault into Azure (phase 2, step 2)

The HTTP host reads a copy of your allow-listed folders from the file share. Step 2 fills that share: a second, always-on container app, `ca-vault-sync-dev-scus-01`, runs Obsidian's own headless Sync client (`obsidian-headless`, the `ob` command, an open beta that needs an Obsidian Sync subscription) in **pull-only** mode. It is the same end-to-end encryption as the desktop app, so the container needs your Sync sign-in and the vault's encryption password; both live in the Key Vault the template created and reach the container as secret references, never as settings. The code is in [`sync/`](sync/): `sync.mjs` wraps `ob`, `exclusions.mjs` turns the `allow:` list of `adr-sidekick.md` into Sync's exclusion list (Sync can only exclude folders, so every folder whose subtree holds no allow-listed folder is excluded at the shallowest level), and after each cycle it deletes local copies of folders that are now excluded. It never serves anything and never pushes to Sync.

**Fails closed:** until `adr-sidekick.md` has been synced, the container has no allow-list to derive exclusions from, and an empty exclusion list would pull your whole vault. So the first run needs `VAULT_SYNC_EXCLUDE_SEED`, the top-level folders that must stay private; without it the container logs a refusal every cycle and syncs nothing. After the first sync the note takes over. Root-level notes (outside any folder) are synced but the server never serves them; keep notes in folders. Obsidian configuration, plugins and attachments are not synced (`--configs ""`, `--file-types ""`).

**You need:** step 1b provisioned, an Obsidian Sync subscription, the name of the remote vault (`ob sync-list-remote` on your machine shows it, or Settings → Sync in the app), and the vault's end-to-end encryption password. Reference: [Obsidian Headless](https://obsidian.md/help/sync/headless).

1. Put the secrets into Key Vault, from a terminal signed in with `az login` (you need Key Vault Secrets Officer on `kv-vault-dev-scus-01`: step 3 of the deploy section, `VAULT_DEPLOYER_PRINCIPAL_ID`). Type each value into a private file so it never lands in your shell history, then delete the file:
   ```bash
   KV=$(cd src/VaultMcp && azd env get-value AZURE_KEY_VAULT_NAME)
   read -rs -p "vault password: " P; printf '%s' "$P" > /tmp/s; unset P
   az keyvault secret set --vault-name "$KV" --name obsidian-vault-password --file /tmp/s --query id -o tsv > /dev/null; rm /tmp/s
   ```
   Expect a line with the secret id on success, and nothing else. For a vault with standard (not end-to-end) encryption, skip the password and run `azd env set VAULT_SYNC_VAULT_ENCRYPTED false` instead.
2. Choose how the container signs in to Obsidian Sync. **Recommended: by hand, once.** Skip this step; the container waits for you (step 6). Alternative, for an Obsidian account **without** two-factor authentication: repeat step 1 for `obsidian-email` and `obsidian-password`, then `azd env set VAULT_SYNC_LOGIN_FROM_KEY_VAULT true`.
3. In `src/VaultMcp`: `azd env set VAULT_SYNC_ENABLED true`, `azd env set VAULT_SYNC_REMOTE_VAULT "<remote vault name>"`, `azd env set VAULT_SYNC_EXCLUDE_SEED "<private top-level folders, comma-separated>"`.
4. Run `azd provision`. Expect the new app `ca-vault-sync-dev-scus-01` in the outputs (`AZURE_SYNC_CONTAINER_APP_NAME`). It runs a placeholder image until the next step. If it fails with a Key Vault error, the secret of step 1 is missing or you lack the role on the vault.
5. Run `azd deploy sync`. Expect the image to build in the registry and `Deploying service sync`.
6. If you chose the sign-in by hand: run `az containerapp exec --name "$(azd env get-value AZURE_SYNC_CONTAINER_APP_NAME)" --resource-group "$(azd env get-value AZURE_RESOURCE_GROUP)" --command "ob login"` and answer the prompts (email, password, two-factor code). Expect your account shown; press Ctrl+D. The login is stored on the state share, so a restart does not ask again.
7. Check: `az containerapp logs show --name "$(azd env get-value AZURE_SYNC_CONTAINER_APP_NAME)" --resource-group "$(azd env get-value AZURE_RESOURCE_GROUP)" --tail 50` shows `vault set up for pull-only sync`, `exclusions applied (<n>): ...` and `synced; exclusions from adr-sidekick.md` within a couple of minutes. Then `curl "$(azd env get-value VAULT_HEALTH_URL)"` answers `"settingsNote":true`: the server sees the note. The tools stay locked until step 3 (sign-in) is turned on.

Step 2 settings (`azd env set NAME VALUE`):

| Variable | Default | Meaning |
|---|---|---|
| `VAULT_SYNC_ENABLED` | `false` | deploy the sync app; needs the Key Vault secrets first |
| `VAULT_SYNC_REMOTE_VAULT` | empty | the vault's name (or id) on Obsidian Sync; required when enabled |
| `VAULT_SYNC_EXCLUDE_SEED` | empty | top-level folders excluded before the note is present; the container refuses to sync without it or the note |
| `VAULT_SYNC_VAULT_ENCRYPTED` | `true` | the vault uses end-to-end encryption, so `obsidian-vault-password` must exist |
| `VAULT_SYNC_LOGIN_FROM_KEY_VAULT` | `false` | sign in with `obsidian-email` and `obsidian-password` instead of by hand |
| `VAULT_SYNC_INTERVAL_SECONDS` | `60` | seconds between sync cycles |

To undo: `azd env set VAULT_SYNC_ENABLED false` and `azd provision` removes the sync app; the shares keep their content until you delete them or run `azd down`. The copy on the share is the only place in Azure that holds your notes in clear; it is readable by the two apps' identity and by whoever holds Storage File Data roles on the storage account.

**Not verified yet** (needs a subscription and the beta): where `ob` stores its login on Linux (the state share is mounted as its home directory, which covers the usual places), whether `--excluded-folders` accepts nested paths such as `Projects/Home` (ADR-0010 names this as the fallback point: if not, keep allow-listed folders at the top level), and that a note edited on the phone reaches the share.

## Sign in with Entra (phase 2, step 3)

Entra sign-in is what lets a phone, Claude.ai or a laptop call the server in Azure as **you**, and nobody else. It is the [ADR-0008](../../docs/adr/ADR-0008-entra-oauth-sign-in-for-mcp-clients.md) pattern applied to a second server with its own identity ([ADR-0010](../../docs/adr/ADR-0010-obsidian-second-brain.md), design item 3): the vault server gets its **own app registration** in Microsoft Entra ID (the tenant's sign-in service), one app role `Vault.Reader`, and its own `user_impersonation` scope. An MCP client signs the user in with Entra, gets an access token for the vault server, and sends it as a `Bearer` credential. The host checks the token itself on every call (there is no platform layer in front of it): the signature against the tenant's published keys (RS256 only), the exact v2.0 issuer, the audience (the vault server's client ID), the lifetime, token version 2.0, the tenant, an allowed client application (`azp`), the scope, and the role. A token for the ADR server is refused here (wrong audience), and the other way round. The settings are checked at start; a bad one stops the host instead of weakening a check.

What a client sees (the MCP authorization spec, revision 2026-07-28; the same table as the ADR server's):

| Call | Answer |
|---|---|
| no token | `401`, `WWW-Authenticate: Bearer resource_metadata="https://<app>/.well-known/oauth-protected-resource/mcp", scope="api://<client id>/user_impersonation"`: the client reads the metadata, finds Entra and the scope, and signs the user in |
| a token that is not valid for this server (signature, issuer, audience, expiry, tenant, client, version, shape) | `401`, `error="invalid_token"`; the reason is in the log only, as a fixed word |
| a token without the scope, or an application token (no user) | `403`, `error="insufficient_scope"` |
| a valid token whose user has no `Vault.Reader` assignment | `403` with no challenge: an assignment in Entra fixes it, signing in again does not |
| the tenant's keys cannot be fetched | `503` with `Retry-After: 30`; never acceptance |
| `GET /.well-known/oauth-protected-resource` (and `.../mcp`) | `200`, the metadata (RFC 9728): `resource`, `authorization_servers`, `scopes_supported`; anonymous and cacheable |
| `GET /health` | `200` without a token, as before |

**You need:** step 1b provisioned; the Azure CLI signed in to the tenant the users live in; permission to create app registrations and assign app roles (Application Administrator or Cloud Application Administrator; an administrator can do step 2 for you); the client IDs of the MCP clients you will use (the table in [docs/11](../../docs/11-entra-sign-in.md#clients-and-what-each-needs-registered); the owner checklist [docs/12](../../docs/12-entra-sign-in-checklist.md) decides them). Recommended first: Entra ID P1 and MFA from Part B of that checklist. Run the blocks from the repository root. Never paste a tenant ID, an application ID or a token into chat, an issue or a commit; report step numbers and status codes only.

Scripted alternative: from the repository root, `.\scripts\setup-entra-signin.ps1 -Vault -VaultResourceUri "$(cd src/VaultMcp && azd env get-value VAULT_MCP_ENDPOINT)"` (PowerShell; add `-PreauthorizeAzureCli` for the check in step 6) does steps 1 to 3 below for both servers, creates the public client of step 4, and ends with a `PASS` line per item; it never creates or prints a secret. The steps stay here so you can see what it does.

1. Create the app registration: single tenant, v2 tokens, the role from `scripts/entra/vault-app-roles.json`, the scope from `scripts/entra/vault-oauth2-permission-scopes.json`, the Application ID URI `api://<client id>`, and a service principal that requires assignment (so only people with the role can sign in at all):
   ```bash
   app=$(az ad app create --display-name 'Obsidian vault MCP server' --sign-in-audience AzureADMyOrg --requested-access-token-version 2 --app-roles @scripts/entra/vault-app-roles.json --query appId -o tsv)
   obj=$(az ad app show --id "$app" --query id -o tsv)
   az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$obj" --headers 'Content-Type=application/json' --body @scripts/entra/vault-oauth2-permission-scopes.json
   az ad app update --id "$app" --identifier-uris "api://$app"
   sp=$(az ad sp create --id "$app" --query id -o tsv)
   az ad sp update --id "$sp" --set appRoleAssignmentRequired=true
   az ad app show --id "$app" --query '[appRoles[].value, api.oauth2PermissionScopes[].value, api.requestedAccessTokenVersion]' -o tsv
   ```
   Expect the last command to print `Vault.Reader`, `user_impersonation` and `2`. Keep this terminal open: the next steps use `$app` and `$sp`.
2. Assign yourself `Vault.Reader` (the only role; add other people the same way, one assignment each, or a group once the checklist's Part A settles it):
   ```bash
   me=$(az ad signed-in-user show --query id -o tsv)
   role=$(python3 -c "import json; print(next(r['id'] for r in json.load(open('scripts/entra/vault-app-roles.json')) if r['value'] == 'Vault.Reader'))")
   az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$sp/appRoleAssignedTo" --headers 'Content-Type=application/json' --body "{\"principalId\":\"$me\",\"resourceId\":\"$sp\",\"appRoleId\":\"$role\"}"
   ```
   Expect JSON for the new assignment, with `principalDisplayName` showing your name.
3. Allow the clients. Each MCP client application must be pre-authorized on the vault scope (or Entra asks the user for consent at the first sign-in), and its client ID goes in the allow-list of step 4. For the check in step 6 from your own terminal, pre-authorize Microsoft's public Azure CLI client with `scripts/entra/vault-preauthorize-azure-cli.json`; do this in a sandbox or for an owner-only vault, and remove it afterwards (it lets anyone in the tenant with the Azure CLI *ask* for a token, though only people with the role get one):
   ```bash
   az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$obj" --headers 'Content-Type=application/json' --body @scripts/entra/vault-preauthorize-azure-cli.json
   ```
   For your real clients (the public client of Claude Code and Claude Desktop, and whatever Part F of the checklist settles for Claude.ai and the phone), copy that file, put each client's `appId` in it, and run the same command once per client. No new client app is needed: the client registrations of ADR-0008 are reused; only this server's scope is new to them.
4. Turn the sign-in on, in `src/VaultMcp` (the client list is comma-separated; include the Azure CLI's `04b07795-8ddb-461a-bbee-02f9e1bf7b46` while step 6 needs it):
   ```bash
   azd env set VAULT_AUTH_MODE Entra
   azd env set VAULT_ENTRA_CLIENT_ID "$app"
   azd env set VAULT_ENTRA_ALLOWED_CLIENT_IDS "<client ids, comma-separated>"
   azd provision
   ```
   Expect the outputs to show `VAULT_AUTH_MODE = Entra` and `VAULT_RESOURCE_METADATA_URL`. The tenant is the one you deploy into (override with the Bicep parameter `entraTenantId` only for a different tenant). If the new revision does not become healthy, the host refused a setting at start: `az containerapp logs show --name "$(azd env get-value AZURE_CONTAINER_APP_NAME)" --resource-group "$(azd env get-value AZURE_RESOURCE_GROUP)" --type console --tail 20` prints the line that names it (`Vault:Auth:Entra:... must ...`).
5. Check without a token: `curl -si -X POST -H 'Content-Type: application/json' -d '{}' "$(azd env get-value VAULT_MCP_ENDPOINT)" | head -5` answers `401` with a `WWW-Authenticate: Bearer resource_metadata="..." scope="api://..."` line and no `error=`; `curl -s "$(azd env get-value VAULT_RESOURCE_METADATA_URL)"` prints the metadata JSON; `curl -s "$(azd env get-value VAULT_HEALTH_URL)"` shows `"mode":"Entra"`.
6. Check with a token, from the repository root. Sign in once for the scope (your browser opens; this is the consent Entra asks for), put the token in an environment variable (never on the command line), run the smoke test, then forget the token:
   ```bash
   az login --scope "api://$app/user_impersonation"
   export VAULT_MCP_TOKEN=$(az account get-access-token --scope "api://$app/user_impersonation" --query accessToken -o tsv)
   python3 src/VaultMcp/smoke-test.py --url "$(cd src/VaultMcp && azd env get-value VAULT_MCP_ENDPOINT)" --bearer-from-env --private-note "<a note path outside your allow-list>"
   unset VAULT_MCP_TOKEN
   ```
   Expect `OK initialize`, `OK tools/list` and `ALL OK`. A `403` with `insufficient_scope` means the token was not for this scope (sign in again with the `--scope` line); a `403` with no `WWW-Authenticate` means your user has no `Vault.Reader` assignment (step 2); a `401` with `invalid_token` means the token is for another tenant, client or server (steps 1 and 4).
7. Clients: connecting Claude Code, Claude Desktop, Claude.ai and the phone is [step 4](#connect-the-clients-phase-2-step-4). Nothing in this step connects a client.

Step 3 settings (`azd env set NAME VALUE`):

| Variable | Default | Meaning |
|---|---|---|
| `VAULT_AUTH_MODE` | `Locked` | `Entra` turns the sign-in on; the template then passes the settings below to the container app, with the tenant you deploy into and the app's own address as the resource URI |
| `VAULT_ENTRA_CLIENT_ID` | empty | client (application) ID of the vault server's app registration (step 1); required in Entra mode |
| `VAULT_ENTRA_ALLOWED_CLIENT_IDS` | empty | comma-separated client IDs of the MCP clients allowed to call (step 3); required in Entra mode, because an empty list would accept every client |
| `VAULT_ENTRA_REQUIRED_ROLE` | `Vault.Reader` | the app role a user must hold; change it only together with `scripts/entra/vault-app-roles.json` |

Outside Azure the same settings are environment variables: `Vault__Auth__Mode=Entra`, `Vault__Auth__Entra__TenantId`, `Vault__Auth__Entra__ClientId`, `Vault__Auth__Entra__AllowedClientIds`, `Vault__Auth__Entra__RequiredRole` and `Vault__Auth__Entra__ResourceUri` (the `https://<host>/mcp` address the challenges name; `http://localhost...` is accepted for a local run only). Optional: `Vault__Auth__Entra__Scope` (default `user_impersonation`) and `Vault__Auth__Entra__AuthorityHost` (default `login.microsoftonline.com`).

To undo: `azd env set VAULT_AUTH_MODE Locked` and `azd provision` puts the server back to 401 for everyone; `az ad app delete --id "$app"` removes the app registration, its service principal and every assignment.

**Not verified yet** (needs a tenant): the whole flow against real Entra tokens (the unit tests prove every row of the table with a key set made in the test), that Claude's connector and the phone accept this host's challenge and metadata (checklist F3), and the `CONTAINER_APP_HOSTNAME` fallback for the resource URI (the template passes the address explicitly, so nothing relies on it).

## Connect the clients (phase 2, step 4)

This is the step the whole of phase 2 exists for: the Claude app on your phone, Claude.ai in a browser, Claude Desktop and Claude Code call the vault server in Azure as you. Every one of them signs in with Microsoft Entra ID through the same OAuth flow: the client calls `/mcp` without a token, gets the 401 challenge from step 3, reads the metadata document, sends you to Entra to sign in (and to consent once), and then calls with an access token for the vault server. Entra has no self-registration for clients (no Dynamic Client Registration, no Client ID Metadata Document), so Claude must identify itself with a **client app you register once**: a *public client* (no secret), used by Claude Code on your machine and, if Entra and Claude agree, by the hosted Claude apps too. The hosted apps (claude.ai, Desktop, the phone, Cowork) share one connector on your Claude account and connect from Anthropic's servers, not from your device; Claude Code connects from your machine. Facts from Anthropic's pages [Add a connector that isn't in the directory](https://claude.com/docs/connectors/custom/add-unlisted), [Authentication for connectors](https://claude.com/docs/connectors/building/authentication) and [MCP in Claude Code](https://code.claude.com/docs/en/mcp), read 2026-10-28; the ADR server's version of this is [docs/06](../../docs/06-claude-connection.md).

**You need:** step 3 done (sign-in on, the two checks passed); the Azure CLI signed in to the tenant with permission to create app registrations; Claude Code 2.1.231 or newer (older builds send a `127.0.0.1` callback); for the phone, Claude.ai and Desktop, a Claude plan that allows custom connectors (Free allows one). The client registration below is the same public client the ADR server's checklist plans in [docs/12](../../docs/12-entra-sign-in-checklist.md) (A3, Part F): create it once and both servers use it; only each server's scope is new to it. Report step numbers and status codes only; never an ID, a token or a secret.

Scripted alternative: `.\scripts\setup-entra-signin.ps1 -Vault -VaultResourceUri <VAULT_MCP_ENDPOINT>` (see step 3) already does steps 1 to 3 of this section, with the same public client name, and prints the `claude mcp add` line of step 5 with the right ids.

1. Register the public client once (skip the `create` if Part F already made it; then only add the redirect URIs with `az ad app update --id "$client" --public-client-redirect-uris <the existing ones and these>`, because the update replaces the list). Two redirect URIs: Claude Code's local callback on a fixed port, and the hosted Claude apps' callback:
   ```bash
   client=$(az ad app create --display-name 'MCP clients (public)' --sign-in-audience AzureADMyOrg --is-fallback-public-client true \
     --public-client-redirect-uris http://localhost:8976/callback https://claude.ai/api/mcp/auth_callback --query appId -o tsv)
   az ad app show --id "$client" --query 'publicClient.redirectUris' -o tsv
   ```
   Expect the two addresses printed. No secret is created: a public client proves nothing about itself, which is why the server checks the user's role, not the client.
2. Pre-authorize it on the vault scope, so Entra does not ask you for consent on every device. The list is replaced as a whole: keep the Azure CLI entry from step 3 of the sign-in section only while you still want the smoke test from your terminal (`$app` and `$obj` are the vault server's ids from that section; read them back with `az ad app list --display-name 'Obsidian vault MCP server' --query '[0].[appId,id]' -o tsv` if the terminal is gone):
   ```bash
   scope=$(python3 -c "import json; print(json.load(open('scripts/entra/vault-oauth2-permission-scopes.json'))['api']['oauth2PermissionScopes'][0]['id'])")
   az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$obj" --headers 'Content-Type=application/json' \
     --body "{\"api\":{\"preAuthorizedApplications\":[{\"appId\":\"$client\",\"delegatedPermissionIds\":[\"$scope\"]},{\"appId\":\"04b07795-8ddb-461a-bbee-02f9e1bf7b46\",\"delegatedPermissionIds\":[\"$scope\"]}]}}"
   ```
   Expect no output (Graph answers 204).
3. Register the MCP address on the vault server's app. Claude asks Entra for a token with the server's exact URL as the OAuth *resource* (RFC 8707), and Entra issues one only if that URL is an Application ID URI of the app; otherwise sign-in fails with `AADSTS9010010`. Keep `api://<client id>` as well:
   ```bash
   endpoint=$(cd src/VaultMcp && azd env get-value VAULT_MCP_ENDPOINT)
   az ad app update --id "$app" --identifier-uris "api://$app" "$endpoint"; echo "exit $?"
   ```
   Expect `exit 0` (PASS). An error that mentions a verified domain is a FAIL: the tenant's policy only accepts `https://` identifier URIs on a domain the tenant has verified, and `azurecontainerapps.io` is Microsoft's. Apps that request v2.0 tokens (this one does) are exempt from the policy by default ([Restrictions on identifier URIs](https://learn.microsoft.com/entra/identity-platform/identifier-uri-restrictions)), so expect PASS; if it fails anyway, the fix is a custom domain on the container app under a domain your tenant has verified, which is a template change (ingress `customDomains` and a managed certificate) the maintainer adds on request. Report PASS or FAIL only.
4. Allow the client at the server. In `src/VaultMcp`: `azd env set VAULT_ENTRA_ALLOWED_CLIENT_IDS "<public client id>,04b07795-8ddb-461a-bbee-02f9e1bf7b46"` (drop the second id once you no longer need the terminal smoke test), then `azd provision`. Expect a new healthy revision; `curl -s "$(azd env get-value VAULT_HEALTH_URL)"` still answers `"mode":"Entra"`.
5. **Claude Code** (any terminal, from the repository root):
   ```bash
   claude mcp add --transport http --client-id "$client" --callback-port 8976 vault "$(cd src/VaultMcp && azd env get-value VAULT_MCP_ENDPOINT)"
   claude mcp login vault
   ```
   Your browser opens on Entra; sign in (with MFA if the tenant asks) and accept the one-time consent for *Access the Obsidian vault MCP server as you*. Expect `claude mcp get vault` to show the server with OAuth configured, and `/mcp` inside a session to list `vault` as connected with the four tools. The phase 1 stdio entry (`vault` over `dotnet`) and this one cannot share a name; keep one, or name this one `vault-azure`. The same thing as a committed project file (`.mcp.json`, no secret in it):
   ```json
   { "mcpServers": { "vault": { "type": "http", "url": "https://<app fqdn>/mcp", "oauth": { "clientId": "<public client id>", "callbackPort": 8976 } } } }
   ```
6. **Claude.ai, Claude Desktop and the phone** (one connector on your account, shown on every device): in claude.ai go to *Customize → Connectors → Add custom connector*. Name `vault`; MCP server URL = `VAULT_MCP_ENDPOINT`; Authentication *Sign in now*; OAuth client *Use your own OAuth client* with the public client's ID and the secret left blank (Anthropic: "Leave the secret blank unless your authorization server requires one"); add, then *Connect*. Expect the Entra sign-in, then the connector listed as connected. In a chat, press **+** → *Connectors* and turn `vault` on; ask for something from an allow-listed note. Expect a `vault_search` call and an excerpt. Open the Claude app on the phone: the same connector is there; turn it on in a chat the same way. If Entra or Claude refuses the public client here (checklist F3 decides), create a confidential client instead: `az ad app create --display-name 'MCP clients (confidential)' --sign-in-audience AzureADMyOrg --web-redirect-uris https://claude.ai/api/mcp/auth_callback`, give it a secret with `az ad app credential reset --id <its id> --years 1 --query password -o tsv` typed straight into the connector dialog's secret field (and into Key Vault if you want a record; never into chat, an issue or this repository), then repeat steps 2 and 4 for its ID.
7. Record the result. This is gate 6 of [ADR-0010](../../docs/adr/ADR-0010-obsidian-second-brain.md) ("an unauthenticated call gets 401; the phone signs in and lists the four tools"): tell the maintainer which client worked and which did not, as step numbers and status codes, so [docs/06](../../docs/06-claude-connection.md), [docs/11](../../docs/11-entra-sign-in.md) and the checklist get the same answer.

What can go wrong, and what it means:

| You see | It means |
|---|---|
| `AADSTS9010010` during sign-in | step 3 of this section is missing or failed: the MCP URL is not an Application ID URI of the vault server app |
| `AADSTS65001` or a consent screen on every device | the client is not pre-authorized on the vault scope (step 2); consenting once is also fine |
| `AADSTS50011` (redirect URI mismatch) | the client's redirect URIs do not hold the callback Claude used (step 1); for Claude Code the port must be 8976 on both sides |
| Claude says "Couldn't reach the MCP server" | the 401 challenge or the metadata document is not reachable from Anthropic's network: `curl -si -X POST ... /mcp` and `curl .../.well-known/oauth-protected-resource/mcp` from anywhere on the internet must answer as in step 3 of the sign-in section |
| sign-in works, then `401 invalid_token` | the token is for another tenant, client or server: the client ID is missing from `VAULT_ENTRA_ALLOWED_CLIENT_IDS` (step 4), or the connector points at a different URL than the resource URI |
| sign-in works, then `403` with no challenge | your user has no `Vault.Reader` assignment (sign-in section, step 2) |
| sign-in works, `403 insufficient_scope` | the token lacks the vault scope: the client requested another scope; re-add the connector so it reads the scope from the challenge again |

To undo: `claude mcp remove vault`; in claude.ai *Customize → Connectors → vault → Remove* (the phone follows); `az ad app delete --id "$client"` removes the client registration for both servers, so prefer removing only its pre-authorization and its entry in `VAULT_ENTRA_ALLOWED_CLIENT_IDS`.

**Not verified yet** (needs a tenant and the apps): every step above. In particular: that Entra accepts the `azurecontainerapps.io` address as an identifier URI (step 3; the v2.0 exemption says it should), that the hosted Claude apps complete the flow with an Entra public client (checklist F3), and that Claude accepts Entra's discovery metadata as it is (Claude requires PKCE S256, which Entra supports; whether Claude also insists on Entra advertising `code_challenge_methods_supported` is not known from its pages).

## Tests

```bash
dotnet test src/VaultMcp/VaultMcp.sln
python3 src/VaultMcp/smoke-test.py --dll src/VaultMcp/VaultMcp.Stdio/bin/Release/net10.0/VaultMcp.Stdio.dll
python3 src/VaultMcp/smoke-test.py --url http://127.0.0.1:8080/mcp --private-note Personal/Diary.md
```

The smoke test speaks the MCP protocol the way a client does and prints one `OK` line per check, ending in `ALL OK`. With `--dll` it starts the stdio host itself (a throw-away vault is created unless you add `--vault "<vault>"`); with `--url` it talks to a running HTTP host, and `--private-note` names one path outside your allow-list that must be refused.

The unit tests run against a throw-away vault on disk and cover: nothing readable without the settings note; allow-listed folders only; path climbing and absolute paths refused; symbolic links refused; named pipes never opened; hidden folders ignored; tags and links inside code ignored; truncation; backlinks by name, alias and path; the tool list is exactly the four read tools with `readOnly` set; and the HTTP host locked by default, serving the tools in Anonymous mode, and refusing Anonymous under Container Apps. The Entra tests host the real pipeline with the tenant's key set replaced by a key made in the test and prove every row of the step 3 table: a token with the role opens the tools; no token, thirteen kinds of invalid token, a token without the scope, a token without the role, and unreachable keys each get their answer; the metadata document; and every setting rule, with no value echoed in a message.

## Not yet

- A run of steps 3 and 4 against a real tenant and the real apps: the sign-in, and Claude Code, Claude Desktop, Claude.ai and the phone connected to the Azure address (ADR-0010 gate 6). The steps are written; nothing has been clicked.
- A proven run of the step 2 sync container against a real Obsidian account (the beta client cannot run in CI).
- Uploading notes to the ADR server as evidence (level B in ADR-0010).
