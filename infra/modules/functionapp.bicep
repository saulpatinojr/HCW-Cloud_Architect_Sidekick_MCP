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
@description('Comma-separated client IDs of the applications allowed to obtain tokens for this server.')
param entraAllowedClientIds string = ''
@description('EntraBuiltIn only: also verify the bearer token inside the app (signature, issuer, tenant, audience, client, scope, role), in addition to App Service Authentication at the edge. Off by default; turn it on after the runbook in docs/11-entra-sign-in.md passes (ADR-0008).')
param entraValidateInApp bool = false
@description('Canonical MCP URL (the RFC 8707 resource clients send) when the app is reached on a custom domain. Empty means https://<default host name>/runtime/webhooks/mcp, using the host name the platform injects.')
param entraResourceUri string = ''

var entraEnabled = authMode == 'EntraBuiltIn'
// Asking for the in-app check without Entra mode would silently do nothing: fail the deployment instead.
var validateInApp = entraValidateInApp ? (entraEnabled ? true : fail('entraValidateInApp requires authMode EntraBuiltIn.')) : false
var entraAllowedApps = filter(map(split(entraAllowedClientIds, ','), id => trim(id)), id => !empty(id))
var entraTenant = empty(entraTenantId) ? tenant().tenantId : entraTenantId
// EntraBuiltIn makes the MCP webhook Anonymous; without App Service Authentication in front of it the endpoint would be open,
// and without an allowed-application list any app in the tenant could obtain a token for it. Fail the deployment instead.
var entraScope = entraEnabled
  ? (length(entraClientId) != 36
      ? fail('authMode EntraBuiltIn requires entraClientId: the client ID (GUID) of the MCP server app registration.')
      : (length(entraTenant) != 36
          ? fail('entraTenantId must be a tenant ID (GUID), or empty for the deployment tenant.')
          : (empty(entraAllowedApps)
              ? fail('authMode EntraBuiltIn requires entraAllowedClientIds: the client IDs of the applications allowed to call the server.')
              : 'api://${entraClientId}/user_impersonation')))
  : ''
var entraIssuer = '${environment().authentication.loginEndpoint}${entraTenant}/v2.0'
// In-app token validation (ADR-0008): the same tenant, server app and client allow-list that App Service Authentication gets, so the two layers cannot drift apart.
// Arrays use indexed names (Auth__Entra__AllowedClientIds__0 ...), the form Linux app settings can express.
// The authority host comes from the cloud being deployed to (login.microsoftonline.com in public Azure), like the issuer Easy Auth uses.
var entraAuthorityHost = replace(replace(environment().authentication.loginEndpoint, 'https://', ''), '/', '')
var entraInAppSettings = validateInApp ? concat([
  { name: 'Auth__Entra__ValidateBearerToken', value: 'true' }
  { name: 'Auth__Entra__TenantId', value: entraTenant }
  { name: 'Auth__Entra__ClientId', value: entraClientId }
  { name: 'Auth__Entra__AuthorityHost', value: entraAuthorityHost }
], map(range(0, length(entraAllowedApps)), i => { name: 'Auth__Entra__AllowedClientIds__${i}', value: entraAllowedApps[i] }),
  empty(entraResourceUri) ? [] : [ { name: 'Auth__Entra__ResourceUri', value: entraResourceUri } ]) : []

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
        // Edge check on the token's tid claim, in addition to the tenant-specific issuer: only this tenant's tokens reach the app.
        { name: 'WEBSITE_AUTH_AAD_ALLOWED_TENANTS', value: entraTenant }
        // Enroll the same tenant in the app (it then refuses any other tid) so scheduled maintenance also covers it. Array form:
        // Linux app setting names cannot hold a GUID key. Add workspaces with Auth__Tenants__0__Workspaces__0..n (docs/05).
        { name: 'Auth__Tenants__0__Id', value: entraTenant }
      ] : [], entraInAppSettings)
    }
  }
}

// Built-in MCP server authorization: App Service Authentication requires an Entra token for this app, issued to one of the
// allowed client applications, before any function runs. Always deployed so that switching back to FunctionKey turns it off.
resource authSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'authsettingsV2'
  properties: entraEnabled ? {
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
        validation: {
          allowedAudiences: [ 'api://${entraClientId}', entraClientId ]
          defaultAuthorizationPolicy: { allowedApplications: entraAllowedApps }
        }
      }
    }
    login: { tokenStore: { enabled: false } }
  } : {
    platform: { enabled: false }
  }
}

output name string = functionApp.name
output defaultHostName string = functionApp.properties.defaultHostName
output id string = functionApp.id
