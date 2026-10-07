param name string
param location string
param tags object
param subnetId string
param vnetId string
param targetResourceId string
param groupId string
param dnsZoneName string

resource dnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: dnsZoneName
  location: 'global'
  tags: tags
}

resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: dnsZone
  name: '${name}-link'
  location: 'global'
  properties: { virtualNetwork: { id: vnetId }, registrationEnabled: false }
}

resource pe 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: name
  location: location
  tags: tags
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
