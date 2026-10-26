param name string
param location string
param tags object
param databaseName string
param consistency string
param enablePrivateNetworking bool
param privateEndpointSubnetId string
param vnetId string
param vnetName string
@description('Name of the private endpoint (ADR-0009), built in main.bicep.')
param privateEndpointName string

// Serverless NoSQL account: pay per RU, no minimum throughput - right-sized for an MVP with bursty MCP traffic.
resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: name
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: { defaultConsistencyLevel: consistency }
    locations: [ { locationName: location, failoverPriority: 0, isZoneRedundant: false } ]
    // Explicit to match what Azure sets on the account. Omitted, the template implies false, so what-if shows a change
    // and a full re-provision would turn it off. No runtime effect: a serverless account runs in one region and can't add more.
    enableAutomaticFailover: true
    // Values Azure assigns to the account, pinned so what-if shows no change (verified with a what-if on 2026-10-20). The
    // only account line left in the plan is the read-only sqlEndpoint, which the template cannot set.
    defaultIdentity: 'FirstPartyIdentity'
    analyticalStorageConfiguration: { schemaType: 'WellDefined' }
    enablePerRegionPerPartitionAutoscale: false
    capabilities: [ { name: 'EnableServerless' } ]
    disableLocalAuth: true // Entra ID / managed identity only
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: enablePrivateNetworking ? 'Disabled' : 'Enabled'
    backupPolicy: { type: 'Continuous', continuousModeProperties: { tier: 'Continuous7Days' } }
    isVirtualNetworkFilterEnabled: false
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: { resource: { id: databaseName } }
}

// Every container uses the synthetic partition key "/partitionKey" = "{tenantId}|{workspaceId}".
var containers = [
  { name: 'adrs', ttl: -1, indexExcludes: [ '/human/context/?', '/human/rationale/?', '/human/consequences/?', '/observed/*' ] }
  { name: 'adr-history', ttl: -1, indexExcludes: [ '/human/*', '/observed/*' ] }
  { name: 'proposals', ttl: -1, indexExcludes: [ '/changes/*' ] }
  { name: 'evidence', ttl: -1, indexExcludes: [ '/safetyFindings/*' ] }
  { name: 'findings', ttl: -1, indexExcludes: [ '/explanation/?', '/evidence/*' ] }
  { name: 'jobs', ttl: 2592000, indexExcludes: [ '/resultJson/?', '/parameters/*' ] }
  { name: 'memory', ttl: 15552000, indexExcludes: [ '/summary/?' ] }
  { name: 'audit', ttl: 31536000, indexExcludes: [ '/summary/?' ] }
  { name: 'idempotency', ttl: 86400, indexExcludes: [ '/responseJson/?', '/requestHash/?' ] }
  { name: 'rules', ttl: -1, indexExcludes: [] }
]

resource sqlContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for c in containers: {
  parent: database
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: { paths: [ '/partitionKey' ], kind: 'Hash', version: 2 }
      defaultTtl: c.ttl
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [ { path: '/*' } ]
        excludedPaths: concat([ { path: '/"_etag"/?' } ], map(c.indexExcludes, p => { path: p }))
      }
    }
  }
}]

module cosmosPe './private-endpoint.bicep' = if (enablePrivateNetworking) {
  name: '${name}-pe'
  params: { name: privateEndpointName, location: location, tags: tags, subnetId: privateEndpointSubnetId, vnetId: vnetId, vnetName: vnetName, targetResourceId: account.id, groupId: 'Sql', dnsZoneName: 'privatelink.documents.azure.com' }
}

output name string = account.name
output endpoint string = account.properties.documentEndpoint
output id string = account.id
