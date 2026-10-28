// Log Analytics workspace on its own (no Application Insights): the Container Apps environment sends its console and
// system logs here through a diagnostic setting, so no workspace key is read anywhere.
param name string
param location string
param tags object

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

output id string = logAnalytics.id
output name string = logAnalytics.name
