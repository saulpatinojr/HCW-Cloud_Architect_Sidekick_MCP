// ADR Intelligence MCP Server - Azure infrastructure (subscription scope, azd compatible)
// Creates: resource group, user-assigned identity, storage (blob + queue), Cosmos DB (serverless NoSQL),
// Key Vault, Log Analytics + Application Insights, Flex Consumption Function App, RBAC.
// No secrets are emitted: every data-plane connection uses managed identity.
// Names and tags follow ADR-0009 (docs/adr/ADR-0009-azure-naming-convention.md): <abbr>-<workload>-<environment>-<region>-<nn>.
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment (for example adr-dev). Used for azd bookkeeping, the GitHub environment and the azd-env-name tag; it is NOT part of the resource names (see workloadName and environmentShort).')
param environmentName string

@minLength(1)
@description('Primary Azure region (must support Flex Consumption and be listed in region-codes.json).')
param location string

@minLength(2)
@maxLength(10)
@description('Short workload name used in every resource name: lower-case letters and digits only, not starting with "obsidian" and not a prefix of it (ADR-0009; that prefix belongs to the Obsidian vault server\'s template, and the storage account name joins workload and environment without a separator).')
param workloadName string = 'adr'

@minLength(2)
@maxLength(5)
@description('Short environment name used in every resource name, for example dev, test or prod: lower-case letters and digits only (ADR-0009). Separate from environmentName so the azd and GitHub environment name can stay as it is, but environmentName must end with "-<environmentShort>" (adr-dev with dev), so that adding a second azd environment without its own short name fails at once instead of sharing resource names.')
param environmentShort string = 'dev'

@maxLength(4)
@description('Optional text appended to the four globally unique names (function app, Cosmos DB account, Key Vault, storage account): lower-case letters and digits only. Leave empty. Set it, for example to "b", only when one of those names is already taken, such as a Key Vault that is still soft-deleted (ADR-0009).')
param nameSuffix string = ''

@description('Optional resource group name override. Empty means the convention name, for example rg-adr-dev-scus-01.')
param resourceGroupName string = ''

@description('Authentication mode for the MCP server: FunctionKey (default, mcp_extension system key) or EntraBuiltIn (App Service Authentication with Entra ID, preview; requires entraClientId).')
@allowed([ 'FunctionKey', 'EntraBuiltIn' ])
param authMode string = 'FunctionKey'

@description('Client ID of the Entra app registration that represents this MCP server. Required when authMode is EntraBuiltIn.')
param entraClientId string = ''

@description('Entra tenant that issues tokens for entraClientId. Empty means the deployment tenant.')
param entraTenantId string = ''

@description('Comma-separated client IDs of the applications allowed to call the server (for example your Claude Code public client and Claude connector app). Required when authMode is EntraBuiltIn.')
param entraAllowedClientIds string = ''

@description('EntraBuiltIn only: client ID of the application (the deploy pipeline\'s federated identity, AZURE_CLIENT_ID) that may read the server with no signed-in user. It is added to the allowed client IDs and, with entraValidateInApp, turns Auth__Entra__AllowApplicationTokens on; the app treats every application as Reader at most. Empty (default): no application may call. Set ADR_ENTRA_PIPELINE_CLIENT_ID; the deploy job sets it from AZURE_CLIENT_ID when the repository variable ADR_ENTRA_PIPELINE_READ is true (docs/11-entra-sign-in.md).')
param entraPipelineClientId string = ''

@description('EntraBuiltIn only: also verify the bearer token inside the app, in addition to App Service Authentication (ADR-0008, docs/11-entra-sign-in.md). Off by default; turn it on only after the real-tenant runbook passes.')
param entraValidateInApp bool = false

@description('Canonical MCP URL when the app is reached on a custom domain. Empty means https://<default host name>/runtime/webhooks/mcp.')
param entraResourceUri string = ''

@description('Enable VNet integration with private endpoints for Storage, Cosmos DB and Key Vault (production option).')
param enablePrivateNetworking bool = false

@description('Enable Microsoft Defender for Storage malware scanning on the storage account (requires Defender plan).')
param enableDefenderForStorage bool = false

@description('Principal id (object id) of the deploying user/pipeline that should get data-plane roles for local testing and smoke tests. Leave empty in production.')
param deployerPrincipalId string = ''

@description('Cosmos DB consistency level.')
@allowed([ 'Session', 'BoundedStaleness', 'Strong' ])
param cosmosConsistency string = 'Session'

@description('Instance memory for Flex Consumption (MB).')
@allowed([ 2048, 4096 ])
param instanceMemoryMB int = 2048

@description('.NET isolated worker version for Flex Consumption; must match Directory.Build.props TargetFramework (ADR-0007). 8.0 is a rollback target only until .NET 8 end of support.')
@allowed([ '8.0', '10.0' ])
param functionsDotnetVersion string = '10.0'

