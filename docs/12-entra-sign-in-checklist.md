# Entra sign-in checklist (owner steps)

A tick-off list for the tenant half of [issue #27](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/issues/27). It orders the steps the owner has to do by hand; the commands and expected lines are in [Entra sign-in](11-entra-sign-in.md) (the runbook) and are not repeated here. The design is [ADR-0008](adr/ADR-0008-entra-oauth-sign-in-for-mcp-clients.md).

Status on 2026-10-28: nothing below is done. The runbook's `az` and Graph commands have never been run against a tenant. Parts A and C were approved by the owner on 2026-10-28; the decisions themselves are still open.

**Rules for every part.** Never paste a token, a tenant ID, an application ID, a group ID or a secret into chat, an issue, a commit or this file. Report a step number, a line number and a status code only. Secrets belong in Key Vault; local settings belong in the git-ignored `local.settings.json`.

## Part A. Decisions (no commands)

Record the answers as a comment on issue #27.

- [ ] **A1. Group and role.** Which Entra group gets access, and at which app role (`Adr.Reader`, `Adr.Author`, `Adr.Approver`, `Adr.Admin`)? Recommended: the group as Reader, the owner as Admin; owner-only first is also fine (the runbook supports adding the group later with one assignment).
- [ ] **A2. Copilot cloud agent.** `docs/11` records "not supported". Confirm, or ask for Option D (an APIM gateway for that one client) from the issue.
- [ ] **A3. Client apps.** One public client (Claude Code, Codex, Kiro) and one confidential client (Antigravity, Perplexity), as `docs/11` plans, unless one client must be revocable on its own. **Claude.ai and Claude Desktop are not assigned to either group yet.** `docs/06` says confidential today, `docs/11` leaves it to the per-client test, and Anthropic's connector docs say the "Use your own OAuth client" option takes a client ID with the secret left blank unless the authorization server requires one ([Add a connector that isn't in the directory](https://claude.com/docs/connectors/custom/add-unlisted), read 2026-10-28). Register nothing for them before F3. F3 tries the public client first; if Entra or the connector refuses, both go to the confidential client. Whichever way it lands, `docs/06` and `docs/11` are updated to the same answer.
- [ ] **A4. Pipelines.** Decided 2026-10-28: CI will also call the server. That needs an application app role (every role in `scripts/entra/app-roles.json` allows `User` only), `Auth__Entra__AllowApplicationTokens`, a federated credential for the GitHub workflow (no stored secret) and a role map entry. It is a separate pull request; this checklist covers people only. **Decided 2026-10-28: read-only.** The pipeline identity gets an application role mapped to Reader, so it can read decisions, jobs and decision memory (`adr_list`, `adr_get`, `adr_search`, `adr_history`, `evidence_list`, `analysis_job_get`, `decision_memory_search` and the other read tools) and can neither run analysis, upload evidence nor change anything. A workflow that needs to run drift checks would need Author, which is a new decision, not a default. Built: the role `Adr.Reader.Application`, the Reader cap for every application, the opt-in `ADR_ENTRA_PIPELINE_READ` and the read-only smoke test (`adr_list`, `adr_search`, and `adr_create` must be refused) in the `ci-cd` deploy job; owner steps in [docs/11, Pipeline access](11-entra-sign-in.md#pipeline-access-an-application-role-read-only). Turn it on only at G3.

## Part B. Tenant protection (Entra ID P1 and MFA)

Decided 2026-10-28: Entra ID P1 (the paid tier that includes Conditional Access, the rule engine that can require multifactor authentication) for the tenant `hybridcloudwork.com`. Token sign-in itself works on the free tier, so Part D does not wait for this part.

Do this signed in with a **work account in that tenant** that holds the Global Administrator role, not a personal account: a trial started from a personal account lands in a new tenant ([Microsoft Q&A](https://learn.microsoft.com/answers/a/12596107)).

- [ ] **B1. Two emergency access accounts.** Cloud-only admin accounts, excluded from every MFA rule below, passwords stored offline. They are the way back in if a policy locks everyone out. Reference: [Manage emergency access accounts](https://learn.microsoft.com/entra/identity/role-based-access-control/security-emergency-access).
- [ ] **B2. Get P1.** Microsoft 365 admin center → Billing → Purchase services → search "Microsoft Entra ID P1" → Start free trial or Buy. Expect: the Entra admin center Overview page shows *Microsoft Entra ID P1*. Reference: [Sign up for Entra ID P1 or P2](https://learn.microsoft.com/entra/fundamentals/get-started-premium).
- [ ] **B3. Assign the license** to each person who will sign in (Users → Active users → the user → Licenses and apps). Expect: a tick next to Microsoft Entra ID P1.
- [ ] **B4. Swap Security Defaults for Conditional Access in one sitting.** Security Defaults (Microsoft's free MFA-for-everyone switch) and Conditional Access cannot both be on.
  1. Entra admin center → Overview → Properties → Manage security defaults. Write down whether it shows Enabled or Disabled (the undo below depends on it), then set it to Disabled if it was enabled.
  2. Entra ID → Conditional Access → Policies → New policy from template. Create **Require multifactor authentication for all users** and set it **On** at once; exclude the two B1 accounts.
  3. From the same templates create **Require phishing-resistant MFA for admins**, **Block legacy authentication** and **Require MFA for Azure management** in **Report-only**. After a few days with nothing unexpected in the sign-in logs, set them On.
  References: [Require MFA for all users](https://learn.microsoft.com/entra/identity/conditional-access/policy-all-users-mfa-strength), [Policy templates](https://learn.microsoft.com/entra/identity/conditional-access/concept-conditional-access-policy-common).
- [ ] **B5. Check.** In a private browser window sign in at `https://myapps.microsoft.com`. Expect an Authenticator prompt.

To undo: set the policies to Off. Turn Security Defaults back on only if you recorded it as Enabled in B4 step 1; if it was Disabled before, leave it off. The B1 accounts are the fallback.

## Part C. Read-only look at the dev environment

Nothing here changes anything.

- [ ] **C1.** Function app → Environment variables: `ADR_AUTH_MODE` is `EntraBuiltIn` (docs say it is).
- [ ] **C2.** Same page: is `Auth__Entra__ValidateBearerToken` `true`? The deploy default is `false`. Record the answer in the issue; do not change it yet.
- [ ] **C3.** Entra → App registrations → the server app → Expose an API: does the list of Application ID URIs hold the **new** function app's MCP URL (`https://func-adr-dev-scus-01…/runtime/webhooks/mcp`)? [ADR-0009](adr/ADR-0009-azure-naming-convention.md) records only the old app's URL.

## Part D. Sandbox runbook (people)

Run [the runbook in docs/11](11-entra-sign-in.md#real-tenant-verification-runbook) in a **sandbox tenant**, never in `adr-dev`. Prerequisites and the exact PowerShell blocks are there.

| | Runbook step | Expect |
|---|---|---|
| [ ] | 1. Create the sandbox app, roles, scope; assign yourself `Adr.Admin` | JSON for the new role assignment |
| [ ] | 2. Application ID URI on an `azurewebsites.net` address | `exit code: 0`. An error naming a verified domain is FAIL: stop and report; the endpoint then needs a custom domain and the Bicep plan changes |
| [ ] | 3. Read back | four roles, scope `user_impersonation`, token version `2`, `assignment required: true`, `PASS` |
| [ ] | 4. Local app with the in-app check on | `Mapped function route '/api/auth/check'` and the MCP endpoint line |
| [ ] | 5. Sign in and call | lines 1–3 → 401, line 4 → 200, line 5 roles `["Admin"]`, line 7 → 404, smoke test prints `OK initialize`, `OK tools/list` |
| [ ] | 6. (optional) second sandbox user | `AADSTS50105` |
| [ ] | 8. Clean up | sandbox app deleted, `local.settings.json` restored |

If any line differs, stop: do not turn sign-in on for a client. `AADSTS50105` from `az account get-access-token` is the owner-only lock working, not a bug.

## Part E. Deployed test (staging or sandbox deployment, not `adr-dev`)

- [ ] **E1.** Run `azd env set ADR_ENTRA_VALIDATE_IN_APP true`, then `azd provision`, so the Function App actually receives the setting ([docs/05](05-local-dev-and-deploy.md)); `azd env set` alone changes only the local environment file. Expect: provision ends without an error, and the app's Environment variables page shows `Auth__Entra__ValidateBearerToken` = `true`.
- [ ] **E2.** Repeat runbook step 5 with `$base` set to the deployed host. Expect: lines 1–3 → 401 (now answered by App Service Authentication), line 4 → 200, line 5 → roles `["Admin"]`, line 6 → 200 or 401 (the platform may answer the anonymous metadata call before the app does; record which), line 7 → 404, line 8 → a `WWW-Authenticate: Bearer` challenge that carries both `resource_metadata="https://<app host>/.well-known/oauth-protected-resource/runtime/webhooks/mcp"` and `scope="api://<server client id>/user_impersonation"` (the shape `docs/11` defines; a challenge missing either is a FAIL, because MCP clients discover the sign-in from it), and the smoke test prints `OK initialize` and `OK tools/list`. Line 4 is the first proof that the platform passes the `Authorization` header through to the app.
- [ ] To undo: `azd env set ADR_ENTRA_VALIDATE_IN_APP false` and provision again.

## Part F. Each client signs in

Register what the table in [docs/11 "Clients and what each needs registered"](11-entra-sign-in.md#clients-and-what-each-needs-registered) lists, then sign in to the staging deployment. Expect, for each: a Microsoft sign-in page (with P1: an Authenticator prompt), then the tool list. Record pass or fail per client in the issue; cells marked UNVERIFIED in the issue may surprise.

- [ ] F1. VS Code and Copilot CLI (nothing to register; pre-authorize Microsoft's built-in client ID from the table)
- [ ] F2. Claude Code (`--client-id` and `--callback-port`, [docs/06](06-claude-connection.md))
- [ ] F3. Claude.ai and Desktop
- [ ] F4. Codex
- [ ] F5. Kiro
- [ ] F6. Antigravity and Perplexity (the confidential client; its secret only in Key Vault, expiry tracked)

## Part G. Close out

- [ ] **G1.** Results to the maintainer (step, line, status code only). The maintainer updates docs/06, docs/11, the changelog and the issue.
- [ ] **G2.** The owner moves [ADR-0008](adr/ADR-0008-entra-oauth-sign-in-for-mcp-clients.md) from Proposed to Accepted. No agent does this.
- [ ] **G3.** Only after Part F passes: `ADR_ENTRA_VALIDATE_IN_APP=true` for `adr-dev`, then the pipeline steps 1 to 4 in [docs/11, Pipeline access](11-entra-sign-in.md#pipeline-access-an-application-role-read-only) (`ADR_ENTRA_PIPELINE_READ=true`). Expect the deploy job to print `SMOKE TEST PASSED (read-only)`.

## Part H. The vault server (ADR-0010, phase 2 step 3)

The Obsidian vault server in Azure uses the same pattern with its **own** app registration and one role, `Vault.Reader`. It reuses the client registrations from Part F (only its scope is new to them), so do it after Part F.

- [ ] **H1.** Create the vault server's app registration, assign yourself `Vault.Reader`, pre-authorize the clients, turn `VAULT_AUTH_MODE=Entra` on and run the two checks: steps 1 to 6 of [Sign in with Entra](../src/VaultMcp/README.md#sign-in-with-entra-phase-2-step-3) in the vault server README. Report step numbers and status codes only.
- [ ] **H2.** Connect Claude Code, Claude Desktop, Claude.ai and the phone to the vault address: steps 1 to 7 of [Connect the clients](../src/VaultMcp/README.md#connect-the-clients-phase-2-step-4) in the vault server README. Step 1 there creates the same public client A3 and F2/F3 plan, so do it once and reuse it here. Report which client signed in and listed the four tools (gate 6 of ADR-0010), and whether the hosted Claude apps needed the confidential client; the maintainer then writes the same answer into docs/06 and docs/11.
