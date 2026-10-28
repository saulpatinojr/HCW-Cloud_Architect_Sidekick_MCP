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
@description('Name of the storage definition inside the environment; the container app refers to it by this name.')
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

// The vault server only reads, so this definition is read-only. Step 2 adds a second, read-write definition of the same share
// for the headless Sync container. ARM evaluates only the chosen branch of the condition, so no key is read in ManagedIdentity mode.
resource vaultShare 'Microsoft.App/managedEnvironments/storages@2026-03-02-preview' = {
  parent: environment
  name: storageDefinitionName
  properties: {
    azureFile: shareAuth == 'ManagedIdentity'
      ? { accountName: storageAccountName, shareName: shareName, accessMode: 'ReadOnly', identity: identityId }
      : { accountName: storageAccountName, shareName: shareName, accessMode: 'ReadOnly', accountKey: storageAccount.listKeys().keys[0].value }
  }
}

output id string = environment.id
output name string = environment.name
output defaultDomain string = environment.properties.defaultDomain
output storageDefinitionName string = vaultShare.name
