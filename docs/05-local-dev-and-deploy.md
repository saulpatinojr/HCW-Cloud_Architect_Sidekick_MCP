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

The host lists 27 tool triggers, the `AnalysisJobWorker` queue trigger, the `MaintenanceTimer` and `health`.

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
dotnet test tests/AdrMcp.UnitTests            # 124 tests, in-memory persistence
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
   Bicep configures App Service Authentication (`authsettingsV2`): the Microsoft provider with the tenant's v2.0 issuer, require authentication, 401 for unauthenticated requests, `/api/health` excluded, and only tokens obtained by the allowed client IDs. It also sets `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` to the tenant, enrolls it in the app (`Auth__Tenants__0__Id`, so the app refuses any other tenant and scheduled maintenance covers it), and `WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES=api://<client-id>/user_impersonation`, and makes the MCP webhook `Anonymous` because the platform now authenticates. Provisioning **fails** if `EntraBuiltIn` is chosen without a client ID (GUID) or allowed client IDs, so the endpoint can never be deployed open. If App Service Authentication is later turned off, the server rejects every call instead of trusting the principal header. Don't change these settings in the portal: the next provision overwrites them, and switching back to `FunctionKey` turns App Service Authentication off again.
3. Workspaces: the enrolled tenant can use `default` only. To give it more, add app settings `Auth__Tenants__0__Workspaces__0`, `__1`, … (for example to the `Auth__Tenants__0__Id` entry in `infra/modules/functionapp.bicep`, because `azd provision` replaces portal edits). Tenant IDs are GUIDs, which Linux app setting names cannot contain, so the `Auth__TenantWorkspaces__<tenant>` form does not work for Entra tenants. `Auth__Tenants__0__MapTo` stores the tenant under a different ADR tenant ID; set it before any data exists, because existing data stays under the old ID. The app refuses to start if two tenants resolve to the same ADR tenant ID, if a tenant ID is not a GUID or appears twice, or if a tenant or workspace ID contains anything other than letters, digits, `-`, `_` and `.`.
4. Rotate/disable the system key: `az functionapp keys set -g $RG -n $APP --key-type systemKeys --key-name mcp_extension`.

## Logs

```bash
az webapp log tail -g "$RG" -n "$APP"                                   # live stream
az monitor app-insights query --app "$(azd env get-value APPLICATIONINSIGHTS_NAME)" -g "$RG" \
  --analytics-query "traces | where customDimensions.Tool != '' | project timestamp, message, customDimensions.CorrelationId, customDimensions.Tool | order by timestamp desc | take 50"
```

Audit events: Cosmos container `audit` (Data Explorer or `az cosmosdb sql container ...`), correlation id ties MCP response → logs → audit.

## Clean up safely

```bash
azd down --purge            # deletes the resource group — irreversible. Key Vault has purge protection, so it stays soft-deleted for 90 days and its name stays reserved (re-provisioning the same environment within that window needs the vault recovered). Evidence under an immutability policy blocks deletion until the retention period ends
# Alternative (review first):
az group delete --name "$RG" --no-wait
```

Immutable `evidence` container: time-based retention (365 days, unlocked). Remove the policy before deleting during development: `az storage container immutability-policy delete --account-name <st> --container-name evidence --if-match "*"` (requires the Storage Account Contributor role). **[REVIEW REQUIRED]** before running against shared environments.
