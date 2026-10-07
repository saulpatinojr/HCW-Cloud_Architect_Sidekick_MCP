param name string
param location string
param tags object
param enablePrivateNetworking bool
param privateEndpointSubnetId string
param vnetId string

// Holds the principal-hash salt and any organization-defined sensitive patterns. Values are created out-of-band (azd hook / az cli),
// never in this template. The Function App reads them via Key Vault references with its managed identity.
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'
    networkAcls: { defaultAction: enablePrivateNetworking ? 'Deny' : 'Allow', bypass: 'AzureServices' }
  }
}

module kvPe './private-endpoint.bicep' = if (enablePrivateNetworking) {
  name: '${name}-pe'
  params: { name: 'pe-${name}', location: location, tags: tags, subnetId: privateEndpointSubnetId, vnetId: vnetId, targetResourceId: vault.id, groupId: 'vault', dnsZoneName: 'privatelink.vaultcore.azure.net' }
}

output name string = vault.name
output uri string = vault.properties.vaultUri
