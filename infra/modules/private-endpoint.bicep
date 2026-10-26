param name string
param location string
param tags object
param subnetId string
param vnetId string
@description('Name of the virtual network. The private DNS zone link takes this name: Microsoft publishes no abbreviation for it (ADR-0009).')
param vnetName string
param targetResourceId string
param groupId string
param dnsZoneName string

// The endpoint and its DNS zone are network plumbing even when they belong to a data or security resource (ADR-0009).
var networkTags = union(tags, { component: 'network' })

resource dnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: dnsZoneName
  location: 'global'
  tags: networkTags
}

resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: dnsZone
  name: vnetName
  location: 'global'
  properties: { virtualNetwork: { id: vnetId }, registrationEnabled: false }
}

resource pe 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: name
  location: location
  tags: networkTags
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [ { name: name, properties: { privateLinkServiceId: targetResourceId, groupIds: [ groupId ] } } ]
  }
}

resource dnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: pe
  name: 'default'
  properties: { privateDnsZoneConfigs: [ { name: 'config', properties: { privateDnsZoneId: dnsZone.id } } ] }
}
