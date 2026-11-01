// Step 2 (ADR-0010): the two read-write links the headless Sync app mounts, added to the existing Container Apps environment:
// the vault share (the same share the server mounts read-only) and the Sync client's own state share. Created only when sync
// is on, by the entry template after the server app, and one at a time (@batchSize(1)): the environment refuses a storage
// change while the previous one is still being applied (ManagedEnvironmentStorageLockConflict), which a real provision hit
// when the links followed each other directly. If a provision still stops on the second link, run it again: the first link
// is then already there and only the second is applied. ARM evaluates only the chosen branch of the condition, so no key is
// read in ManagedIdentity mode.
param environmentName string
@description('Resource id of the user-assigned managed identity that mounts the shares (ManagedIdentity mode).')
param identityId string
param storageAccountName string
param shareName string
param stateShareName string
@description('Name of the read-only storage definition the vault server mounts; the read-write one made here is <name>-rw, the state one is the state share name.')
param storageDefinitionName string
@allowed([ 'ManagedIdentity', 'AccountKey' ])
param shareAuth string

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageAccountName }
resource environment 'Microsoft.App/managedEnvironments@2026-03-02-preview' existing = { name: environmentName }

var mounts = [
  { name: '${storageDefinitionName}-rw', share: shareName }
  { name: stateShareName, share: stateShareName }
]

@batchSize(1)
resource shares 'Microsoft.App/managedEnvironments/storages@2026-03-02-preview' = [for m in mounts: {
  parent: environment
  name: m.name
  properties: {
    azureFile: shareAuth == 'ManagedIdentity'
      ? { accountName: storageAccountName, shareName: m.share, accessMode: 'ReadWrite', identity: identityId }
      : { accountName: storageAccountName, shareName: m.share, accessMode: 'ReadWrite', accountKey: storageAccount.listKeys().keys[0].value }
  }
}]

output storageDefinitionNameReadWrite string = shares[0].name
output stateStorageDefinitionName string = shares[1].name
