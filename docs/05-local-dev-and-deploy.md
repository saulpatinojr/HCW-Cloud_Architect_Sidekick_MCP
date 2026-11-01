# H. Local development and deployment

## Prerequisites (verify)

```bash
dotnet --version            # 10.0.1xx or later 10.0 band (global.json rolls forward within 10.0)
func --version              # Azure Functions Core Tools 4.x (>= 4.0.7030 required by the MCP extension)
azurite --version           # 3.x (npm i -g azurite)
az version                  # Azure CLI 2.6x+
azd version                 # Azure Developer CLI 1.1x+
bicep --version             # 0.41.2 or newer (az bicep install; az bicep upgrade if older): the resource-naming tests use 'bicep snapshot'
python3 --version           # 3.9+ for scripts/smoke-test.py (stdlib only)
```

Install Core Tools and Azurite without admin rights: `npm install -g azure-functions-core-tools@4 azurite`.

## Local settings

```bash
cp src/AdrMcp.Functions/local.settings.json.example src/AdrMcp.Functions/local.settings.json
```

`local.settings.json` is git-ignored (`.gitignore`). The example uses **InMemory** persistence (no Cosmos needed) and Azurite for Blob/Queue. Keep `Auth__Mode=FunctionKey` for a run on your own machine; locally no key is required. This is the local setting only: the deployed dev environment (`adr-dev`) runs in `EntraBuiltIn` mode, see "Deploy" below. Never paste real connection strings or keys into this file; for Cosmos use the emulator connection string or `Cosmos__AccountEndpoint` with `az login` (DefaultAzureCredential).

## Start Azurite

```bash
mkdir -p .azurite && azurite --silent --location .azurite --skipApiVersionCheck
```

`--skipApiVersionCheck` is required. Azure.Storage.Blobs 12.30.1 sends service API version 2026-10-06, which no Azurite release supports yet (verified 2026-10-16 with Azurite 3.37.0).

## Build

```bash
dotnet restore
dotnet build AdrMcp.sln -c Release
```

## Run

```bash
cd src/AdrMcp.Functions
func start --port 7071
# Health:            http://localhost:7071/api/health
# MCP (Streamable):  http://localhost:7071/runtime/webhooks/mcp
```

The host lists 28 tool triggers, the `AnalysisJobWorker` queue trigger, the `MaintenanceTimer` and `health`.

## Test MCP tools locally

```bash
# Protocol-level smoke test (initialize, tools/list, read tools, optional write round-trip)
python3 scripts/smoke-test.py --url http://localhost:7071/runtime/webhooks/mcp --write

# Single tool via curl
./scripts/local-mcp-call.sh adr_list '{"take":5}'
./scripts/local-mcp-call.sh technology_detect @samples/mcp-requests/technology_detect.json

# MCP Inspector (Node 18+)
npx @modelcontextprotocol/inspector   # transport: Streamable HTTP, URL above

# Claude Code against the local host
claude mcp add --transport http adr-local http://localhost:7071/runtime/webhooks/mcp
```

Upload flow locally: call `file_upload_request`, then `curl -X PUT -H "x-ms-blob-type: BlockBlob" -H "Content-Type: application/xml" --data-binary @src/Orders/Orders.csproj "<uploadUrl>"`, then `file_register_upload`.

## Tests

```bash
dotnet test tests/AdrMcp.UnitTests            # in-memory persistence; the resource-naming tests need Bicep 0.41.2 or newer (az bicep install) and are skipped without it
dotnet test tests/AdrMcp.IntegrationTests     # Azurite-backed (skipped automatically when Azurite is down)
ADR_TEST_COSMOS_CONNECTION_STRING="<emulator>" dotnet test tests/AdrMcp.IntegrationTests   # Cosmos tests
```

## Provision Azure resources

```bash
az login
azd auth login
azd init -e adr-dev                           # creates .azure/adr-dev
azd env set AZURE_LOCATION eastus2            # one of the 16 regions in infra/region-codes.json (all support Flex Consumption)
azd env set AZURE_PRINCIPAL_ID "$(az ad signed-in-user show --query id -o tsv)"   # grants you data-plane roles for testing (omit in prod)
azd env set ADR_AUTH_MODE FunctionKey         # or EntraBuiltIn (then also ADR_ENTRA_CLIENT_ID and ADR_ENTRA_ALLOWED_CLIENT_IDS, see below)
azd env set ADR_PRIVATE_NETWORKING false      # true for private endpoints
azd provision                                  # Bicep: RG, identity, storage, Cosmos, Key Vault, App Insights, Flex Consumption app, RBAC
```

