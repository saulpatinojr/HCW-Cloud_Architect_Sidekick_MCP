# H. Local development and deployment

## Prerequisites (verify)

```bash
dotnet --version            # 10.0.1xx or later 10.0 band (global.json rolls forward within 10.0)
func --version              # Azure Functions Core Tools 4.x (>= 4.0.7030 required by the MCP extension)
azurite --version           # 3.x (npm i -g azurite)
az version                  # Azure CLI 2.6x+
azd version                 # Azure Developer CLI 1.1x+
bicep --version             # 0.3x+ (az bicep install)
python3 --version           # 3.9+ for scripts/smoke-test.py (stdlib only)
```

Install Core Tools and Azurite without admin rights: `npm install -g azure-functions-core-tools@4 azurite`.

## Local settings

```bash
cp src/AdrMcp.Functions/local.settings.json.example src/AdrMcp.Functions/local.settings.json
```

`local.settings.json` is git-ignored (`.gitignore`). The example uses **InMemory** persistence (no Cosmos needed) and Azurite for Blob/Queue. Keep `Auth__Mode=FunctionKey`; locally no key is required. Never paste real connection strings or keys into this file; for Cosmos use the emulator connection string or `Cosmos__AccountEndpoint` with `az login` (DefaultAzureCredential).

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
dotnet test tests/AdrMcp.UnitTests            # in-memory persistence; the resource-naming tests need the Bicep CLI (az bicep install) and are skipped without it
dotnet test tests/AdrMcp.IntegrationTests     # Azurite-backed (skipped automatically when Azurite is down)
ADR_TEST_COSMOS_CONNECTION_STRING="<emulator>" dotnet test tests/AdrMcp.IntegrationTests   # Cosmos tests
```

## Provision Azure resources

```bash
az login
azd auth login
azd init -e adr-dev                           # creates .azure/adr-dev
azd env set AZURE_LOCATION eastus2            # a Flex Consumption region
azd env set AZURE_PRINCIPAL_ID "$(az ad signed-in-user show --query id -o tsv)"   # grants you data-plane roles for testing (omit in prod)
azd env set ADR_AUTH_MODE FunctionKey         # or EntraBuiltIn (then also ADR_ENTRA_CLIENT_ID and ADR_ENTRA_ALLOWED_CLIENT_IDS, see below)
azd env set ADR_PRIVATE_NETWORKING false      # true for private endpoints
azd provision                                  # Bicep: RG, identity, storage, Cosmos, Key Vault, App Insights, Flex Consumption app, RBAC
```

### Resource names and tags

Every name is built in one place, `infra/main.bicep`, from `<abbreviation>-<workload>-<environment>-<region code>-<nn>`. The abbreviations are Microsoft's published ones ([list](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations)); the reasons and the Azure name rules are in [ADR-0009](adr/ADR-0009-azure-naming-convention.md). With the defaults (`ADR_WORKLOAD_NAME=adr`, `ADR_ENVIRONMENT_SHORT=dev`) in `southcentralus` you get:

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
- The azd environment name (`adr-dev`) is **not** part of any name. It stays the azd environment, the GitHub environment and the `azd-env-name` tag.
- The region needs a short code in `infra/region-codes.json` (16 regions today, the table is in the ADR). For another region the deployment stops at once and says so; add the code first.
- The function app, the Cosmos DB account, the Key Vault and the storage account must be unique in all of Azure. If Azure says one is taken (for example a Key Vault that is still soft-deleted), set a suffix and provision again: `azd env set ADR_NAME_SUFFIX b` (in GitHub: the repository variable `ADR_NAME_SUFFIX`). The suffix goes on those four names only, for example `kv-adr-dev-scus-01b`. Use lower-case letters and digits, at most four.
- Tags on the group and every resource: `workload` (`adr`), `application` (`adr-mcp`), `environment` (`dev`), `azd-env-name` (`adr-dev`), and `component` (`api`, `data`, `security`, `monitoring` or `network`) on each resource.
- Changing a name later means replacing the resource. Azure cannot rename most resources.

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

Retrieve connection details (never commit the key):

```bash
APP=$(azd env get-value AZURE_FUNCTION_APP_NAME); RG=$(azd env get-value AZURE_RESOURCE_GROUP)
az functionapp keys list -g "$RG" -n "$APP" --query systemKeys.mcp_extension -o tsv > /dev/null   # pipe into your secret store, do not echo in CI logs
echo "MCP endpoint: $(azd env get-value MCP_ENDPOINT)"
```

Post-deployment smoke test:

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
4. Optional second layer (**off by default**): the app also verifies the bearer token itself. Set `azd env set ADR_ENTRA_VALIDATE_IN_APP true` (and, only for a custom domain, `ADR_ENTRA_RESOURCE_URI <canonical MCP URL>`) and run `azd provision`; in GitHub set the repository variables of the same names. Bicep passes the tenant, server client ID and the same allowed client IDs as `Auth__Entra__*` settings. Turn it on only after the real-tenant runbook in [Entra sign-in](11-entra-sign-in.md) passes; to roll back set it to `false` and provision again. The app then also serves `/.well-known/oauth-protected-resource` and `/api/auth/check`.
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
azd down                    # asks you to confirm, then deletes the resource group — irreversible. Key Vault has purge protection, so it stays soft-deleted for 90 days and nobody can purge it sooner (--purge only skips the confirmation question). Its name stays reserved: creating the environment again within 90 days needs a new name, so set ADR_NAME_SUFFIX first (see Resource names and tags). Evidence under an immutability policy blocks deletion until the retention period ends
# Alternative (review first):
az group delete --name "$RG" --no-wait
```

