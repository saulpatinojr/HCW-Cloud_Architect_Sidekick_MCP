resource stGood 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stordersprod'
  location: 'eastus2'
  sku: { name: 'Standard_ZRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    allowSharedKeyAccess: false
  }
}

resource stDrift 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stbillinglegacy'
  location: 'eastus2'
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_0'
    publicNetworkAccess: 'Enabled'
  }
}
