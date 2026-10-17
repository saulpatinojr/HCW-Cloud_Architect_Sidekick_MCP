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
@allowed([ '8.0', '10.0' ])
param dotnetVersion string = '10.0'
param entraClientId string = ''
param entraTenantId string = ''

var entraEnabled = authMode == 'EntraBuiltIn'
// EntraBuiltIn makes the MCP webhook Anonymous; without App Service Authentication in front of it the endpoint would be open,
// so fail the deployment instead of deploying that combination.
var entraScope = entraEnabled
  ? (empty(entraClientId) ? fail('authMode EntraBuiltIn requires entraClientId (the MCP server app registration client ID).') : 'api://${entraClientId}/user_impersonation')
  : ''
var entraIssuer = '${environment().authentication.loginEndpoint}${empty(entraTenantId) ? tenant().tenantId : entraTenantId}/v2.0'

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
      runtime: { name: 'dotnet-isolated', version: dotnetVersion }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      appSettings: concat([
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
        // Built-in MCP auth: with EntraBuiltIn, App Service Authentication (below) validates every request, so the function key requirement is removed.
        { name: 'AzureFunctionsJobHost__extensions__mcp__system__webhookAuthorizationLevel', value: entraEnabled ? 'Anonymous' : 'System' }
      ], entraEnabled ? [
        // Protected resource metadata (RFC 9728) so MCP clients can discover the Entra scope (built-in MCP server authorization, preview).
        { name: 'WEBSITE_AUTH_PRM_DEFAULT_WITH_SCOPES', value: entraScope }
      ] : [])
    }
  }
}

// Built-in MCP server authorization: App Service Authentication requires an Entra token for this app before any function runs.
resource authSettings 'Microsoft.Web/sites/config@2024-04-01' = if (entraEnabled) {
  parent: functionApp
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
      excludedPaths: [ '/api/health' ]   // anonymous liveness probe; returns no configuration or data
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: { clientId: entraClientId, openIdIssuer: entraIssuer }
        validation: { allowedAudiences: [ 'api://${entraClientId}', entraClientId ] }
      }
    }
    login: { tokenStore: { enabled: false } }
  }
}

output name string = functionApp.name
output defaultHostName string = functionApp.properties.defaultHostName
output id string = functionApp.id
