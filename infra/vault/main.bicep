// Obsidian vault MCP server (ADR-0010, phase 2, Route A) - Azure infrastructure (subscription scope, azd compatible).
// Creates, in its own resource group: user-assigned identity, Log Analytics, container registry, storage account with one SMB
// file share (the synced vault copy), Key Vault (the step 2 Sync credentials go there, out of band), a Container Apps
// environment with the share linked read-only, the vault server as one Container App, and the role assignments.
// It shares nothing with the ADR server's deployment (infra/main.bicep) except the Bicep modules and this naming convention.
// No secrets are emitted: the image is pulled with the identity, the share is mounted with the identity, logs flow by diagnostic setting.
// Names and tags follow ADR-0009 (docs/adr/ADR-0009-azure-naming-convention.md): <abbr>-<workload>-<environment>-<region>-<nn>.
// azd project: src/VaultMcp/azure.yaml (service "vault", remote image build in the registry).
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment (for example vault-dev). Used for azd bookkeeping and the azd-env-name tag; it is NOT part of the resource names (see workloadName and environmentShort).')
param environmentName string

@minLength(1)
@description('Primary Azure region (must be listed in infra/region-codes.json).')
param location string

@minLength(2)
@maxLength(8)
@description('Short workload name used in every resource name: lower-case letters and digits only, starting with "vault" (ADR-0009; the ADR server\'s template refuses names that start with vault, so the two can never collide).')
param workloadName string = 'vault'

@minLength(2)
@maxLength(5)
@description('Short environment name used in every resource name, for example dev, test or prod: lower-case letters and digits only (ADR-0009). environmentName must end with "-<environmentShort>" (vault-dev with dev).')
param environmentShort string = 'dev'

@maxLength(4)
@description('Optional text appended to the three globally unique names (container registry, Key Vault, storage account): lower-case letters and digits only. Leave empty; set it, for example to "b", only when one of those names is taken (ADR-0009).')
param nameSuffix string = ''

@description('Optional resource group name override. Empty means the convention name, for example rg-vault-dev-scus-01.')
param resourceGroupName string = ''

@description('Set by azd (SERVICE_VAULT_RESOURCE_EXISTS): true when the container app already exists, so that the image azd deployed is kept across provisions.')
param vaultExists bool = false

@description('How the Container Apps environment mounts the file share. ManagedIdentity (default): SMB OAuth with the environment identity, no key anywhere (preview feature). AccountKey: fallback if the preview is not available in your region; the account key is then read inside the deployment and shared-key access is on for this storage account.')
@allowed([ 'ManagedIdentity', 'AccountKey' ])
param shareAuth string = 'ManagedIdentity'

@description('Size limit of the vault file share in GiB.')
@minValue(1)
@maxValue(100)
param shareQuotaGiB int = 5

@description('Opt-in: principal id (object id) of the person who will put the step 2 secrets into Key Vault (Key Vault Secrets Officer). Set VAULT_DEPLOYER_PRINCIPAL_ID yourself; it is deliberately not AZURE_PRINCIPAL_ID, which azd fills in on every provision, so nobody gets the role by accident.')
param deployerPrincipalId string = ''

// ---- Naming (ADR-0009) --------------------------------------------------------------------------------------------------
// Pattern: <abbreviation>-<workload>-<environment>-<region code>-<instance>, for example ca-vault-dev-scus-01.
// The storage account and the container registry allow lower-case letters and digits only, so they have no hyphens.
var abbrs = loadJsonContent('../abbreviations.json')
var regionCodes = loadJsonContent('../region-codes.json')
var regionKey = toLower(replace(location, ' ', ''))
var regionCode = regionCodes[?regionKey] ?? ''
var instance = '01'
var nameTail = '${environmentShort}-${regionCode}-${instance}'
var baseName = '${workloadName}-${nameTail}'

// The three names that must be unique across all of Azure take the optional suffix.
var keyVaultName = '${abbrs.keyVaultVaults}-${baseName}${nameSuffix}'
var storageAccountName = '${abbrs.storageStorageAccounts}${workloadName}${environmentShort}${regionCode}${instance}${nameSuffix}'
var registryName = '${abbrs.containerRegistryRegistries}${workloadName}${environmentShort}${regionCode}${instance}${nameSuffix}'

// Stop before anything is created, with a message that says what to change. The Key Vault limit (24 characters) is the tightest:
// the storage account (24, no hyphens) and the registry (50) names fit whenever the vault name fits, and the container app (32)
// fits with the longest allowed parts. The unit test ResourceNamingTests checks every name for every region.
func isLowerAlphanumeric(value string) bool => empty(filter(map(range(0, length(value)), i => substring(value, i, 1)), c => !contains('abcdefghijklmnopqrstuvwxyz0123456789', c)))

var nameProblem = empty(regionCode)
  ? 'Region "${location}" has no short code. Add it to infra/region-codes.json and to the region table in docs/adr/ADR-0009-azure-naming-convention.md, then deploy again.'
  : !(isLowerAlphanumeric(workloadName) && isLowerAlphanumeric(environmentShort) && isLowerAlphanumeric(nameSuffix))
    ? 'workloadName, environmentShort and nameSuffix may hold lower-case letters and digits only (ADR-0009).'
    : !startsWith(workloadName, 'vault')
      ? 'workloadName "${workloadName}" must start with "vault" (ADR-0009): the ADR server (infra/main.bicep) refuses such names, so the two deployments can never share a resource group or a global name. Set VAULT_WORKLOAD_NAME to vault, or to vault followed by letters or digits.'
    : !(environmentName == environmentShort || endsWith(environmentName, '-${environmentShort}'))
      ? 'environmentName "${environmentName}" must end with "-${environmentShort}". The azd environment name is not part of any resource name, so two azd environments in one subscription and region with the same environmentShort would get the same names and overwrite each other. Name the azd environment vault-<short> (for example vault-prod) and set VAULT_ENVIRONMENT_SHORT to <short> for it (ADR-0009).'
      : length(keyVaultName) > 24
        ? 'The Key Vault name ${keyVaultName} is longer than the 24 characters Azure allows. Shorten workloadName, environmentShort or nameSuffix.'
        : ''

