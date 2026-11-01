// One storage link (an Azure Files share) on an existing Container Apps environment. The environment accepts one storage
// change at a time and keeps the lock for a while after each (ManagedEnvironmentStorageLockConflict otherwise), so the entry
// template deploys every link in its own module, one after the other, with another deployment in between where it can: the
// server's read-only link in the environment module, then the server app, then the sync app's two read-write links through
// this module twice in a chain. ARM evaluates only the chosen branch of the condition, so no key is read in ManagedIdentity mode.
param environmentName string
@description('Resource id of the user-assigned managed identity that mounts the share (ManagedIdentity mode).')
param identityId string
param storageAccountName string
param shareName string
@description('Name of the storage definition the container apps mount by.')
param definitionName string
@allowed([ 'ReadOnly', 'ReadWrite' ])
param accessMode string
@allowed([ 'ManagedIdentity', 'AccountKey' ])
param shareAuth string

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageAccountName }
resource environment 'Microsoft.App/managedEnvironments@2026-03-02-preview' existing = { name: environmentName }

resource link 'Microsoft.App/managedEnvironments/storages@2026-03-02-preview' = {
  parent: environment
  name: definitionName
  properties: {
    azureFile: shareAuth == 'ManagedIdentity'
      ? { accountName: storageAccountName, shareName: shareName, accessMode: accessMode, identity: identityId }
      : { accountName: storageAccountName, shareName: shareName, accessMode: accessMode, accountKey: storageAccount.listKeys().keys[0].value }
  }
}

output name string = link.name
