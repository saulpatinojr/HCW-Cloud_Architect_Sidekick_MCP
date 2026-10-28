// Container registry for the vault server image. Basic tier (no private endpoint, no geo-replication: one small image).
// No admin user and no anonymous pull: the container app pulls with its managed identity (AcrPull in vault-rbac.bicep), and
// `azd deploy` builds the image inside the registry (remoteBuild in src/VaultMcp/azure.yaml), so no credential is handled anywhere.
param name string
param location string
param tags object

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled' // Basic has no private endpoint; the deployer's remote build and the pull both come over TLS with Entra tokens
    zoneRedundancy: 'Disabled'
  }
}

output name string = registry.name
output loginServer string = registry.properties.loginServer
output id string = registry.id