### Resource names and tags

Every name is built in the entry template, `infra/main.bicep` (the vault server of ADR-0010 has its own, `infra/vault/main.bicep`, see below), from `<abbreviation>-<workload>-<environment>-<region code>-<nn>`. The abbreviations are Microsoft's published ones ([list](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations)); the reasons and the Azure name rules are in [ADR-0009](adr/ADR-0009-azure-naming-convention.md). With the defaults (`ADR_WORKLOAD_NAME=adr`, `ADR_ENVIRONMENT_SHORT=dev`) in `southcentralus` you get:

| Resource | Name |
|---|---|
| Resource group | `rg-adr-dev-scus-01` |
| Function app (its host name is `func-adr-dev-scus-01.azurewebsites.net`) | `func-adr-dev-scus-01` |
| App Service plan | `asp-adr-dev-scus-01` |
| Cosmos DB account | `cosno-adr-dev-scus-01` |
| Key Vault | `kv-adr-dev-scus-01` |
| Storage account (no hyphens allowed) | `stadrdevscus01` |
| Log Analytics workspace, Application Insights, managed identity | `log-…`, `appi-…`, `id-adr-dev-scus-01` |
| Private networking only | `vnet-…`, `snet-adr-app-…`, `snet-adr-pep-…`, `pep-adr-cosmos-…`, `pep-adr-kv-…`, `pep-adr-blob-…`, `pep-adr-queue-…` |

Things to know:
- The azd environment name (`adr-dev`) is **not** part of any name. It stays the azd environment, the GitHub environment and the `azd-env-name` tag. It must end with `-<environmentShort>` (`adr-dev` ends with `-dev`). That catches the likely mistake of adding a second azd environment without its own short name: a second one is called `adr-prod` and sets `azd env set ADR_ENVIRONMENT_SHORT prod` (the workflow then needs its own `ADR_ENVIRONMENT_SHORT` line too). It cannot catch two environments that end in the same short name, such as `adr-dev` and `other-dev`: give those a different `ADR_WORKLOAD_NAME`, region or suffix. `ADR_WORKLOAD_NAME` takes 2 to 8 and `ADR_ENVIRONMENT_SHORT` 2 to 5 lower-case letters or digits.
- The region needs a short code in `infra/region-codes.json` (16 regions today, the table is in the ADR). The `azd env set AZURE_LOCATION eastus2` above gives `eus2`, so the group is `rg-adr-dev-eus2-01`. For another region the deployment stops at once and says so; add the code first.
- The function app, the Cosmos DB account, the Key Vault and the storage account must be unique in all of Azure. If Azure says one is taken (for example a Key Vault that is still soft-deleted), set a suffix and provision again: `azd env set ADR_NAME_SUFFIX b` (in GitHub: the repository variable `ADR_NAME_SUFFIX`). The suffix goes on those four names only, for example `kv-adr-dev-scus-01b`. Use lower-case letters and digits, at most four.
- Tags on the group and every resource: `workload` (`adr`), `application` (`adr-mcp`), `environment` (`dev`), `azd-env-name` (`adr-dev`), and `component` (`api`, `data`, `security`, `monitoring` or `network`) on each resource.
- Changing a name later means replacing the resource. Azure cannot rename most resources.
- The Obsidian vault MCP server ([ADR-0010](adr/ADR-0010-obsidian-second-brain.md)) is a second workload, `adrmcp`, with its own azd project in `src/VaultMcp/` (environment `vault-dev`, variables `VAULT_WORKLOAD_NAME`, `VAULT_ENVIRONMENT_SHORT`, `VAULT_NAME_SUFFIX`) and its own resource group `rg-adrmcp-dev-scus-01`. It can touch nothing of the ADR server's: the vault template accepts only workload names that start with `adrmcp`, and this template refuses them, so the two deployments can never share a resource group or a global name, whatever you set. The steps are in the [vault server README](../src/VaultMcp/README.md#deploy-the-http-host-to-azure-phase-2-step-1b).