var names = !empty(nameProblem)
  ? fail(nameProblem)
  : {
    resourceGroup: !empty(resourceGroupName) ? resourceGroupName : '${abbrs.resourcesResourceGroups}-${baseName}'
    identity: '${abbrs.managedIdentityUserAssignedIdentities}-${baseName}'
    logAnalytics: '${abbrs.operationalInsightsWorkspaces}-${baseName}'
    registry: registryName
    storageAccount: storageAccountName
    keyVault: keyVaultName
    environment: '${abbrs.appManagedEnvironments}-${baseName}'
    containerApp: '${abbrs.appContainerApps}-${baseName}'
  }

// Data names (not Azure resource names, ADR-0009 decision 8).
var shareName = 'vault'          // the file share and the environment's storage definition
var azdServiceName = 'vault'     // the service in src/VaultMcp/azure.yaml; azd finds the container app by this tag

// Tags (ADR-0009). 'component' is added per module below. 'azd-env-name' stays: azd needs it to find the environment's resources.
var tags = {
  'azd-env-name': environmentName
  workload: workloadName
  application: 'vault-mcp'
  environment: environmentShort
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: names.resourceGroup
  location: location
  tags: tags
}

module identity '../modules/identity.bicep' = {
  name: 'identity'
  scope: rg
  params: { name: names.identity, location: location, tags: union(tags, { component: 'security' }) }
}

module logAnalytics './modules/loganalytics.bicep' = {
  name: 'loganalytics'
  scope: rg
  params: { name: names.logAnalytics, location: location, tags: union(tags, { component: 'monitoring' }) }
}

module registry './modules/containerregistry.bicep' = {
  name: 'registry'
  scope: rg
  params: { name: names.registry, location: location, tags: union(tags, { component: 'api' }) }
}

module storage './modules/fileshare-storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    name: names.storageAccount
    location: location
    tags: union(tags, { component: 'data' })
    shareName: shareName
    shareQuotaGiB: shareQuotaGiB
    shareAuth: shareAuth
  }
}

// Holds the step 2 secrets (Obsidian Sync credentials and the vault password), created out of band by the owner, never here.
module keyVault '../modules/keyvault.bicep' = {
  name: 'keyvault'
  scope: rg
  params: {
    name: names.keyVault
    privateEndpointName: '' // no private networking in this template (the module creates no endpoint when it is off)
    location: location
    tags: union(tags, { component: 'security' })
    enablePrivateNetworking: false
    privateEndpointSubnetId: ''
    vnetId: ''
    vnetName: ''
  }
}

// Roles first: the environment's share link needs the SMB role, and the app's first pull needs AcrPull. Azure may still take a
// minute to propagate a new assignment; if the first provision fails at the share link with an authorization error, run it again.
module rbac './modules/vault-rbac.bicep' = {
  name: 'rbac'
  scope: rg
  params: {
    principalId: identity.outputs.principalId
    deployerPrincipalId: deployerPrincipalId
    registryName: registry.outputs.name
    storageAccountName: storage.outputs.name
    keyVaultName: keyVault.outputs.name
    shareAuth: shareAuth
  }
}

module environment './modules/containerapps-environment.bicep' = {
  name: 'environment'
  scope: rg
  dependsOn: [ rbac ]
  params: {
    name: names.environment
    location: location
    tags: union(tags, { component: 'api' })
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
    identityId: identity.outputs.id
    storageAccountName: storage.outputs.name
    shareName: storage.outputs.shareName
    storageDefinitionName: shareName
    shareAuth: shareAuth
  }
}

module containerApp './modules/containerapp.bicep' = {
  name: 'containerapp'
  scope: rg
  params: {
    name: names.containerApp
    location: location
    tags: union(tags, { component: 'api', 'azd-service-name': azdServiceName })
    environmentId: environment.outputs.id
    identityId: identity.outputs.id
    registryLoginServer: registry.outputs.loginServer
    exists: vaultExists
    storageDefinitionName: environment.outputs.storageDefinitionName
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.outputs.loginServer   // azd pushes (and remote-builds) the image here
output AZURE_CONTAINER_REGISTRY_NAME string = registry.outputs.name
output AZURE_CONTAINER_APPS_ENVIRONMENT string = environment.outputs.name
output AZURE_CONTAINER_APP_NAME string = containerApp.outputs.name
output VAULT_MCP_ENDPOINT string = 'https://${containerApp.outputs.fqdn}/mcp'
output VAULT_HEALTH_URL string = 'https://${containerApp.outputs.fqdn}/health'
output AZURE_STORAGE_ACCOUNT string = storage.outputs.name
output AZURE_FILE_SHARE string = storage.outputs.shareName
output AZURE_KEY_VAULT_NAME string = keyVault.outputs.name
output AZURE_MANAGED_IDENTITY_CLIENT_ID string = identity.outputs.clientId