@description('Maximum Flex Consumption instance count.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 100

// ---- Naming (ADR-0009) --------------------------------------------------------------------------------------------------
// Pattern: <abbreviation>-<workload>[-<component>]-<environment>-<region code>-<instance>, for example kv-adr-dev-scus-01.
// The storage account name allows lower-case letters and digits only, so it has no hyphens: stadrdevscus01.
var abbrs = loadJsonContent('./abbreviations.json')
var regionCodes = loadJsonContent('./region-codes.json')
var regionKey = toLower(replace(location, ' ', ''))
var regionCode = regionCodes[?regionKey] ?? ''
var instance = '01'
var nameTail = '${environmentShort}-${regionCode}-${instance}'
var baseName = '${workloadName}-${nameTail}'

// The four names that must be unique across all of Azure (they become public host names or DNS names) take the optional suffix.
var keyVaultName = '${abbrs.keyVaultVaults}-${baseName}${nameSuffix}'
var storageAccountName = '${abbrs.storageStorageAccounts}${workloadName}${environmentShort}${regionCode}${instance}${nameSuffix}'
var cosmosAccountName = '${abbrs.documentDBDatabaseAccounts}-${baseName}${nameSuffix}'
var functionAppName = '${abbrs.webSitesFunctions}-${baseName}${nameSuffix}'

// Stop before anything is created, with a message that says what to change. Azure would otherwise reject the name part-way through the deployment.
// The Key Vault limit (24 characters) is the tightest of the four global names: the storage account (24) and the function app (32, the host ID rule)
// fit whenever the vault name fits. The unit test ResourceNamingTests checks all three for every region.
func isLowerAlphanumeric(value string) bool => empty(filter(map(range(0, length(value)), i => substring(value, i, 1)), c => !contains('abcdefghijklmnopqrstuvwxyz0123456789', c)))

var nameProblem = empty(regionCode)
  ? 'Region "${location}" has no short code. Add it to infra/region-codes.json and to the region table in docs/adr/ADR-0009-azure-naming-convention.md, then deploy again.'
  : !(isLowerAlphanumeric(workloadName) && isLowerAlphanumeric(environmentShort) && isLowerAlphanumeric(nameSuffix))
    ? 'workloadName, environmentShort and nameSuffix may hold lower-case letters and digits only (ADR-0009).'
    : startsWith(workloadName, 'obsidian') || startsWith('obsidian', workloadName)
      ? 'workloadName "${workloadName}" is reserved for the Obsidian vault server (infra/vault/main.bicep requires names that start with "obsidian"; a prefix of "obsidian" is refused too, because the storage account name joins workload and environment without a separator), so that the two deployments can never share a resource group or a global name. Choose another ADR_WORKLOAD_NAME (ADR-0009).'
    : !(environmentName == environmentShort || endsWith(environmentName, '-${environmentShort}'))
      ? 'environmentName "${environmentName}" must end with "-${environmentShort}". The azd environment name is not part of any resource name, so two azd environments in one subscription and region with the same environmentShort would get the same names and overwrite each other. Name the azd environment adr-<short> (for example adr-prod) and set ADR_ENVIRONMENT_SHORT to <short> for it (ADR-0009).'
      : length(keyVaultName) > 24
        ? 'The Key Vault name ${keyVaultName} is longer than the 24 characters Azure allows. Shorten workloadName, environmentShort or nameSuffix.'
        : ''

var names = !empty(nameProblem)
  ? fail(nameProblem)
  : {
    resourceGroup: !empty(resourceGroupName) ? resourceGroupName : '${abbrs.resourcesResourceGroups}-${baseName}'
    identity: '${abbrs.managedIdentityUserAssignedIdentities}-${baseName}'
    logAnalytics: '${abbrs.operationalInsightsWorkspaces}-${baseName}'
    applicationInsights: '${abbrs.insightsComponents}-${baseName}'
    appServicePlan: '${abbrs.webServerFarms}-${baseName}'
    functionApp: functionAppName
    cosmosAccount: cosmosAccountName
    keyVault: keyVaultName
    storageAccount: storageAccountName
    virtualNetwork: '${abbrs.networkVirtualNetworks}-${baseName}'
    appSubnet: '${abbrs.networkVirtualNetworksSubnets}-${workloadName}-app-${nameTail}'
    privateEndpointSubnet: '${abbrs.networkVirtualNetworksSubnets}-${workloadName}-pep-${nameTail}'
    cosmosPrivateEndpoint: '${abbrs.networkPrivateEndpoints}-${workloadName}-cosmos-${nameTail}'
    keyVaultPrivateEndpoint: '${abbrs.networkPrivateEndpoints}-${workloadName}-kv-${nameTail}'
    blobPrivateEndpoint: '${abbrs.networkPrivateEndpoints}-${workloadName}-blob-${nameTail}'
    queuePrivateEndpoint: '${abbrs.networkPrivateEndpoints}-${workloadName}-queue-${nameTail}'
  }

// Tags (ADR-0009). 'component' is added per module below. 'azd-env-name' stays: azd needs it to find the environment's resources.
var tags = {
  'azd-env-name': environmentName
  workload: workloadName
  application: 'adr-mcp'
  environment: environmentShort
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: names.resourceGroup
  location: location
  tags: tags
}

module identity './modules/identity.bicep' = {
  name: 'identity'
  scope: rg
  params: { name: names.identity, location: location, tags: union(tags, { component: 'security' }) }
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    logAnalyticsName: names.logAnalytics
    applicationInsightsName: names.applicationInsights
    location: location
    tags: union(tags, { component: 'monitoring' })
  }
}