`scripts/postprovision.sh` creates the Key Vault secret `principal-hash-salt` (random) that the app reads through a Key Vault reference. It never overwrites an existing secret, and it waits (up to ~5 minutes) for the Key Vault role assigned in the same provision to propagate instead of failing on the first 403. The salt is passed to `az` through a private temporary file, never on the command line. With `ADR_PRIVATE_NETWORKING=true` the vault refuses public traffic, so the hook stops at once with a network error: run `azd provision` from a network that reaches the vault's private endpoint, or create the secret from there.

The app refuses to start with durable (Cosmos) persistence if `Adr:PrincipalHashSalt` is the built-in default, shorter than 32 characters, or an unresolved `@Microsoft.KeyVault(...)` reference — principal hashes in audit and memory would otherwise be guessable. If a first deployment does not start, check the app setting's Key Vault reference status (Portal → Function App → Environment variables), confirm the secret exists and the app identity holds Key Vault Secrets User, then restart the app.

Preview before applying (recommended for existing environments):

```bash
azd provision --preview     # what-if with the same parameters azd provision uses; runs no hooks and changes nothing
```

## Deploy

```bash
azd deploy api            # builds src/AdrMcp.Functions, publishes to the Flex Consumption app via the deployment container
azd up                    # provision + deploy in one step
```

**Which sign-in mode `adr-dev` uses.** The `adr-dev` environment is deployed by the `ci-cd` workflow, which reads `ADR_AUTH_MODE` from a GitHub variable (`FunctionKey` when the variable is not set, see [Tests and CI](07-tests-and-ci.md)). In the successful run of 2026-10-26 ([run 40386017357](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/actions/runs/40386017357)) the deploy job ran in `EntraBuiltIn` mode: it created `rg-adr-dev-scus-01` and `func-adr-dev-scus-01`, the step "Verify unauthenticated MCP calls are rejected (Entra mode)" passed, and the key-based smoke test was skipped. So the key commands below do not apply to `adr-dev`; clients sign in with Microsoft Entra ID ([Connecting Claude](06-claude-connection.md)). Not yet verified: that each MCP client signs in to `adr-dev`, and whether the optional in-app token check is on there ([Entra sign-in](11-entra-sign-in.md)).

`FunctionKey` mode only: retrieve connection details (never commit the key):

```bash
APP=$(azd env get-value AZURE_FUNCTION_APP_NAME); RG=$(azd env get-value AZURE_RESOURCE_GROUP)
az functionapp keys list -g "$RG" -n "$APP" --query systemKeys.mcp_extension -o tsv > /dev/null   # pipe into your secret store, do not echo in CI logs
echo "MCP endpoint: $(azd env get-value MCP_ENDPOINT)"
```

