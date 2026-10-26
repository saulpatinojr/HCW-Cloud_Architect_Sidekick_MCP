# Entra sign-in for MCP clients

Status: the code half of [issue #27](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/issues/27) is built and tested without a tenant. The tenant half (app registrations, client sign-in, staging) is **not done**, and adr-dev stays on `FunctionKey`. The design and the reasons are in [ADR-0008](adr/ADR-0008-entra-oauth-sign-in-for-mcp-clients.md) (Proposed).

## What this is, in plain words

A sign-in token is a signed text the client gets from Microsoft Entra ID and sends with every request. The server must check it. Two checks run, one behind the other:

1. **At the edge**: App Service Authentication (Easy Auth, a preview feature) refuses a request without a good token with HTTP 401 and tells the client where to sign in. Only this layer can do that for the MCP address `/runtime/webhooks/mcp`. It is switched on by `ADR_AUTH_MODE=EntraBuiltIn` and already existed.
2. **In the app** (new, **off by default**): the app checks the same token again for every tool call, and builds the caller's identity only from what that token says. It no longer believes the `X-MS-CLIENT-PRINCIPAL` header the platform adds.

Why twice: the edge layer is a preview feature the app cannot see into. If it is misconfigured, drifts, or changes behaviour, the app still refuses a bad token instead of trusting a header.

## What the app checks

| Check | Rule | If it fails |
|---|---|---|
| Algorithm | `RS256` only. `none`, `HS256` and everything else are refused before any key is touched | 401 |
| Key | `kid` in the header must match a key in the tenant's published key set | 401 (unknown key), 503 (keys cannot be fetched) |
| Signature | verified with that key by `Microsoft.IdentityModel.JsonWebTokens` | 401 |
| Issuer | exactly `https://login.microsoftonline.com/<tenant>/v2.0` | 401 |
| Tenant | `tid` is the configured tenant | 401 |
| Audience | `aud` is the server app's client ID (plus any extras you configure) | 401 |
| Version | `ver` is `2.0` | 401 |
| Time | `exp` and `nbf` are both present and valid, with 60 s of clock skew (0 to 300) | 401 |
| Client | `azp` is on the allow-list (the same client IDs Easy Auth gets) | 401 |
| Scope | a delegated token must carry `user_impersonation` (scope values are case sensitive). A token with no `scp` is refused unless `AllowApplicationTokens` is on | 403 |
| Role | the `roles` claim is mapped to an ADR role through `Auth:AppRoleMap`; with no mapped role access is refused | 403 (tool call: `FORBIDDEN`) |
| Size | a token over 16 KiB is refused unread | 401 |

Every check that cannot be decided because signing keys are missing ends in **503 / `AUTH_UNAVAILABLE` (retryable)**, never in acceptance. Signing keys are cached for 60 minutes (5 to 1440). A `kid` that is not cached triggers one refresh, at most one per minute per instance. Expired keys are never used if the refresh fails.

The caller is told only "a valid access token is required". The reason goes to the log as a fixed code (below); the log never holds a token, a claim value, a key ID or a library error message.

## Settings (all optional; everything off by default)

App settings use `__` for `:`. Arrays use `__0`, `__1`. With the defaults the server behaves exactly as before.

| Setting | Default | Meaning |
|---|---|---|
| `Auth__Entra__ValidateBearerToken` | `false` | Master switch. Needs `Auth__Mode=EntraBuiltIn`. The host refuses to start on a bad value below instead of weakening a check |
| `Auth__Entra__TenantId` | none | Entra tenant (directory) ID, a GUID. Must be one of the enrolled tenants (`Auth__Tenants__n__Id`) |
| `Auth__Entra__ClientId` | none | Client ID of the server app registration (the `aud` of a v2 token) |
| `Auth__Entra__AllowedClientIds__n` | none (required) | Client application IDs allowed as `azp` |
| `Auth__Entra__Audiences__n` | none | Extra accepted `aud` values, for example `api://<client id>` for a v1 app |
| `Auth__Entra__RequiredScopes__n` | `user_impersonation` | Scope values of which a delegated token must carry one. Must differ from every app role value |
| `Auth__Entra__AllowApplicationTokens` | `false` | Accept tokens with no `scp` (client-credentials callers) that carry an app role |
| `Auth__Entra__AuthorityHost` | `login.microsoftonline.com` | Host name only. Sovereign clouds use their own |
| `Auth__Entra__JwksUrl` | built from host and tenant | Key set address. `https` only (plain `http` only for loopback, for local tests) |
| `Auth__Entra__ClockSkewSeconds` | `60` | 0 to 300 |
| `Auth__Entra__JwksCacheMinutes` | `60` | 5 to 1440 |
| `Auth__Entra__ResourceUri` | `https://<WEBSITE_HOSTNAME>/runtime/webhooks/mcp` | The canonical MCP URL clients send as `resource`. Set it when the app is on a custom domain |

Bicep fills the first four from the values you already give for `EntraBuiltIn`, so the two layers cannot disagree: `azd env set ADR_ENTRA_VALIDATE_IN_APP true` (default `false`) and, only for a custom domain, `ADR_ENTRA_RESOURCE_URI`. In GitHub, set the repository variables of the same names.

## What the app serves itself

| Address | What |
|---|---|
| `GET /.well-known/oauth-protected-resource` and `GET /.well-known/oauth-protected-resource/runtime/webhooks/mcp` | Protected resource metadata (RFC 9728): `resource`, `authorization_servers` (the tenant's v2.0 issuer), `scopes_supported` (`api://<client id>/user_impersonation`), `bearer_methods_supported`. Public, cached 5 minutes, CORS `*`. Any other path is 404 |
| `GET /api/auth/check` | Runs the same checks as a tool call and answers with the HTTP status and `WWW-Authenticate` challenge the MCP spec asks for. Returns only the caller's own mapped roles, scopes and seconds left |
| `GET /api/health` | Unchanged address. `host.json` now sets the route prefix to `""` so the well-known address can be at the root, so the health route is spelled out in the code |

Both return 404 while the switch is off. **Reachability behind Easy Auth is not verified:** with authentication required (only `/api/health` is excluded) the platform may answer or refuse these anonymous calls before the app sees them. They are certain to be reachable where Easy Auth is not in front (the local runbook below); `/api/auth/check` with a token should pass through Easy Auth. The Bicep exclusion list is deliberately not widened until a staging app shows what the platform does. Challenges (revision 2026-07-28 of the MCP authorization spec, RFC 6750 and RFC 9728):

| Situation | Status | Header |
|---|---|---|
| No token | 401 | `Bearer resource_metadata="<metadata url>", scope="api://<client id>/user_impersonation"` (no `error`) |
| Token not valid | 401 | `Bearer error="invalid_token", error_description="The access token is not valid for this server.", resource_metadata="...", scope="..."` |
| Wrong scope, or an application token while those are off | 403 | `Bearer error="insufficient_scope", ..., scope="...", resource_metadata="..."` |
| Valid token, but the user holds no ADR role | 403 | none: an assignment is not fixed by signing in again, and a scope challenge would send clients round the spec's step-up loop |
| Signing keys cannot be fetched | 503 | `Retry-After: 30`, no `WWW-Authenticate` (the token may be fine) |

For the MCP address itself, App Service Authentication sends the challenge. The app cannot: the Functions host answers that address before the app sees the request.

## Reason codes in the log

Search Application Insights with `traces | where message startswith "Bearer token refused" or message startswith "Sign-in check refused" or message startswith "Signing key"`.

| Code | Usual cause |
|---|---|
| `missing_token` | No `Authorization: Bearer` header reached the app. If the client did send one, check that Easy Auth and the MCP extension forward it (see Gate 1 in ADR-0008) |
| `malformed`, `token_too_large` | Not a JWT, or two tokens in one header |
| `unsupported_algorithm` | Not `RS256`: a forged or unsigned token |
| `unknown_key`, `invalid_signature` | Token not signed by this tenant's keys, or a key rotated and the refresh has not happened yet (try again after a minute) |
| `expired`, `not_yet_valid` | Token lifetime, or a clock more than the skew apart. Sign in again |
| `wrong_issuer`, `wrong_tenant` | Token from another tenant, or a v1 token (issuer `sts.windows.net`). Set `requestedAccessTokenVersion` to 2 on the server app |
| `wrong_audience` | Token was requested for another API. Check the scope the client asks for starts with `api://<server client id>/` and that the Application ID URI matches the `resource` the client sends (AADSTS9010010 appears at sign-in if not) |
| `unsupported_token_version` | `ver` is not `2.0` |
| `wrong_client` | The client app is not in `Auth__Entra__AllowedClientIds__n` (and not pre-authorized on the server app) |
| `missing_claim` | The token lacks `exp`, `nbf`, `tid`, `azp` or `oid` |
| `insufficient_scope`, `application_token_not_allowed` | Delegated token without `user_impersonation`, or an application token while `AllowApplicationTokens` is off |
| `no_role` (sign-in check only; a tool call answers `FORBIDDEN`) | The user holds no ADR app role: assign one on the enterprise application |
| `keys_unavailable` | The key set could not be fetched (network, firewall, Entra outage). Callers get 503 and should retry |

## Owner-only first, group later

Only the owner is assigned `Adr.Admin` on the enterprise application (assignment required = yes), so Entra issues no token to anyone else. To add the Entra group later, assign the group to a role on the enterprise application: one assignment, no code, Bicep or app setting changes. The roles are defined in `scripts/entra/app-roles.json` and map as follows (`Auth:AppRoleMap`): `Adr.Reader` Reader, `Adr.Author` Author, `Adr.Approver` Approver, `Adr.Admin` Admin. An app role and a delegated scope share one collection in Entra, so their values must never be equal: the roles are `Adr.*`, the scope is `user_impersonation`, and `EntraDefinitionTests` fails if that changes or if the files, the code and Bicep drift apart.

## Clients and what each needs registered

From the research in issue #27 (vendor documents read on 2026-10-17, not re-verified; cells marked UNVERIFIED there are still unverified). Entra has no dynamic client registration, so every client uses a client ID you register once.

| Client | Client app ID to allow | Redirect URI to register |
|---|---|---|
| VS Code and GitHub Copilot, Copilot CLI | `aebc6443-996d-45c2-90f0-388ff96faa56` (Microsoft's, built into VS Code) | none; pre-authorize it on the scope |
| Claude Code, Claude.ai and Desktop, Codex, Kiro | one **public client** app you create | `http://localhost:<port>/callback` (Claude Code, `--callback-port`), `http://127.0.0.1/callback` (Codex), `http://localhost:<port>/oauth/callback` (Kiro), `https://claude.ai/api/mcp/auth_callback` (Claude.ai). Entra ignores the port of a loopback address, and `127.0.0.1` redirects are added in the manifest. Whether Claude.ai accepts a client with no secret is to be settled in the per-client test (docs/06 currently says confidential) |
| Antigravity, Perplexity | one **confidential client** app you create; its secret goes only in Key Vault | `https://antigravity.google/oauth-callback`, `https://www.perplexity.ai/rest/connections/oauth_callback` |
| Copilot cloud agent | not supported (no OAuth with a pre-registered client; owner decision on the issue) | none |

Each client app ID goes in `ADR_ENTRA_ALLOWED_CLIENT_IDS` and is pre-authorized on the server app's `user_impersonation` scope. Never put a client secret in a chat, an issue, this repository or an app setting; the confidential client's secret belongs in Key Vault, and its expiry needs tracking.

## What is verified, and what is NOT

**Verified without a tenant** (commands and counts are in the pull request that added this page):

| What | How |
|---|---|
| Every token rule above, including missing, malformed, oversized, expired, not yet valid, wrong audience, wrong issuer, wrong tenant, wrong client, v1, wrong scope, no role, `alg: none`, HS256 with the public key as secret, RS384, unknown `kid`, missing `kid`, `jku` and embedded `jwk` headers ignored, tampered payload, wrong signature | `tests/AdrMcp.UnitTests/Auth/BearerTokenValidatorTests.cs` (tokens signed by a key generated at test time) |
| Key cache: one fetch for many calls, expiry, rotation, at most one refresh a minute for unknown key IDs, failure backoff, single flight, no stale keys after expiry, size cap, weak and wrong-issuer keys dropped | `JwksSigningKeyProviderTests.cs` |
| A real tool call: roles, owner-only, generic refusal text, 503 on a key outage, the platform principal header ignored, switch off changes nothing, content-free logs | `BearerPipelineTests.cs` |
| Challenge text and the two HTTP functions | `McpChallengeTests.cs`, `AuthEndpointTests.cs` |
| Settings that stop the host | `EntraTokenOptionsTests.cs` |
| Role and scope values differ; files, code, Bicep and defaults agree | `EntraDefinitionTests.cs` |
| The same flow on the real Functions host (Core Tools 4.15.2, .NET 10, Azurite, the MCP webhook set to `Anonymous`, a local key stand-in on loopback): the well-known documents, 401, 403 and 200 from `/api/auth/check`, `Authorization` reaching a tool call, a forged platform header ignored, 503 and `AUTH_UNAVAILABLE` with the key endpoint down and recovery after the backoff, log lines with reason codes only | run by hand on 2026-10-26, on the final code except the key-outage case (503, `AUTH_UNAVAILABLE`, recovery), which was run on an earlier commit and is covered by the unit tests on the final code; not repeated by CI |

**NOT verified** (needs your tenant or a deployment):

- That real Entra tokens carry the claims the code expects (`aud` = server client ID, `ver` 2.0, `azp`, `scp`, `roles`, `tid`) for this registration, and that the app accepts them.
- That the app can fetch `https://login.microsoftonline.com/<tenant>/discovery/v2.0/keys` from the Flex Consumption app (network, private endpoints).
- That Easy Auth forwards the `Authorization` header to the function on Azure, and that the platform metadata and the app's metadata coexist behind it.
- That the tenant accepts `https://<app>.azurewebsites.net/runtime/webhooks/mcp` as an Application ID URI (step 2 of the runbook answers this).
- That each client signs in: VS Code, Copilot CLI, Claude Code, Claude.ai and Desktop, Codex, Kiro, Antigravity, Perplexity.
- The `az` and Graph commands in the runbook have never been run (they were checked only against `az ... --help`).
- Easy Auth's MCP authorization is a preview feature.

## Real-tenant verification runbook

Run this once in a **sandbox tenant**, before turning anything on for a client. It creates one app registration in Entra and runs the app on your machine. Nothing here is run by CI or by an agent. The commands are PowerShell (7 or Windows PowerShell 5.1). You need the Azure CLI, .NET 10, Azure Functions Core Tools 4, Azurite (`npm install -g azurite`), Python 3, and an account in the sandbox tenant that can create applications and assign app roles. Microsoft Learn lists Application Administrator and Cloud Application Administrator among the Entra roles supported for the app-role assignment call in step 1 ([Grant an appRoleAssignment for a service principal](https://learn.microsoft.com/graph/api/serviceprincipal-post-approleassignedto)); the permissions behind it are `AppRoleAssignment.ReadWrite.All` and `Application.Read.All`. If your account lacks them, run step 1 up to `az ad sp update` and ask an administrator to do the final assignment. Every step starts from the folder you cloned this repository into: `Test-Path scripts/entra/app-roles.json` must print `True`. Each block re-reads the values it needs, so you can paste it into a fresh window.

1. Create the Entra objects (owner step, sandbox only). This makes a single-tenant app with v2 tokens, the four app roles and the `user_impersonation` scope from `scripts/entra/`, pre-authorizes the Azure CLI (a public application ID; do this only in a sandbox), requires assignment, and assigns **you** as `Adr.Admin`:

```powershell
az login
$app = az ad app create --display-name 'ADR MCP server (sandbox)' --sign-in-audience AzureADMyOrg --requested-access-token-version 2 --app-roles '@scripts/entra/app-roles.json' --query appId -o tsv
$obj = az ad app show --id $app --query id -o tsv
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$obj" --headers 'Content-Type=application/json' --body '@scripts/entra/oauth2-permission-scopes.json'
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$obj" --headers 'Content-Type=application/json' --body '@scripts/entra/preauthorize-azure-cli.json'
az ad app update --id $app --identifier-uris "api://$app"
$sp = az ad sp create --id $app --query id -o tsv
az ad sp update --id $sp --set appRoleAssignmentRequired=true
$me = az ad signed-in-user show --query id -o tsv
$admin = (Get-Content scripts/entra/app-roles.json -Raw | ConvertFrom-Json | Where-Object { $_.value -eq 'Adr.Admin' }).id
$body = New-TemporaryFile; (@{ principalId = $me; resourceId = $sp; appRoleId = $admin } | ConvertTo-Json -Compress) | Set-Content $body
az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$sp/appRoleAssignedTo" --headers 'Content-Type=application/json' --body "@$body"; Remove-Item $body
```

   You should see JSON for the new assignment at the end. If `az login` asks for a subscription, press Enter; this runbook does not use one.

2. Answer the open question from the issue: does your tenant accept the app's `azurewebsites.net` address as an Application ID URI? Replace the host name with the one your app has or will have (`azd env get-value AZURE_FUNCTION_APP_NAME` after provisioning):

```powershell
$app = az ad app list --display-name 'ADR MCP server (sandbox)' --query '[0].appId' -o tsv
az ad app update --id $app --identifier-uris "api://$app" 'https://func-adr-sandbox.azurewebsites.net/runtime/webhooks/mcp'
"exit code: $LASTEXITCODE (0 = accepted)"
```

   Exit code 0: PASS. An error mentioning a verified domain: FAIL; put the MCP endpoint on a verified custom domain and use that URL here and in `ADR_ENTRA_RESOURCE_URI`. Either way, tell the maintainer only PASS or FAIL.

3. Read back what Entra now holds and check the role and scope values differ:

```powershell
$app = az ad app list --display-name 'ADR MCP server (sandbox)' --query '[0].appId' -o tsv; $a = az ad app show --id $app -o json | ConvertFrom-Json
$roles = @($a.appRoles.value); $scopes = @($a.api.oauth2PermissionScopes.value)
"app roles  : $($roles -join ', ')"; "scopes     : $($scopes -join ', ')"; "token ver  : $($a.api.requestedAccessTokenVersion)"; "id URIs    : $($a.identifierUris -join ', ')"
"assignment required: " + (az ad sp show --id (az ad sp list --filter "appId eq '$app'" --query '[0].id' -o tsv) --query appRoleAssignmentRequired -o tsv)
if (@($roles | Where-Object { $scopes -contains $_ }).Count) { 'FAIL: a role value equals a scope value' } else { 'PASS: role and scope values differ' }
```

   Expect `Adr.Reader, Adr.Author, Adr.Approver, Adr.Admin`, scope `user_impersonation`, token version `2`, `assignment required: true` and the PASS line.

4. Start the app on your machine with the in-app check on (needs two more PowerShell windows opened in the repository folder). This block writes `src/AdrMcp.Functions/local.settings.json` (git-ignored) from the example; if you already have that file it is first saved as `local.settings.json.bak` and put back in step 8. `WEBSITE_AUTH_ENABLED` is set only because the health probe reports unavailable without a platform in front; never set it as an app setting in Azure (a test guards `infra/`):

```powershell
$tenant = az account show --query tenantId -o tsv; $app = az ad app list --display-name 'ADR MCP server (sandbox)' --query '[0].appId' -o tsv
$f = 'src/AdrMcp.Functions/local.settings.json'; if ((Test-Path $f) -and -not (Test-Path "$f.bak") -and -not (Select-String -Path $f -Pattern 'Auth__Entra__ValidateBearerToken' -Quiet)) { Copy-Item $f "$f.bak" }; Copy-Item 'src/AdrMcp.Functions/local.settings.json.example' $f -Force; $j = Get-Content $f -Raw | ConvertFrom-Json
$set = [ordered]@{ Auth__Mode = 'EntraBuiltIn'; WEBSITE_AUTH_ENABLED = 'true'; Auth__Tenants__0__Id = $tenant; Auth__Entra__ValidateBearerToken = 'true'; Auth__Entra__TenantId = $tenant; Auth__Entra__ClientId = $app; Auth__Entra__AllowedClientIds__0 = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'; Auth__Entra__ResourceUri = 'http://localhost:7071/runtime/webhooks/mcp'; AzureFunctionsJobHost__extensions__mcp__system__webhookAuthorizationLevel = 'Anonymous' }
foreach ($k in $set.Keys) { $j.Values | Add-Member -NotePropertyName $k -NotePropertyValue $set[$k] -Force }
$j | ConvertTo-Json -Depth 5 | Set-Content $f; 'settings written'
```

   Window 2 (storage emulator): `New-Item -ItemType Directory -Force .azurite | Out-Null; azurite --silent --location .azurite --skipApiVersionCheck`. Window 3 (the app): `cd src/AdrMcp.Functions; func start --port 7071`. Wait for `Mapped function route '/api/auth/check' [get] to 'auth-check'` and the line `MCP server endpoint: http://localhost:7071/runtime/webhooks/mcp`. A start that stops with `Auth:Entra:...` names the setting that is wrong.

5. Sign in for the scope, then call. First sign in once with the scope named, so the Azure CLI has your consent for it (it opens your browser; choose the sandbox account; if it asks for a subscription press Enter):

```powershell
$tenant = az account show --query tenantId -o tsv; $app = az ad app list --display-name 'ADR MCP server (sandbox)' --query '[0].appId' -o tsv; az login --tenant $tenant --scope "api://$app/user_impersonation"
```

   Then paste this as a second block (the tokens stay in variables; never print or paste them):

```powershell
$base = 'http://localhost:7071'; $tenant = az account show --query tenantId -o tsv; $app = az ad app list --display-name 'ADR MCP server (sandbox)' --query '[0].appId' -o tsv
$good = az account get-access-token --tenant $tenant --scope "api://$app/user_impersonation" --query accessToken -o tsv
$arm  = az account get-access-token --tenant $tenant --resource-type arm --query accessToken -o tsv
"1 no token            (expect 401): " + (curl.exe -s -o NUL -w '%{http_code}' "$base/api/auth/check")
"2 junk token          (expect 401): " + (curl.exe -s -o NUL -w '%{http_code}' -H 'Authorization: Bearer not-a-token' "$base/api/auth/check")
"3 wrong audience      (expect 401): " + (curl.exe -s -o NUL -w '%{http_code}' -H "Authorization: Bearer $arm" "$base/api/auth/check")
"4 valid token         (expect 200): " + (curl.exe -s -o NUL -w '%{http_code}' -H "Authorization: Bearer $good" "$base/api/auth/check")
"5 who am I            (expect status ok, roles Admin): " + (curl.exe -s -H "Authorization: Bearer $good" "$base/api/auth/check")
"6 metadata            (expect 200 here; behind Easy Auth it may be 401, see above): " + (curl.exe -s -o NUL -w '%{http_code}' "$base/.well-known/oauth-protected-resource")
"7 metadata, wrong path (expect 404): " + (curl.exe -s -o NUL -w '%{http_code}' "$base/.well-known/oauth-protected-resource/other")
"8 the challenge on a 401:"; curl.exe -s -i "$base/api/auth/check" | Select-String 'WWW-Authenticate'
$env:ADR_MCP_BEARER = $good; python scripts/smoke-test.py --url "$base/runtime/webhooks/mcp"; Remove-Item Env:ADR_MCP_BEARER
```

   Lines 1 to 3 and 7 need no real token; lines 3 to 5 and the last command need the tokens above. Line 5 should show your roles as `["Admin"]` and `["user_impersonation"]` as the scope; no object ID or user name is in the answer. Line 8 should show `Bearer resource_metadata="http://localhost:7071/.well-known/oauth-protected-resource/runtime/webhooks/mcp", scope="api://<app id>/user_impersonation"`. The smoke test should print `OK initialize`, `OK tools/list` and the read tools: that proves a real tool call carried the token into the app and the app accepted it as `Adr.Admin`. If line 4 says 401, check that `$good` is a long text (`$good.Length` over 1000) and read the reason code in window 3. If `az account get-access-token` fails with `AADSTS50105`, Entra refused to issue a token to a user without an assignment, which is the owner-only lock working.

6. Owner-only check (optional, needs a second sandbox user): sign in as that user with `az login --tenant $tenant --scope "api://$app/user_impersonation"` and request a token as in step 5. Expect `AADSTS50105` (not assigned). Then sign back in as yourself.

7. Deployed app instead of local: deploy with `ADR_AUTH_MODE=EntraBuiltIn`, the server app ID, the allowed client IDs and `ADR_ENTRA_VALIDATE_IN_APP=true` (docs/05), set `$base` to `https://<app host>` and repeat step 5. Lines 1 to 3 are then answered by Easy Auth (still 401); line 4 onward proves that Easy Auth lets the token through and that the app accepts it. Do this in the staging environment before `ADR_AUTH_MODE=EntraBuiltIn` is switched on for adr-dev.

8. Clean up. Stop windows 2 and 3 with Ctrl+C, then:

```powershell
$app = az ad app list --display-name 'ADR MCP server (sandbox)' --query '[0].appId' -o tsv; az ad app delete --id $app
$f = 'src/AdrMcp.Functions/local.settings.json'; if (Test-Path "$f.bak") { Move-Item "$f.bak" $f -Force } else { Remove-Item $f -ErrorAction SilentlyContinue }; Remove-Item -Recurse -Force .azurite -ErrorAction SilentlyContinue
```

If any line differs from what is written, do not turn sign-in on for a client. Open an issue with the step and line number and the status code only; never paste a token, a tenant ID or an application ID.

## Known gaps

- The Entra objects are created by hand-run commands, not by a setup script with a read-back `PASS` check (issue #27, item 4). That script, the client registrations and the staging sign-in of each client are the tenant half.
- Easy Auth's MCP authorization is a preview feature. The app's own metadata and challenge are meant as a fallback, but behind Easy Auth they may never be reached (see above), and the MCP address itself can only be challenged by the platform.
- The key cache is strict by design: once its lifetime ends and a refresh fails, calls get 503 until the key endpoint answers, even for key IDs that were cached. A bounded grace period is an owner decision.
- A custom domain may be needed for the Application ID URI (step 2).
- Rate limiting and per-client quotas are not part of this change (docs/04, section 5).
