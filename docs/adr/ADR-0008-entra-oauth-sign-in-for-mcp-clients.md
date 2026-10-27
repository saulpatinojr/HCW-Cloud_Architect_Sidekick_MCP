# ADR-0008: Entra OAuth sign-in for every MCP client, with the bearer token validated twice

- Status: Proposed
- Date: 2026-10-25
- Owners: Platform architecture
- Tags: auth, entra, oauth, mcp, security
- Issue: [#27](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/issues/27) (design, options A to E, owner defaults recorded in its status comment of 2026-10-25)

## Context
adr-dev runs in `FunctionKey` mode: one shared `mcp_extension` key in `x-functions-key`. *(Out of date: see [Update 2026-10-27](#update-2026-10-27-the-dev-environment-runs-in-entrabuiltin-mode).)* Issue #27 chose **Option A** (Entra ID as the authorization server, pre-registered clients) because OAuth with a pre-registered client ID is the only mechanism every listed client supports. Owner defaults on the issue: only the owner is `Adr.Admin` at first (a group is one later role assignment), the Copilot cloud agent is not supported (no Option D), one public client for loopback-capable clients plus one confidential client for Antigravity and Perplexity.

**Observed in this repository (2026-10-25):**
- `Auth:Mode=EntraBuiltIn` already exists. App Service Authentication (Easy Auth) validates the token at the edge and injects `X-MS-CLIENT-PRINCIPAL`; `HeaderTenantResolver` trusts that header only while the platform-injected `WEBSITE_AUTH_ENABLED` is true. `infra/modules/functionapp.bicep` configures `authsettingsV2`, `WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES` and makes the MCP webhook `Anonymous` in that mode.
- The MCP endpoint `/runtime/webhooks/mcp` is served by the **Functions host** (ADR-0001), not by our worker. The worker only sees a tool invocation after the host has accepted the HTTP request, with the request headers in `HttpTransport.Headers`. Code in this app therefore **cannot set the HTTP status or `WWW-Authenticate` header of an MCP request**; it can only fail a tool call with a JSON-RPC/tool error. **Observed locally on 2026-10-26** (Core Tools 4.15.2, .NET 10, webhook level `Anonymous`, no tenant): `initialize` returned the server info to a caller with no token, so `initialize` (and, by the same mechanism, `tools/list`) is protected only by the edge layer; and a `tools/call` request does carry its `Authorization` header into `HttpTransport.Headers`, so the worker can validate the token itself.
- There is no in-app token validation. Trust rests entirely on a header the platform injects.

**Specification (retrieved 2026-10-25, [MCP authorization, revision 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)):**
- MCP servers **MUST** implement OAuth 2.0 Protected Resource Metadata (RFC 9728), including `authorization_servers`, and **MUST** offer at least one discovery path: `WWW-Authenticate: Bearer resource_metadata="..."` on a 401, or a well-known URI (path-inserted `/.well-known/oauth-protected-resource/<mcp path>`, then the root). Clients must support both.
- Servers **MUST** validate that a token was issued specifically for them (audience, RFC 8707), **MUST NOT** accept or pass through other tokens, and **MUST** answer invalid or expired tokens with HTTP 401. Insufficient scope is 403 with `error="insufficient_scope"`, `scope=` and `resource_metadata`. `WWW-Authenticate` **SHOULD** carry `scope`.
- Clients **MUST** send `resource=<canonical MCP URL>`; pre-registered client credentials are a first-class registration mechanism. Dynamic Client Registration is deprecated in this revision; Client ID Metadata Documents are preferred where the authorization server supports them (Entra does not, per the issue).

**Microsoft Entra (Microsoft Learn, retrieved 2026-10-25):**
- [Secure an MCP server with Entra ID](https://learn.microsoft.com/entra/agent-id/secure-mcp-server-with-entra-id): use v2 access tokens; the Application ID URI must equal the `resource` the client sends (otherwise AADSTS9010010); the server must validate signature, `iss`, `tid`, `aud` (the app's client ID), expiry, and then `roles` and, for delegated tokens, `scp`; **do not write token validation from scratch**, use a vetted library.
- [Access tokens](https://learn.microsoft.com/entra/identity-platform/access-tokens): in v2 tokens `aud` is the resource's client ID and the key is identified by `kid` only; the issuer must match exactly and be tied to `tid`; signing keys rotate and "a reasonable frequency to check for updates ... is every 24 hours".
- [Configure built-in MCP server authorization](https://learn.microsoft.com/azure/app-service/configure-authentication-mcp): **preview**; Easy Auth issues the 401 challenge and serves the PRM document; MCP authorization defines access to the server, not to individual tools.
- [App roles](https://learn.microsoft.com/entra/identity-platform/howto-add-app-roles-in-apps#app-role-limits): app roles and exposed delegated scopes live in one permission-definition collection, so their values must be kept distinct (the sister project HCW-CloudAssessor_App hit this in its first real-tenant `plan`).

**Not verified (needs a live tenant, same list as the issue):** that the tenant accepts a `*.azurewebsites.net` Application ID URI; the UNVERIFIED cells of the client matrix; that Easy Auth forwards the `Authorization` header to the function on Azure (the extension forwarding it was observed locally, without Easy Auth); that the platform PRM and an app-served PRM coexist behind Easy Auth.

## Decision
Keep Option A and add a **second, in-process validation layer**. Two layers, each doing what only it can do:

| Concern (spec 2026-07-28) | Layer 1: edge (Easy Auth, preview; unchanged) | Layer 2: this app (new, off by default) |
|---|---|---|
| Unauthenticated or invalid request answered with HTTP 401 and a `WWW-Authenticate` challenge | Yes, for the MCP webhook; this is the only layer that can | `GET /api/auth/check` returns 401/403/503 with the challenge, from the same code; tool calls fail with `UNAUTHORIZED` / `FORBIDDEN` / `AUTH_UNAVAILABLE` |
| Protected resource metadata | Platform serves `/.well-known/oauth-protected-resource` | The app serves it too (root and path-inserted). **Unverified:** behind Easy Auth with `requireAuthentication` the platform probably answers those paths itself or with 401 before the app runs (only `/api/health` is excluded), so the app's copy is certain to be reachable only where Easy Auth is not in front (local runs, another host); the Bicep `excludedPaths` is deliberately not widened until a staging app shows how the platform treats them |
| Signature, issuer, tenant, audience, expiry, `nbf` | `authsettingsV2` | Validated again on every tool call |
| Which client, which scope, which role | `allowedApplications`, assignment required | `azp` allow-list, `scp`, `roles` re-checked from the signed token |
| Trust in `X-MS-CLIENT-PRINCIPAL` | Required today | **Not used** when layer 2 is on: the principal is built only from the verified token |

Layer 2 details:
1. **Library for the cryptography and claim checks.** `Microsoft.IdentityModel.JsonWebTokens` (`JsonWebTokenHandler`) verifies signature and standard claims. We write only the policy around it. The signing-key cache is ours because the issue asks for a hard fail-closed rule that the library's configuration manager does not give (it serves last-known-good configuration when a refresh fails).
2. **Policy.** Header `alg` is `RS256` only (`none`, `HS*` and anything else are refused before any key is touched); `kid` is required and selects the key (no trying every key). `iss` equals `https://<authority host>/<tenant>/v2.0` exactly; `tid` equals the configured tenant; `ver` is `2.0`; `aud` is the server app's client ID (plus any configured extras); `exp` and `nbf` are both required, with a clock skew of 60 s (0 to 300); `azp` is in the allow-list (the same client IDs given to Easy Auth). A token with `scp` must carry a required scope (`user_impersonation`); a token without `scp` (application token) is refused unless `AllowApplicationTokens` is set. The ADR role comes from `roles` through `Auth:AppRoleMap`; with no mapped role access is refused (`DefaultRoleWhenNoAppRoles` stays `None`). Token size is capped at 16 KiB.
3. **Fail closed.** Signing keys are cached for `JwksCacheMinutes` (default 60). When the cache has expired and a refresh fails, every call gets 503 until a fetch works, including calls whose key was cached before (a bounded grace period for already-known key IDs would trade a short window of a possibly revoked key for resilience; the owner can decide that later). A `kid` that is not cached triggers at most one refresh per 60 s per instance (no fetch amplification by a caller who sends random key IDs). If keys are needed and cannot be fetched or parsed, the answer is **503 / `AUTH_UNAVAILABLE` (retryable)**, never "accept" and never "401" (the credentials may be fine). Stale keys are not used after the cache expires.
4. **Challenges** follow RFC 6750 / RFC 9728 and the MCP spec: no token gives `Bearer resource_metadata="...", scope="..."` with no `error`; a bad token gives `error="invalid_token"` with a generic description (the reason code goes to the log, not to the caller); insufficient scope (or an application token) gives 403 `error="insufficient_scope"` with `scope`; a valid token whose user holds no ADR role gives a plain 403 with no challenge (an assignment cannot be fixed by signing in again, and a scope challenge would send clients round the spec's step-up loop); a key-fetch failure gives 503 with `Retry-After` and no `WWW-Authenticate`.
5. **Scope and role values stay different.** Roles are `Adr.Reader`, `Adr.Author`, `Adr.Approver`, `Adr.Admin`; the delegated scope is `user_impersonation` (requested as `api://<client id>/user_impersonation`). The role and scope definitions the runbook creates live in `scripts/entra/*.json`, and a unit test fails if any role value equals any scope value, or if those files drift from the code and Bicep.
6. **Owner-only first.** The only assignment on the enterprise application is the owner as `Adr.Admin` (assignment required = yes). Adding the Entra group later is one role assignment; no code or Bicep depends on it.
7. **Configuration** is environment/app settings (`Auth__Entra__*`), **off by default**: with the defaults the server behaves exactly as before, including local `func start` in `FunctionKey` mode. Bicep wires the settings only when `authMode=EntraBuiltIn` and the new parameter `entraValidateInApp` is `true` (default `false`, so merging this change alters no deployment). Invalid settings stop the host at startup instead of weakening a check.
8. **Logs are content free.** Only a fixed reason code, the HTTP status class and the correlation ID are logged. Never the token, any claim value (object ID, user name, tenant), the key ID, or an exception message from the token library.
9. **Routes.** `host.json` sets `extensions.http.routePrefix` to `""` so the well-known document can be served at the root; the health function now declares `api/health` explicitly, so `/api/health` does not move.

Out of scope here (the tenant half, which needs the owner): creating the server and client app registrations, pre-authorization, role assignment, the Application ID URI check, custom domain if needed, and the per-client sign-in tests. `docs/11-entra-sign-in.md` carries the runbook.

## Rationale
- It is the only design that keeps one sign-in method for every client without running a token-issuing proxy (Option C) or a gateway (Option D).
- The edge layer is the only place that can return a real 401 for `/runtime/webhooks/mcp`, so it stays. It is also a preview feature that this app does not control; a second layer means a platform misconfiguration, a missing header, or a changed preview behaviour fails closed at the tool call instead of silently trusting a header.
- Removing the dependence on `X-MS-CLIENT-PRINCIPAL` turns "the platform strips inbound copies" from an assumption into a non-issue.
- Microsoft says not to write validation by hand; using `JsonWebTokenHandler` keeps the signature and claim logic in the vetted library, and the remaining policy is small enough to test with negative cases and mutation checks.
- App-served PRM and a probe endpoint give the owner a way to prove the policy with `curl` before any MCP client is involved.

## Considered alternatives
1. **Easy Auth only (status quo plus tenant work)** — rejected: nothing in the app proves the token; a header is the whole trust boundary.
2. **`ConfigurationManager<OpenIdConnectConfiguration>` for keys** — rejected for now: serves stale keys when a refresh fails, which conflicts with the fail-closed rule; extra OpenID dependency and an extra discovery hop. Revisit if our cache proves fragile.
3. **`Microsoft.Identity.Web` / ASP.NET Core JWT bearer middleware** — rejected: needs the ASP.NET Core authentication pipeline, which the MCP trigger never passes through.
4. **In-app validation only, webhook `Anonymous`, Easy Auth off** — rejected: the host would answer `initialize` and `tools/list` to anyone and could never return a 401 challenge, so clients could not start the OAuth flow. The health probe keeps reporting 503 in `EntraBuiltIn` mode when the platform layer is missing.
5. **Self-hosted MCP SDK server (custom handler, preview) where the app owns the whole HTTP exchange** — rejected here, see ADR-0001; the validator and challenge builder are plain classes and would move unchanged.

## Consequences
- New dependency: `Microsoft.IdentityModel.JsonWebTokens` (and its transitive `Microsoft.IdentityModel.Tokens`), pinned centrally, covered by NuGet Audit in CI.
- A new error code, `AUTH_UNAVAILABLE` (retryable), is added to the common failure codes of every tool, so `docs/03-mcp-contracts.md` changes by one word per tool even with the switch off; input and output schemas are unchanged and the code is only ever returned when the switch is on and keys cannot be fetched.
- With layer 2 on, a caller needs a bearer token that reaches the function. If Easy Auth or the MCP extension does not forward `Authorization`, every call is refused; rollback is `entraValidateInApp=false` (the header path returns).
- `host.json` route prefix changes to `""`; any future HTTP function must give its full route.
- Merging this change deploys code only; defaults are off, so adr-dev stays on `FunctionKey` with no behaviour change. *(Out of date: see [Update 2026-10-27](#update-2026-10-27-the-dev-environment-runs-in-entrabuiltin-mode).)*

## Gates before this ADR can be Accepted
All need the owner's tenant and cannot be shown from the repository:
1. The runbook in `docs/11-entra-sign-in.md` completes in a sandbox or the staging deployment with every line as expected, including that `aud` of a real v2 token equals the server app's client ID and that a real tool call carries the `Authorization` header into the app.
2. The `*.azurewebsites.net` Application ID URI is accepted by the tenant (or a custom domain is chosen).
3. Each client in the issue's matrix signs in to staging. The Copilot cloud agent stays unsupported.
4. `ADR_AUTH_MODE=EntraBuiltIn` is switched on for adr-dev only after 1 to 3. *(Already on, before 1 to 3 are verified: see [Update 2026-10-27](#update-2026-10-27-the-dev-environment-runs-in-entrabuiltin-mode).)*

## Rollback
- Turn layer 2 off: `ADR_ENTRA_VALIDATE_IN_APP=false` (or remove `Auth__Entra__ValidateBearerToken`) and `azd provision`; the header path and Easy Auth behave as before.
- Turn Entra off entirely: `ADR_AUTH_MODE=FunctionKey`.
- Revert the pull request; nothing is persisted by this feature.

## Update 2026-10-27: the dev environment runs in EntraBuiltIn mode
This update leaves the decision, rationale and alternatives above unchanged. It corrects three statements that said adr-dev stays on `FunctionKey` (one shared `mcp_extension` key in the `x-functions-key` header): one in Context, one in Consequences and gate 4. Each is marked in place.

**Observed (2026-10-26)** in the successful `ci-cd` run [40386017357](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/actions/runs/40386017357):
- The deploy job ran with `ADR_AUTH_MODE: EntraBuiltIn`. In this mode App Service Authentication (also called Easy Auth, the platform's sign-in check) is the only gate in front of the MCP address, because the MCP webhook itself is `Anonymous` ([deployment guide](../05-local-dev-and-deploy.md#enable-built-in-entra-authentication-preview-instead-of-the-key)).
- It created the resource group `rg-adr-dev-scus-01` and the function app `func-adr-dev-scus-01` (names from [ADR-0009](ADR-0009-azure-naming-convention.md)).
- The step "Verify unauthenticated MCP calls are rejected (Entra mode)" passed. It sends a POST with no token to the MCP address and requires HTTP 401.
- The step "Smoke test (key fetched into a masked variable, never echoed)" was skipped. It runs only when `ADR_AUTH_MODE` is `FunctionKey`.
- [ADR-0009](ADR-0009-azure-naming-convention.md) had already recorded that the environment it replaces also ran in `EntraBuiltIn` mode. The repository does not record when the mode was switched.

**What this means.** The shared key is not the way into the dev environment: clients sign in with Microsoft Entra ID ([Connecting Claude](../06-claude-connection.md), [Entra sign-in](../11-entra-sign-in.md)). Running the app on your own machine with `func start` is unchanged and still uses `FunctionKey` mode, which is the code default and the value in `local.settings.json.example`. The Rollback section above still applies: `ADR_AUTH_MODE=FunctionKey` turns Entra off.

**The gates above, as of this update:**
1. The runbook in `docs/11-entra-sign-in.md` completes with every line as expected: not yet verified.
2. The `*.azurewebsites.net` Application ID URI (the address Entra ID treats as the identity of the server) is accepted by the tenant: [ADR-0009](ADR-0009-azure-naming-convention.md) records that the server's Entra app registration holds the old function app's MCP URL as an Application ID URI, and says this answers the question for this tenant. That the new function app's MCP URL is registered there is not yet verified.
3. Each client in the issue's matrix signs in to staging: not yet verified.
4. `ADR_AUTH_MODE=EntraBuiltIn` is switched on for adr-dev: done. The gate list put this last, after gates 1 to 3, so its order no longer describes what happened.

**Also not yet verified:** whether the optional in-app token check (`Auth__Entra__ValidateBearerToken`, layer 2 in the Decision) is on in the dev environment. Its default is off.

**Status.** This ADR stays Proposed. Changing it to Accepted is the owner's decision, once the evidence for gates 1 to 3 exists.