module network './modules/network.bicep' = if (enablePrivateNetworking) {
  name: 'network'
  scope: rg
  params: {
    name: names.virtualNetwork
    appSubnetName: names.appSubnet
    privateEndpointSubnetName: names.privateEndpointSubnet
    location: location
    tags: union(tags, { component: 'network' })
  }
}

module storage './modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    name: names.storageAccount
    blobPrivateEndpointName: names.blobPrivateEndpoint
    queuePrivateEndpointName: names.queuePrivateEndpoint
    location: location
    tags: union(tags, { component: 'data' })
    enablePrivateNetworking: enablePrivateNetworking
    privateEndpointSubnetId: enablePrivateNetworking ? network.?outputs.privateEndpointSubnetId ?? '' : ''
    vnetId: enablePrivateNetworking ? network.?outputs.vnetId ?? '' : ''
    vnetName: names.virtualNetwork
    enableDefenderForStorage: enableDefenderForStorage
    jobsQueueName: 'analysis-jobs'
    blobContainers: [ 'adr-source', 'uploads-quarantine', 'evidence', 'evidence-sanitized', 'reports', 'snapshots', 'manifests', 'diagrams', 'deployment' ]
  }
}

module cosmos './modules/cosmos.bicep' = {
  name: 'cosmos'
  scope: rg
  params: {
    name: names.cosmosAccount
    privateEndpointName: names.cosmosPrivateEndpoint
    location: location
    tags: union(tags, { component: 'data' })
    databaseName: 'adr'
    consistency: cosmosConsistency
    enablePrivateNetworking: enablePrivateNetworking
    privateEndpointSubnetId: enablePrivateNetworking ? network.?outputs.privateEndpointSubnetId ?? '' : ''
    vnetId: enablePrivateNetworking ? network.?outputs.vnetId ?? '' : ''
    vnetName: names.virtualNetwork
  }
}

module keyVault './modules/keyvault.bicep' = {
  name: 'keyvault'
  scope: rg
  params: {
    name: names.keyVault
    privateEndpointName: names.keyVaultPrivateEndpoint
    location: location
    tags: union(tags, { component: 'security' })
    enablePrivateNetworking: enablePrivateNetworking
    privateEndpointSubnetId: enablePrivateNetworking ? network.?outputs.privateEndpointSubnetId ?? '' : ''
    vnetId: enablePrivateNetworking ? network.?outputs.vnetId ?? '' : ''
    vnetName: names.virtualNetwork
  }
}

module functionApp './modules/functionapp.bicep' = {
  name: 'functionapp'
  scope: rg
  params: {
    name: names.functionApp
    planName: names.appServicePlan
    location: location
    tags: union(tags, { component: 'api' })
    identityId: identity.outputs.id
    identityClientId: identity.outputs.clientId
    storageAccountName: storage.outputs.name
    deploymentContainerName: 'deployment'
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    cosmosEndpoint: cosmos.outputs.endpoint
    keyVaultName: keyVault.outputs.name
    authMode: authMode
    instanceMemoryMB: instanceMemoryMB
    maximumInstanceCount: maximumInstanceCount
    dotnetVersion: functionsDotnetVersion
    entraClientId: entraClientId
    entraTenantId: entraTenantId
    entraAllowedClientIds: entraAllowedClientIds
    entraPipelineClientId: entraPipelineClientId
    entraValidateInApp: entraValidateInApp
    entraResourceUri: entraResourceUri
    vnetIntegrationSubnetId: enablePrivateNetworking ? network.?outputs.appSubnetId ?? '' : ''
  }
}

module rbac './modules/rbac.bicep' = {
  name: 'rbac'
  scope: rg
  params: {
    principalId: identity.outputs.principalId
    deployerPrincipalId: deployerPrincipalId
    storageAccountName: storage.outputs.name
    cosmosAccountName: cosmos.outputs.name
    keyVaultName: keyVault.outputs.name
    applicationInsightsName: monitoring.outputs.applicationInsightsName
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_FUNCTION_APP_NAME string = functionApp.outputs.name
output AZURE_FUNCTION_APP_HOSTNAME string = functionApp.outputs.defaultHostName
output MCP_ENDPOINT string = 'https://${functionApp.outputs.defaultHostName}/runtime/webhooks/mcp'
output AZURE_STORAGE_ACCOUNT string = storage.outputs.name
output AZURE_COSMOS_ENDPOINT string = cosmos.outputs.endpoint
output AZURE_KEY_VAULT_NAME string = keyVault.outputs.name
output AZURE_MANAGED_IDENTITY_CLIENT_ID string = identity.outputs.clientId
output APPLICATIONINSIGHTS_NAME string = monitoring.outputs.applicationInsightsName
