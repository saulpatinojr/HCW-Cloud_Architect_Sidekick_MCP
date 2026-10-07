// ADR Intelligence MCP Server - Azure infrastructure (subscription scope, azd compatible)
// Creates: resource group, user-assigned identity, storage (blob + queue), Cosmos DB (serverless NoSQL),
// Key Vault, Log Analytics + Application Insights, Flex Consumption Function App, RBAC.
// No secrets are emitted: every data-plane connection uses managed identity.
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment; used to derive resource names.')
param environmentName string

@minLength(1)
@description('Primary Azure region (must support Flex Consumption).')
param location string

@description('Optional resource group name override.')
param resourceGroupName string = ''

@description('Authentication mode for the MCP server: FunctionKey (default, mcp_extension system key) or EntraBuiltIn (App Service Authentication, configure after deploy).')
@allowed([ 'FunctionKey', 'EntraBuiltIn' ])
param authMode string = 'FunctionKey'

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

@description('Maximum Flex Consumption instance count.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 100

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = { 'azd-env-name': environmentName, workload: 'adr-mcp' }
var rgName = !empty(resourceGroupName) ? resourceGroupName : '${abbrs.resourcesResourceGroups}${environmentName}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: tags
}

module identity './modules/identity.bicep' = {
  name: 'identity'
  scope: rg
  params: { name: '${abbrs.managedIdentityUserAssignedIdentities}adr-${resourceToken}', location: location, tags: tags }
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    logAnalyticsName: '${abbrs.operationalInsightsWorkspaces}adr-${resourceToken}'
    applicationInsightsName: '${abbrs.insightsComponents}adr-${resourceToken}'
    location: location
    tags: tags
  }
}

module network './modules/network.bicep' = if (enablePrivateNetworking) {
  name: 'network'
  scope: rg
  params: { name: '${abbrs.networkVirtualNetworks}adr-${resourceToken}', location: location, tags: tags }
}

module storage './modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    name: '${abbrs.storageStorageAccounts}adr${resourceToken}'
    location: location
    tags: tags
    enablePrivateNetworking: enablePrivateNetworking
    privateEndpointSubnetId: enablePrivateNetworking ? network.?outputs.privateEndpointSubnetId ?? '' : ''
    vnetId: enablePrivateNetworking ? network.?outputs.vnetId ?? '' : ''
    enableDefenderForStorage: enableDefenderForStorage
    jobsQueueName: 'analysis-jobs'
    blobContainers: [ 'adr-source', 'uploads-quarantine', 'evidence', 'evidence-sanitized', 'reports', 'snapshots', 'manifests', 'diagrams', 'deployment' ]
  }
}

module cosmos './modules/cosmos.bicep' = {
  name: 'cosmos'
  scope: rg
  params: {
    name: '${abbrs.documentDBDatabaseAccounts}adr-${resourceToken}'
    location: location
    tags: tags
    databaseName: 'adr'
    consistency: cosmosConsistency
    enablePrivateNetworking: enablePrivateNetworking
    privateEndpointSubnetId: enablePrivateNetworking ? network.?outputs.privateEndpointSubnetId ?? '' : ''
    vnetId: enablePrivateNetworking ? network.?outputs.vnetId ?? '' : ''
  }
}

module keyVault './modules/keyvault.bicep' = {
  name: 'keyvault'
  scope: rg
  params: {
    name: '${abbrs.keyVaultVaults}adr-${resourceToken}'
    location: location
    tags: tags
    enablePrivateNetworking: enablePrivateNetworking
    privateEndpointSubnetId: enablePrivateNetworking ? network.?outputs.privateEndpointSubnetId ?? '' : ''
    vnetId: enablePrivateNetworking ? network.?outputs.vnetId ?? '' : ''
  }
}

module functionApp './modules/functionapp.bicep' = {
  name: 'functionapp'
  scope: rg
  params: {
    name: '${abbrs.webSitesFunctions}adr-${resourceToken}'
    planName: '${abbrs.webServerFarms}adr-${resourceToken}'
    location: location
    tags: tags
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
