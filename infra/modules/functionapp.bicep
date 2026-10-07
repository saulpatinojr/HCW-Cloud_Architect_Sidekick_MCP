param name string
param planName string
param location string
param tags object
param identityId string
param identityClientId string
param storageAccountName string
param deploymentContainerName string
param applicationInsightsConnectionString string
param cosmosEndpoint string
param keyVaultName string
param authMode string
param instanceMemoryMB int
param maximumInstanceCount int
param vnetIntegrationSubnetId string = ''

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageAccountName }

// Flex Consumption plan (FC1). Verified against the Flex Consumption how-to (2026).
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identityId
    virtualNetworkSubnetId: empty(vnetIntegrationSubnetId) ? null : vnetIntegrationSubnetId
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: identityId }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: instanceMemoryMB
        maximumInstanceCount: maximumInstanceCount
        // MCP tool calls are short; the queue worker and timers run on the same app.
        triggers: { http: { perInstanceConcurrency: 16 } }
      }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      appSettings: [
        // Host storage with managed identity (no connection string). The MCP extension's SSE transport also uses this account.
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: identityClientId }
        { name: 'AzureWebJobsStorage__blobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'AzureWebJobsStorage__queueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'AzureWebJobsStorage__tableServiceUri', value: storage.properties.primaryEndpoints.table }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'ClientId=${identityClientId};Authorization=AAD' }
        { name: 'AZURE_CLIENT_ID', value: identityClientId }
        // Application configuration (double underscore = ':' in IConfiguration)
        { name: 'Persistence__Provider', value: 'Cosmos' }
        { name: 'Cosmos__AccountEndpoint', value: cosmosEndpoint }
        { name: 'Cosmos__DatabaseName', value: 'adr' }
        { name: 'Storage__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'Storage__QueueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'Storage__JobsQueue', value: 'analysis-jobs' }
        { name: 'Auth__Mode', value: authMode }
        { name: 'Auth__StaticTenantId', value: 'default-tenant' }
        { name: 'Auth__StaticWorkspaceId', value: 'default' }
        { name: 'Auth__StaticRoles__0', value: 'Author' }
        { name: 'Auth__StaticRoles__1', value: 'Approver' }
        // Secret material comes from Key Vault via reference; the secret is created out-of-band (see docs/05-local-dev-and-deploy.md).
        { name: 'Adr__PrincipalHashSalt', value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=principal-hash-salt)' }
        { name: 'Uploads__MaxFileBytes', value: '52428800' }
        { name: 'Uploads__UploadUrlTtlMinutes', value: '15' }
        { name: 'Uploads__BlockOnCriticalSafetyFinding', value: 'true' }
        { name: 'Uploads__RequireDefenderScanResult', value: 'false' }
        // Built-in MCP auth: when authMode is EntraBuiltIn the function key requirement is removed and App Service Authentication must be enabled (docs/06-claude-connection.md).
        { name: 'AzureFunctionsJobHost__extensions__mcp__system__webhookAuthorizationLevel', value: authMode == 'EntraBuiltIn' ? 'Anonymous' : 'System' }
      ]
    }
  }
}

output name string = functionApp.name
output defaultHostName string = functionApp.properties.defaultHostName
output id string = functionApp.id
