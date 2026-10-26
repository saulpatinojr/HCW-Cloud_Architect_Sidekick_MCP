param name string
@description('Subnet names (ADR-0009), built in main.bicep.')
param appSubnetName string
param privateEndpointSubnetName string
param location string
param tags object

resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [ '10.40.0.0/16' ] }
    subnets: [
      {
        name: appSubnetName
        properties: {
          addressPrefix: '10.40.1.0/24'
          delegations: [ { name: 'flex', properties: { serviceName: 'Microsoft.App/environments' } } ]
        }
      }
      {
        name: privateEndpointSubnetName
        properties: { addressPrefix: '10.40.2.0/24' }
      }
    ]
  }
}

output vnetId string = vnet.id
output appSubnetId string = vnet.properties.subnets[0].id
output privateEndpointSubnetId string = vnet.properties.subnets[1].id
