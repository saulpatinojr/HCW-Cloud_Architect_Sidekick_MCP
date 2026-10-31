// Container Apps environment for the vault server, with the file share linked to it.
// Logs go to Log Analytics through a diagnostic setting (destination azure-monitor), so the template reads no workspace key.
// The user-assigned identity sits on the environment because the file-share mount authenticates with the environment's identity.
param name string
param location string
param tags object
param logAnalyticsWorkspaceId string
@description('Resource id of the user-assigned managed identity that mounts the share (ManagedIdentity mode).')
param identityId string
param storageAccountName string
param shareName string
param stateShareName string
@description('Name of the read-only storage definition the vault server mounts; the read-write one for the sync app is <name>-rw, the state one is the state share name.')
param storageDefinitionName string
@allowed([ 'ManagedIdentity', 'AccountKey' ])
param shareAuth string

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageAccountName }

resource environment 'Microsoft.App/managedEnvironments@2026-03-02-preview' = {
  name: name
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    appLogsConfiguration: { destination: 'azure-monitor' }
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
    zoneRedundant: false
  }
}

resource logs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'log-analytics'
  scope: environment
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      { category: 'ContainerAppConsoleLogs', enabled: true }
      { category: 'ContainerAppSystemLogs', enabled: true }
    ]
  }
}

// Three mounts of two shares. The vault server only reads (ReadOnly); the headless Sync app writes the vault copy and keeps
// its own login state on the second share (ReadWrite). ARM evaluates only the chosen branch of the condition, so no key is
// read in ManagedIdentity mode.
// @batchSize(1): the environment takes one storage change at a time. Deployed in parallel, the second and third links fail
// with ManagedEnvironmentStorageLockConflict ("another modification is in progress"); one after the other, they succeed.
var mounts = [
  { name: storageDefinitionName, share: shareName, access: 'ReadOnly' }
  { name: '${storageDefinitionName}-rw', share: shareName, access: 'ReadWrite' }
  { name: stateShareName, share: stateShareName, access: 'ReadWrite' }
]

@batchSize(1)
resource shares 'Microsoft.App/managedEnvironments/storages@2026-03-02-preview' = [for m in mounts: {
  parent: environment
  name: m.name
  properties: {
    azureFile: shareAuth == 'ManagedIdentity'
      ? { accountName: storageAccountName, shareName: m.share, accessMode: m.access, identity: identityId }
      : { accountName: storageAccountName, shareName: m.share, accessMode: m.access, accountKey: storageAccount.listKeys().keys[0].value }
  }
}]

output id string = environment.id
output name string = environment.name
output defaultDomain string = environment.properties.defaultDomain
output storageDefinitionName string = shares[0].name
output storageDefinitionNameReadWrite string = shares[1].name
output stateStorageDefinitionName string = shares[2].name
