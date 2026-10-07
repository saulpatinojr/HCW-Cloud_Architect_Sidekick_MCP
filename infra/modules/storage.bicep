param name string
param location string
param tags object
param blobContainers array
param jobsQueueName string
param enablePrivateNetworking bool
param privateEndpointSubnetId string
param vnetId string
param enableDefenderForStorage bool

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_ZRS' }
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // identity only; upload grants use user-delegation SAS
    defaultToOAuthAuthentication: true
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'
    networkAcls: { defaultAction: enablePrivateNetworking ? 'Deny' : 'Allow', bypass: 'AzureServices' }
    encryption: {
      services: { blob: { enabled: true }, queue: { enabled: true } }
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 30 }
    containerDeleteRetentionPolicy: { enabled: true, days: 30 }
    isVersioningEnabled: true
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for c in blobContainers: {
  parent: blobService
  name: c
  properties: { publicAccess: 'None' }
}]

// Approved evidence is an immutable snapshot: time-based retention, unlocked so retention can be tuned, and protected from deletion.
resource evidenceImmutability 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = {
  parent: containers[2] // 'evidence'
  name: 'default'
  properties: {
    immutabilityPeriodSinceCreationInDays: 365
    allowProtectedAppendWrites: false
  }
}

// Lifecycle: anything left in quarantine for a day was never registered; purge it.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'purge-quarantine'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: [ 'blockBlob' ], prefixMatch: [ 'uploads-quarantine/' ] }
            actions: { baseBlob: { delete: { daysAfterCreationGreaterThan: 1 } } }
          }
        }
        {
          name: 'cool-reports'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: [ 'blockBlob' ], prefixMatch: [ 'reports/' ] }
            actions: { baseBlob: { tierToCool: { daysAfterModificationGreaterThan: 30 } } }
          }
        }
      ]
    }
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource jobsQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: jobsQueueName
}

resource poisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: '${jobsQueueName}-poison'
}

// Microsoft Defender for Storage (malware scanning on upload writes a blob index tag the server checks when Uploads:RequireDefenderScanResult=true).
resource defender 'Microsoft.Security/defenderForStorageSettings@2022-12-01-preview' = if (enableDefenderForStorage) {
  name: 'current'
  scope: storage
  properties: {
    isEnabled: true
    malwareScanning: { onUpload: { isEnabled: true, capGBPerMonth: 1000 } }
    sensitiveDataDiscovery: { isEnabled: false }
    overrideSubscriptionLevelSettings: true
  }
}

module blobPe './private-endpoint.bicep' = if (enablePrivateNetworking) {
  name: '${name}-blob-pe'
  params: { name: 'pe-${name}-blob', location: location, tags: tags, subnetId: privateEndpointSubnetId, vnetId: vnetId, targetResourceId: storage.id, groupId: 'blob', dnsZoneName: 'privatelink.blob.${environment().suffixes.storage}' }
}

module queuePe './private-endpoint.bicep' = if (enablePrivateNetworking) {
  name: '${name}-queue-pe'
  params: { name: 'pe-${name}-queue', location: location, tags: tags, subnetId: privateEndpointSubnetId, vnetId: vnetId, targetResourceId: storage.id, groupId: 'queue', dnsZoneName: 'privatelink.queue.${environment().suffixes.storage}' }
}

output name string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
output queueEndpoint string = storage.properties.primaryEndpoints.queue