Post-deployment smoke test (`FunctionKey` mode only; in `EntraBuiltIn` mode pass a user's access token as `ADR_MCP_BEARER` instead, see [Connecting Claude](06-claude-connection.md#smoke-test-against-an-entra-protected-server)):

```bash
ADR_MCP_KEY="$(az functionapp keys list -g "$RG" -n "$APP" --query systemKeys.mcp_extension -o tsv)" python3 scripts/smoke-test.py --url "$(azd env get-value MCP_ENDPOINT)"
```

### Enable built-in Entra authentication (preview) instead of the key

1. Register an Entra app for the MCP server (single tenant):
   - **Expose an API**: set the Application ID URI to `api://<client-id>` and add the scope `user_impersonation`.
   - **Manifest**: set `requestedAccessTokenVersion` (`accessTokenAcceptedVersion` in the legacy manifest) to `2`. The server validates the v2.0 issuer, so v1 tokens are rejected with 401.
   - **App roles**: add `Adr.Reader`, `Adr.Author`, `Adr.Approver` and `Adr.Admin`, and assign them to users or groups under Enterprise applications. A caller without one of these roles is refused (`Auth__DefaultRoleWhenNoAppRoles` defaults to `None`).
   - **Enterprise applications → Properties**: set **Assignment required** to *Yes*, so unassigned users cannot get a token at all.
   - Register the client applications you will use (docs/06) and pre-authorize them on the `user_impersonation` scope.
2. Set the environment and provision:
   ```bash
   azd env set ADR_AUTH_MODE EntraBuiltIn
   azd env set ADR_ENTRA_CLIENT_ID <server app client id>
   azd env set ADR_ENTRA_ALLOWED_CLIENT_IDS "<client id>,<client id>"   # the client apps from docs/06
   azd env set ADR_ENTRA_TENANT_ID <tenant id>                          # optional; default is the deployment tenant
   azd provision
   ```
   Bicep configures App Service Authentication (`authsettingsV2`): the Microsoft provider with the tenant's v2.0 issuer, require authentication, 401 for unauthenticated requests, `/api/health` excluded, and only tokens obtained by the allowed client IDs. It also sets `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` to the tenant, enrolls it in the app (`Auth__Tenants__0__Id`, so the app refuses any other tenant and scheduled maintenance covers it), and `WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES=api://<client-id>/user_impersonation`, and makes the MCP webhook `Anonymous` because the platform now authenticates. Provisioning **fails** if `EntraBuiltIn` is chosen without a client ID (GUID) or allowed client IDs, so the endpoint can never be deployed open. If App Service Authentication is later turned off, the server rejects every call instead of trusting the principal header (with the optional in-app check below switched on, tool calls with a valid token still work, but `initialize` and `tools/list` are open and `/api/health` reports 503). Don't change these settings in the portal: the next provision overwrites them, and switching back to `FunctionKey` turns App Service Authentication off again.
3. Workspaces: the enrolled tenant can use `default` only. To give it more, add app settings `Auth__Tenants__0__Workspaces__0`, `__1`, … (for example to the `Auth__Tenants__0__Id` entry in `infra/modules/functionapp.bicep`, because `azd provision` replaces portal edits). Tenant IDs are GUIDs, which Linux app setting names cannot contain, so the `Auth__TenantWorkspaces__<tenant>` form does not work for Entra tenants. `Auth__Tenants__0__MapTo` stores the tenant under a different ADR tenant ID; set it before any data exists, because existing data stays under the old ID. The app refuses to start if two tenants resolve to the same ADR tenant ID, if a tenant ID is not a GUID or appears twice, or if a tenant or workspace ID contains anything other than letters, digits, `-`, `_` and `.`.
4. Optional second layer (**off by default**): the app also verifies the bearer token itself. Set `azd env set ADR_ENTRA_VALIDATE_IN_APP true` (and, only for a custom domain, `ADR_ENTRA_RESOURCE_URI <canonical MCP URL>`) and run `azd provision`; in GitHub set the repository variables of the same names. Bicep passes the tenant, server client ID and the same allowed client IDs as `Auth__Entra__*` settings. Turn it on only after the real-tenant runbook in [Entra sign-in](11-entra-sign-in.md) passes; to roll back set it to `false` and provision again. The app then also serves `/.well-known/oauth-protected-resource` and `/api/auth/check`. Optional, after that: `azd env set ADR_ENTRA_PIPELINE_CLIENT_ID <client id of an application>` lets that one application (in CI, the deploy job's own identity, switched on with the repository variable `ADR_ENTRA_PIPELINE_READ=true`) call the server with no signed-in user; it is Reader at most (docs/11, "Pipeline access").
5. Rotate/disable the system key: `az functionapp keys set -g $RG -n $APP --key-type systemKeys --key-name mcp_extension`.

## Logs

```bash
az webapp log tail -g "$RG" -n "$APP"                                   # live stream
az monitor app-insights query --app "$(azd env get-value APPLICATIONINSIGHTS_NAME)" -g "$RG" \
  --analytics-query "traces | where customDimensions.Tool != '' | project timestamp, message, customDimensions.CorrelationId, customDimensions.Tool | order by timestamp desc | take 50"
```

Audit events: Cosmos container `audit` (Data Explorer or `az cosmosdb sql container ...`), correlation id ties MCP response → logs → audit.

## Clean up safely

```bash
azd down                    # asks you to confirm, then deletes the resource group — irreversible. Key Vault has purge protection, so it stays soft-deleted for 90 days and nobody can purge it sooner (`--purge` makes azd try to purge soft-deleted resources such as the vault without asking; Azure refuses while purge protection holds, so the vault stays soft-deleted; not tested here). Its name stays reserved: creating the environment again within 90 days needs a new name, so set ADR_NAME_SUFFIX first (see Resource names and tags). Evidence under an immutability policy blocks deletion until the retention period ends
# Alternative (review first):
az group delete --name "$RG" --no-wait
```

Immutable `evidence` container: time-based retention (365 days, unlocked). Remove the policy before deleting during development: `az storage container immutability-policy delete --account-name <st> --container-name evidence --if-match "*"` (requires the Storage Account Contributor role). **[REVIEW REQUIRED]** before running against shared environments.

## Replacing the dev environment after the naming change

**Who and when.** This is an owner step, done once. Start it when the pull request that introduces [ADR-0009](adr/ADR-0009-azure-naming-convention.md) has been reviewed and is ready to merge; step 10 merges it. Until part D, the old environment keeps working and nothing is deleted.

**Why it cannot be done in place.** Azure cannot rename a resource group, a function app, a Key Vault or most other resources ([Microsoft's naming guidance](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming) says the same). So the new environment `rg-adr-dev-scus-01` is built next to the old one `rg-adr-dev`, and the old one is deleted at the end. This runbook needs only the Azure CLI (`az`), the GitHub CLI (`gh`) and the `ci-cd` workflow that created the old environment. (`azd down` followed by `azd up` from your own machine would also work, but it needs an azd environment set up as in "Provision Azure resources" above and has not been tried.)

**What changes.**

| | Before | After |
|---|---|---|
| Resource group | `rg-adr-dev` | `rg-adr-dev-scus-01` |
| Function app and its host | `func-adr-<token>`, `func-adr-<token>.azurewebsites.net` | `func-adr-dev-scus-01`, `func-adr-dev-scus-01.azurewebsites.net` |
| Other resources | `…-adr-<token>` (a hash) | `…-adr-dev-scus-01`, storage `stadrdevscus01` (see [Resource names and tags](#resource-names-and-tags)) |
| MCP URL | `https://func-adr-<token>.azurewebsites.net/runtime/webhooks/mcp` | `https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp` |

**The order, and why.** Part A only looks. Part B builds the new environment next to the old one. Part C moves the sign-in identity and your clients to the new address. Part D deletes the old environment. The delete is last on purpose: Azure shows whether the new names are free, and whether your organization's policies accept them, only while it deploys. If the deployment fails, the old environment is still there.

**What is lost in part D (irreversible).** The Cosmos DB data (ADRs, history, findings, audit, memory), everything in the storage account (uploaded evidence, reports), the logs, and the Key Vault secret `principal-hash-salt` (the new environment creates a new random one, so old principal hashes would no longer match; nothing is kept, so nothing is compared). Time, as estimates: the build in part B takes 10 to 15 minutes, the delete in part D about 5.

Use **one PowerShell window** (Windows PowerShell 5.1 or PowerShell 7) from step 1 to the end: later steps reuse the variables `$sub` and `$app`. If you close the window, repeat step 4 (and step 17 for `$app`). You need the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) signed in with an account that can delete a resource group in the subscription, the [GitHub CLI](https://cli.github.com/) signed in, and, in `EntraBuiltIn` mode (people sign in with their Microsoft Entra account instead of a shared key), the right to edit the server's Entra app registration. None of the commands reads a file from the repository, so any folder will do.

### Part A: Prepare (changes nothing)

1. **Open one PowerShell window** and keep it open until the end.

2. **Check that you are signed in to Azure.**

   ```powershell
   az account show --output none
   ```

   It prints nothing. If it says `Please run 'az login'`, run `az login`, sign in in the browser, and repeat.

3. **Check that you are signed in to GitHub.**

   ```powershell
   gh auth status
   ```

   It prints `Logged in to github.com`. If it says you are not logged in, run `gh auth login` and repeat.

4. **Read the subscription from the repository variable**, so no id is typed here or stored in the repository.

   ```powershell
   $sub = gh variable get AZURE_SUBSCRIPTION_ID --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP
   ```

   It prints nothing. If it says the variable was not found, stop: the deploy setup in [Tests and CI](07-tests-and-ci.md) has not been done.

5. **Check that no variable is overridden for the `adr-dev` environment.** A GitHub environment can hold its own copy of a variable, and the workflow uses that copy before the repository one. The commands below read the repository copies.

   ```powershell
   gh variable list --env adr-dev --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP
   ```

   It prints nothing: the deploy setup keeps the variables at repository level. If it lists `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `ADR_AUTH_MODE`, `ADR_ENTRA_CLIENT_ID` or `ADR_NAME_SUFFIX`, add `--env adr-dev` to every `gh variable get` and `gh variable set` for that name below, and repeat step 4 first if it is `AZURE_SUBSCRIPTION_ID`.

6. **Check that you can see that subscription.**

   ```powershell
   az account show --subscription $sub --query name --output tsv
   ```

   It prints the subscription's name. If it says the subscription was not found, run `az login` with the account that owns it.

7. **Look at the old group.**

   ```powershell
   az resource list --resource-group rg-adr-dev --subscription $sub --output table
   ```

   You should see the function app, plan, Cosmos DB account, Key Vault, storage account, Log Analytics workspace, Application Insights and identity, plus an `Application Insights Smart Detection` action group that Azure adds itself. If it says `ResourceGroupNotFound`, the old environment is already gone: skip part D.

8. **Keep anything you need from the old environment, now.** Part D deletes it, and after step 19 clients can no longer sign in to the old address. To keep an ADR, export it with the `adr_export` tool through your MCP client while the old address still works. The evidence blobs and the logs cannot be exported by this runbook. If you are unsure, stop here.

9. **Check that deployments are switched on for the repository.**

   ```powershell
   gh variable get ADR_DEPLOY_ENABLED --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP
   ```

   It prints `true`. If it prints anything else, or says the variable was not found, stop: without it the `ci-cd` workflow never deploys, also not when started by hand (item 4 of the required configuration in [Tests and CI](07-tests-and-ci.md)).

### Part B: Build the new environment next to the old one

10. **Merge the pull request** on the [pull requests page](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/pulls). The merge starts a `ci-cd` run.

11. **Open the new run.** Go to the [ci-cd runs](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/actions/workflows/ci-cd.yml) and open the newest one. `Build, test, scan` turns green. `Provision + deploy (OIDC)` then shows `Waiting`: GitHub asks you to approve a deployment to the protected environment `adr-dev` because `infra/` changed. If there is no waiting run (it was cancelled, or the job was skipped), start one and open it:

    ```powershell
    gh workflow run ci-cd.yml --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP --ref main -f environment=adr-dev
    ```

12. **Approve the deployment.** On the run, choose `Review deployments`, tick `adr-dev`, choose `Approve and deploy`. If GitHub does not offer the button for a run you started yourself, the environment is set to prevent self-review: ask a second reviewer, or switch that setting off for the run under the [environment settings](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/settings/environments) and switch it back on afterwards.

13. **Wait for the run to finish** (about 10 to 15 minutes). The `Preview` step shows a what-if plan (a dry-run list of what the deployment would do); it should list only creates, in `rg-adr-dev-scus-01`. The run ends with `Wait for the app to start` and, in `EntraBuiltIn` mode, `Verify unauthenticated MCP calls are rejected (Entra mode)`, both green. If a step is red, open its log; see "If the build in part B fails" below before you continue.

14. **Check the new group.**

    ```powershell
    az resource list --resource-group rg-adr-dev-scus-01 --subscription $sub --output table
    ```

    You should see `func-adr-dev-scus-01`, `asp-adr-dev-scus-01`, `cosno-adr-dev-scus-01`, `kv-adr-dev-scus-01`, `stadrdevscus01`, `log-adr-dev-scus-01`, `appi-adr-dev-scus-01` and `id-adr-dev-scus-01` (plus the Azure-created action group). The old group is untouched.

15. **Check that the new app answers.**

    ```powershell
    curl.exe --silent https://func-adr-dev-scus-01.azurewebsites.net/api/health
    ```

    You should see JSON that contains `"status":"ok"`. If the name does not resolve, Azure gave the app another host name; read the real one and use it instead of `func-adr-dev-scus-01.azurewebsites.net` below:

    ```powershell
    az resource show --resource-group rg-adr-dev-scus-01 --name func-adr-dev-scus-01 --resource-type Microsoft.Web/sites --subscription $sub --query properties.defaultHostName --output tsv
    ```

### Part C: Move the sign-in identity and your clients to the new address

16. **Check the sign-in mode of the new app**, which is what is really deployed. Steps 17 to 20 are for `EntraBuiltIn` mode only; in `FunctionKey` mode go straight to step 21.

    ```powershell
    (az functionapp config appsettings list --resource-group rg-adr-dev-scus-01 --name func-adr-dev-scus-01 --subscription $sub --output json | ConvertFrom-Json | Where-Object name -eq 'Auth__Mode').value
    ```

    It prints `EntraBuiltIn` or `FunctionKey` (people sign in with their Microsoft Entra account, or with a shared key). If it prints nothing or an error, stop and do not go on to part D: you do not know which mode is deployed. (With a suffix, use the names with the suffix, see "If the build in part B fails".)

17. **Read the id of the server's app registration.**

    ```powershell
    $app = gh variable get ADR_ENTRA_CLIENT_ID --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP
    ```

    It prints nothing. If it says the variable was not found, stop and do not go on to part D: the mode is `EntraBuiltIn` but the app registration is unknown, so ask the maintainer.

18. **Read its Application ID URIs and write them down.** An Application ID URI is the address Entra ID treats as the identity of this server; the MCP URL clients send must equal one of them ([ADR-0008](adr/ADR-0008-entra-oauth-sign-in-for-mcp-clients.md)).

    ```powershell
    az ad app show --id $app --query identifierUris --output json
    ```

    You should see `api://` followed by the client id, and the old MCP URL `https://func-adr-….azurewebsites.net/runtime/webhooks/mcp`. Copy the whole list somewhere: you need it to undo. If anything else is in the list, stop and ask the maintainer: the next step replaces the whole list. If `az` cannot find the application, you are signed in to another tenant: run `az login --tenant (gh variable get AZURE_TENANT_ID --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP)` and repeat.

19. **Replace the old MCP URL with the new one.** Clients can no longer sign in to the old address after this.

    ```powershell
    az ad app update --id $app --identifier-uris "api://$app" "https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp"
    ```

    It prints nothing. If the error mentions a verified domain, see step 2 of the runbook in [Entra sign-in](11-entra-sign-in.md). If it says the URI is already in use by another object, another app registration holds it: find and fix that one first.

20. **Read it back.**

    ```powershell
    az ad app show --id $app --query identifierUris --output json
    ```

    You should see exactly two entries: `api://` plus the client id, and the new MCP URL. The old host must be gone.

21. **Point each client you use at the new address** (use the new host from step 15 if it differed). [Connecting Claude](06-claude-connection.md) has the details. Do only the ones you use:

    - Claude Code with the project `.mcp.json`: set the URL it reads, then open a new terminal.

      ```powershell
      [Environment]::SetEnvironmentVariable('ADR_MCP_URL', 'https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp', 'User')
      ```

    - Claude Code with a server you added by hand: run `claude mcp remove adr`, then run `claude mcp add --transport http adr https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp` with the same `--client-id` and `--callback-port` options you used before. `claude mcp list` should show `adr` as `Connected`, or as `Needs authentication` in `EntraBuiltIn` mode: open `/mcp` inside Claude Code and sign in.
    - VS Code (`.vscode/mcp.json`): when it asks for the host, enter `func-adr-dev-scus-01.azurewebsites.net`.
    - Claude.ai, Desktop and mobile: open Customize, then Connectors, edit the connector and replace the URL. Its sign-in settings stay as they are.
    - `FunctionKey` mode only: the new app has a new key. Keep it for this window only; it is not printed:

      ```powershell
      $env:ADR_MCP_KEY = az functionapp keys list --resource-group rg-adr-dev-scus-01 --name func-adr-dev-scus-01 --subscription $sub --query systemKeys.mcp_extension --output tsv
      ```

      If the value is empty, the host has not created the key yet: wait a few minutes and repeat. To keep the key, put it in your secret manager, not in a file ([Connecting Claude](06-claude-connection.md)).

22. **Prove one client works against the new address.** In Claude Code, open `/mcp`: `adr` should be `Connected` and list its tools. If it does not, fix that before part D.

### Part D: Delete the old environment (irreversible)

23. **Stop and check.** Do part D only if step 15 showed `"status":"ok"` and step 22 worked. Nothing before this step deleted anything, and until now you can undo everything (see "To undo"). From step 24 on, the data listed under "What is lost" is gone for good.

24. **Delete the old group. This cannot be undone.**

    ```powershell
    az group delete --name rg-adr-dev --subscription $sub
    ```

    It asks `Are you sure you want to perform this operation? (y/n):`. Type `y` and press Enter. The command waits a few minutes and then returns with no output. If it fails with a message about an immutable blob or an immutability policy, use "If the delete stops at the evidence container" below, then repeat this step.

25. **Check that it is gone.**

    ```powershell
    az group exists --name rg-adr-dev --subscription $sub
    ```

    It prints `false`. If it prints `true`, the delete is still running: wait two minutes and repeat.

26. **Look at the old Key Vault, and leave it alone.** A deleted Key Vault is soft-deleted: Azure keeps it in a recycle bin for 90 days. This one has purge protection, so Azure refuses to empty the bin early ([Key Vault recovery](https://learn.microsoft.com/azure/key-vault/general/key-vault-recovery#what-are-soft-delete-and-purge-protection)).

    ```powershell
    az keyvault list-deleted --subscription $sub --output json | ConvertFrom-Json | Select-Object name, @{n='purgeDate';e={$_.properties.scheduledPurgeDate}}
    ```

    You should see a row whose name starts with `kv-adr-` and a purge date about 90 days away. Other rows are older vaults; ignore them. The old vault's name stays reserved until then, which does not matter: the new vault is called `kv-adr-dev-scus-01`.

You are done when a client lists the tools from the new address and step 25 printed `false`.

### If the build in part B fails

- **`Provision` says a name already exists or is not available.** Names are checked only at deployment, not in `Preview`. Someone else holds one of the four names that must be unique in all of Azure, or an earlier try left it behind. Set a suffix, then start the workflow again (step 11) and approve it (step 12):

  ```powershell
  gh variable set ADR_NAME_SUFFIX --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP --body b
  ```

  With suffix `b` the four global names end in `b`: `func-adr-dev-scus-01b`, `cosno-adr-dev-scus-01b`, `kv-adr-dev-scus-01b`, `stadrdevscus01b`, and the host is `func-adr-dev-scus-01b.azurewebsites.net`. Use those names in steps 14 to 21. The failed first run can leave resources with the plain names in `rg-adr-dev-scus-01` (Azure may not roll back a partly finished deployment; this was not tested): after step 14, delete any extra function app, Cosmos DB account or storage account in the portal. Leave an extra Key Vault alone: purge protection keeps it for 90 days.
- **`Deploy` or `Wait for the app to start` is red.** Open the step's log. On a first deployment the app identity's role can still be spreading (the workflow retries three times), and an unresolved Key Vault reference stops the app from starting; the comments in `.github/workflows/ci-cd.yml` say what to look for. Use `Re-run failed jobs` on the run.
- **Anything else.** Part B only added a new group. Nothing of the old environment was changed, so you can stop here, delete `rg-adr-dev-scus-01` and ask the maintainer.

### If the delete stops at the evidence container

The `evidence` container has a time-based retention policy (365 days, unlocked), which Microsoft's documentation suggests can block deletion while the container holds blobs. Remove the policy, then repeat step 24. This works only because the policy is unlocked; a locked policy cannot be removed ([Microsoft Learn](https://learn.microsoft.com/azure/storage/blobs/immutable-policy-configure-container-scope#modify-an-unlocked-retention-policy)).

```powershell
$st = (az storage account list --resource-group rg-adr-dev --subscription $sub --output json | ConvertFrom-Json).name
```

```powershell
az storage container immutability-policy delete --resource-group rg-adr-dev --account-name $st --container-name evidence --if-match "*" --subscription $sub
```

### To undo

- **Before step 24** you can go back completely: delete the new group with `az group delete --name rg-adr-dev-scus-01 --subscription $sub`, put back the Application ID URIs you wrote down in step 18 (`az ad app update --id $app --identifier-uris` followed by each URI in quotes), and point your clients back at the old address. Do **not** use `azd down` while both groups exist: it deletes the group recorded by the latest deployment, which is the new one.
- **After step 24** there is no undo for the data. The old Key Vault can be recovered for 90 days with `az keyvault recover --name` followed by the `kv-adr-…` name from step 26 and `--location southcentralus`; that brings back the vault and its secret, not the data. If you ever deploy the old template again within 90 days, recover the vault first, because its name is reserved. See Rollback in [ADR-0009](adr/ADR-0009-azure-naming-convention.md).