Immutable `evidence` container: time-based retention (365 days, unlocked). Remove the policy before deleting during development: `az storage container immutability-policy delete --account-name <st> --container-name evidence --if-match "*"` (requires the Storage Account Contributor role). **[REVIEW REQUIRED]** before running against shared environments.

## Replacing the dev environment after the naming change

**Who and when.** This is an owner step. Do it once, after the pull request that introduced [ADR-0009](adr/ADR-0009-azure-naming-convention.md) is merged. Until you do, `adr-dev` keeps working under its old names; the steps below remove the old environment and create the new one.

**Why it cannot be done in place.** Azure cannot rename a resource group, a function app, a Key Vault or most other resources ([Microsoft's naming guidance](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming) says the same). So the old group `rg-adr-dev` is deleted and a new group `rg-adr-dev-scus-01` is created. The workflow that created the old group (`ci-cd`) creates the new one, so this runbook uses it and the Azure CLI. `azd` was not found on the maintainer's workstation when this was written; if you use it, `azd down` (without `--purge`) followed by `azd up` reaches the same result.

**What changes.**

| | Before | After |
|---|---|---|
| Resource group | `rg-adr-dev` | `rg-adr-dev-scus-01` |
| Function app and its host | `func-adr-<token>`, `func-adr-<token>.azurewebsites.net` | `func-adr-dev-scus-01`, `func-adr-dev-scus-01.azurewebsites.net` |
| Other resources | `…-adr-<token>` (a hash) | `…-adr-dev-scus-01`, storage `stadrdevscus01` (see [Resource names and tags](#resource-names-and-tags)) |
| MCP URL | `https://func-adr-<token>.azurewebsites.net/runtime/webhooks/mcp` | `https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp` |

**What is lost (irreversible).** The Cosmos DB data (ADRs, history, findings, audit and memory), everything in the storage account (uploaded evidence, reports), the logs, and the Key Vault secret `principal-hash-salt` (a new random one is created, so old principal hashes would no longer match; nothing is kept, so nothing is compared). While the old environment is deleted and the new one is being built, no MCP client can connect. Expect about 5 minutes for the delete and 10 to 15 minutes for the deployment.

You need: Windows PowerShell 5.1 or PowerShell 7, the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) signed in with an account that can delete a resource group in the subscription, the [GitHub CLI](https://cli.github.com/) signed in (`gh auth status`), and, in `EntraBuiltIn` mode, the right to edit the server's Entra app registration. None of these commands reads a file from the repository, so you can run them in any folder.

1. **Make sure nothing in the old environment is needed.** Open the Azure portal on [Resource groups](https://portal.azure.com/#browse/resourcegroups) and look at `rg-adr-dev`. If you want to keep any ADR or evidence, export it with the `adr_export` tool or copy the blobs now; after step 6 it is gone. You should see about nine resources. If you are not sure, stop here.

2. **Open PowerShell and check both sign-ins.**

   ```powershell
   az account show --output none
   ```

   ```powershell
   gh auth status
   ```

   The first prints nothing. The second prints `Logged in to github.com`. If `az` says `Please run 'az login'`, run `az login` and repeat.

3. **Read the subscription from the repository variable, so no id is typed or stored in the repository.**

   ```powershell
   $sub = gh variable get AZURE_SUBSCRIPTION_ID --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP
   ```

   ```powershell
   az account show --subscription $sub --query name --output tsv
   ```

   The second command prints the subscription's name. If it says the subscription was not found, you are signed in with an account that cannot see it: run `az login` with the right account.

4. **Look at the old group.**

   ```powershell
   az resource list --resource-group rg-adr-dev --subscription $sub --output table
   ```

   You should see the function app, plan, Cosmos DB account, Key Vault, storage account, workspace, Application Insights and identity. If it says `ResourceGroupNotFound`, the old group is already gone: continue with step 8.

5. **Merge the pull request, then leave the deployment waiting.** After the merge, open the [ci-cd runs](https://github.com/saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP/actions/workflows/ci-cd.yml) and open the newest one. `Build, test, scan` should turn green. `Provision + deploy (OIDC)` shows `Waiting`: GitHub is asking you to approve a deployment to `adr-dev` because `infra/` changed. **Do not approve it yet.** If the second job is skipped, nothing deployable changed; you will start the deployment yourself in step 9.

6. **Delete the old group. This cannot be undone.** Everything listed under "What is lost" disappears. Run:

   ```powershell
   az group delete --name rg-adr-dev --subscription $sub
   ```

   It asks `Are you sure you want to perform this operation? (y/n):`. Type `y` and press Enter. The command waits a few minutes and then returns with no output. If it fails with a message about an immutable blob or an immutability policy, use "If the delete stops at the evidence container" below, then run this step again.

7. **Check that it is gone.**

   ```powershell
   az group exists --name rg-adr-dev --subscription $sub
   ```

   It prints `false`. If it prints `true`, the delete is still running: wait two minutes and repeat.

8. **Look at the old Key Vault, and leave it alone.**

   ```powershell
   az keyvault list-deleted --subscription $sub --output table
   ```

   You should see one row named `kv-adr-…` with a purge date about 90 days away. The vault has purge protection, so nobody can purge it sooner ([Key Vault recovery](https://learn.microsoft.com/azure/key-vault/general/key-vault-recovery#what-are-soft-delete-and-purge-protection)). Its name stays reserved, which does not matter: the new vault is called `kv-adr-dev-scus-01`.

9. **Approve the deployment.** On the waiting run from step 5, choose `Review deployments`, tick `adr-dev` and choose `Approve and deploy`. If there is no waiting run, start one:

   ```powershell
   gh workflow run ci-cd.yml --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP --ref main -f environment=adr-dev
   ```

   then approve it the same way. The job first shows the what-if plan (`Preview`): it should list only creates, in `rg-adr-dev-scus-01`. It ends with `Wait for the app to start` and, in `EntraBuiltIn` mode, `Verify unauthenticated MCP calls are rejected`, both green. If `Preview` or `Provision` fails because a name already exists, set the suffix and start the workflow again:

   ```powershell
   gh variable set ADR_NAME_SUFFIX --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP --body b
   ```

10. **Check the new group.**

    ```powershell
    az resource list --resource-group rg-adr-dev-scus-01 --subscription $sub --output table
    ```

    You should see `func-adr-dev-scus-01`, `asp-adr-dev-scus-01`, `cosno-adr-dev-scus-01`, `kv-adr-dev-scus-01`, `stadrdevscus01`, `log-adr-dev-scus-01`, `appi-adr-dev-scus-01` and `id-adr-dev-scus-01` (plus the `Application Insights Smart Detection` action group that Azure adds itself).

11. **Check that the app answers.**

    ```powershell
    curl.exe --silent https://func-adr-dev-scus-01.azurewebsites.net/api/health
    ```

    You should see JSON that contains `"status":"ok"`. If the name does not resolve, Azure gave the app a different host name; read the real one and use it in the steps below:

    ```powershell
    az resource show --resource-group rg-adr-dev-scus-01 --name func-adr-dev-scus-01 --resource-type Microsoft.Web/sites --subscription $sub --query properties.defaultHostName --output tsv
    ```

12. **`EntraBuiltIn` mode only: move the Application ID URI to the new host.** Check the mode first; if it prints `FunctionKey`, skip to step 13.

    ```powershell
    gh variable get ADR_AUTH_MODE --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP
    ```

    The server's app registration has the old MCP URL as one of its Application ID URIs (observed on 2026-10-26), and the MCP URL clients send must equal one of them ([ADR-0008](adr/ADR-0008-entra-oauth-sign-in-for-mcp-clients.md)). Replace the old one:

    ```powershell
    $app = gh variable get ADR_ENTRA_CLIENT_ID --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP
    ```

    ```powershell
    az ad app update --id $app --identifier-uris "api://$app" "https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp"
    ```

    ```powershell
    az ad app show --id $app --query identifierUris --output json
    ```

    The last command lists two URIs: `api://` followed by the client id, and the new MCP URL. The old host must not appear. If `az` cannot find the application, you are signed in to another tenant: run `az login --tenant (gh variable get AZURE_TENANT_ID --repo saulpatinojr/Proj-Cloud_Architect_Sidekick_MCP)` and repeat. If it mentions a verified domain, see [Entra sign-in](11-entra-sign-in.md), step 2 of the runbook.

13. **Point your clients at the new host.** Do the ones you use ([Connecting Claude](06-claude-connection.md) has the details):

    - Claude Code with the project `.mcp.json`: set the URL it reads, then open a new terminal.

      ```powershell
      [Environment]::SetEnvironmentVariable('ADR_MCP_URL', 'https://func-adr-dev-scus-01.azurewebsites.net/runtime/webhooks/mcp', 'User')
      ```

    - Claude Code with a server you added by hand: run `claude mcp remove adr`, then the same `claude mcp add --transport http adr …` command you used before with the new URL, then `claude mcp list`. It shows `Connected`, or `Needs authentication` in `EntraBuiltIn` mode: open `/mcp` inside Claude Code and sign in.
    - VS Code (`.vscode/mcp.json`): when it asks for the host, enter `func-adr-dev-scus-01.azurewebsites.net`.
    - Claude.ai, Desktop and mobile: open Customize, then Connectors, edit the connector and replace the URL. Its sign-in settings stay as they are.
    - `FunctionKey` mode only: the new app has a new key. Store it without printing it:

      ```powershell
      [Environment]::SetEnvironmentVariable('ADR_MCP_KEY', (az functionapp keys list --resource-group rg-adr-dev-scus-01 --name func-adr-dev-scus-01 --subscription $sub --query systemKeys.mcp_extension --output tsv), 'User')
      ```

You are done when step 11 shows `"status":"ok"` and a client lists the tools from the new URL.

**If the delete stops at the evidence container.** The `evidence` container has a time-based retention policy (365 days, unlocked), which can block deletion while it holds blobs. Remove the policy, then repeat step 6. This is allowed only because the policy is unlocked; a locked policy cannot be removed ([Microsoft Learn](https://learn.microsoft.com/azure/storage/blobs/immutable-policy-configure-container-scope#modify-an-unlocked-retention-policy)).

```powershell
$st = (az storage account list --resource-group rg-adr-dev --subscription $sub --output json | ConvertFrom-Json).name
```

```powershell
az storage container immutability-policy delete --resource-group rg-adr-dev --account-name $st --container-name evidence --if-match "*" --subscription $sub
```

**If you approved the deployment before step 6.** Nothing breaks: the new group is created next to the old one and the two do not touch. Do step 6 afterwards. Do **not** use `azd down` in that situation: it deletes the group recorded by the last deployment, which is then the new one.

**To undo.** There is no undo for the delete. To go back to the old naming, revert the pull request and deploy; that creates a group named `rg-adr-dev` again, empty. See the Rollback section of [ADR-0009](adr/ADR-0009-azure-naming-convention.md).
