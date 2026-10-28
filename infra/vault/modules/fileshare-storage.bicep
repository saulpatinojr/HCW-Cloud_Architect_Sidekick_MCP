// Storage account with one SMB file share: the decrypted copy of the allow-listed vault folders that the headless Sync
// container (step 2) writes and the vault server reads. The share is mounted into Container Apps; the mount authenticates
// with the environment's managed identity (SMB OAuth, preview) by default, so the account keeps shared-key access off.
// AccountKey is the fallback if the preview is not available: then the mount uses the account key and shared-key access is on.
param name string
param location string
param tags object
param shareName string
@description('Size limit of the share in GiB. A vault of Markdown notes is small; raise it if Obsidian attachments are allow-listed.')
param shareQuotaGiB int = 5
@allowed([ 'ManagedIdentity', 'AccountKey' ])
param shareAuth string

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' } // a synced copy, re-creatable from Obsidian Sync; locally redundant is enough
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: shareAuth == 'AccountKey'
    defaultToOAuthAuthentication: true
    // The consumption Container Apps environment has no virtual network, so the mount reaches the share over the public
    // endpoint (SMB 3 with encryption). Private networking for the vault server is a later step, with the environment's own subnet.
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow', bypass: 'AzureServices' }
    // "Managed Identity for SMB": lets a managed identity that holds Storage File Data SMB MI Admin mount the share without a key.
    azureFilesIdentityBasedAuthentication: {
      directoryServiceOptions: 'None'
      smbOAuthSettings: { isSmbOAuthEnabled: shareAuth == 'ManagedIdentity' }
    }
    encryption: {
      services: { file: { enabled: true } }
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
    }
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    shareDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-01-01' = {
  parent: fileService
  name: shareName
  properties: {
    shareQuota: shareQuotaGiB
    enabledProtocols: 'SMB'
  }
}

output name string = storage.name
output id string = storage.id
output shareName string = share.name
