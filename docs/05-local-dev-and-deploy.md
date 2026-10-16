# H. Local development and deployment

## Prerequisites (verify)

```bash
dotnet --version            # 8.0.4xx (global.json rolls forward within 8.0)
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
azd env set ADR_AUTH_MODE FunctionKey         # or EntraBuiltIn
azd env set ADR_PRIVATE_NETWORKING false      # true for private endpoints
azd provision                                  # Bicep: RG, identity, storage, Cosmos, Key Vault, App Insights, Flex Consumption app, RBAC
```

`scripts/postprovision.sh` creates the Key Vault secret `principal-hash-salt` (random) that the app reads through a Key Vault reference.

Preview before applying (recommended for existing environments):

```bash
az deployment sub what-if --location eastus2 --template-file infra/main.bicep --parameters environmentName=adr-dev location=eastus2
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
python3 scripts/smoke-test.py --url "$(azd env get-value MCP_ENDPOINT)" --key "$(az functionapp keys list -g "$RG" -n "$APP" --query systemKeys.mcp_extension -o tsv)"
```

### Enable built-in Entra authentication (preview) instead of the key

1. Deploy with `ADR_AUTH_MODE=EntraBuiltIn` (sets `AzureFunctionsJobHost__extensions__mcp__system__webhookAuthorizationLevel=Anonymous`).
2. Portal → Function App → **AI (preview)** → **Turn on MCP authentication** (creates the Entra app registration, PRM settings and auth config), or follow the manual steps in the Microsoft tutorial (identity provider = Microsoft, "Require authentication", 401 for unauthenticated, token store on, `WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES=api://<client-id>/user_impersonation`).
3. Add app roles `Adr.Reader/Author/Approver/Admin` to the registration and assign users; pre-authorize the client application ids you will use (see docs/06).
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
