// The vault server (VaultMcp.Http) as one Container App, external HTTPS ingress on port 8080, the file share mounted read-only at
// /vault, one replica. The image is set by `azd deploy` (src/VaultMcp/azure.yaml); this template never changes it once set.
//
// First `azd provision`: no image exists yet, so the app starts with Microsoft's ASP.NET Core sample image, which listens on the
// same port 8080, under TCP probes on that port, so the placeholder revision is healthy. `azd deploy` then swaps in the real image,
// which keeps working under the same TCP probes. Every later provision keeps the image the app already runs (currentImage, read by
// fetch-container-image.bicep from azd's SERVICE_VAULT_RESOURCE_EXISTS) and switches to the HTTP probes on /health, which the
// sample image does not have.
param name string
param location string
param tags object
param environmentId string
param identityId string
param registryLoginServer string
@description('The image the app already runs (from fetch-container-image.bicep), kept as is; empty on the first provision, when the placeholder is used.')
param currentImage string = ''
param placeholderImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp' // listens on 8080 like VaultMcp.Http
param containerName string = 'vault'
param targetPort int = 8080
param storageDefinitionName string
param mountPath string = '/vault'
@description('Locked (no sign-in yet: 401 on /mcp) or Entra (step 3).')
@allowed([ 'Locked', 'Entra' ])
param authMode string = 'Locked'
param entraTenantId string = ''
param entraClientId string = ''
param entraAllowedClientIds string = ''
param entraRequiredRole string = 'Vault.Reader'
@description('The canonical MCP URL (https://<fqdn>/mcp) the host puts in its challenges and metadata; built by the entry template.')
param resourceUri string = ''

var image = empty(currentImage) ? placeholderImage : currentImage
var isPlaceholder = image == placeholderImage

// Settings (the __ stands for :). None in Locked mode: with Vault__Auth__Mode unset the host stays Locked, and Anonymous would be
// refused here anyway (CONTAINER_APP_NAME). In Entra mode the host checks every value at start and refuses to run on a bad one.
var authEnv = authMode == 'Entra' ? [
  { name: 'Vault__Auth__Mode', value: 'Entra' }
  { name: 'Vault__Auth__Entra__TenantId', value: entraTenantId }
  { name: 'Vault__Auth__Entra__ClientId', value: entraClientId }
  { name: 'Vault__Auth__Entra__AllowedClientIds', value: entraAllowedClientIds }
  { name: 'Vault__Auth__Entra__RequiredRole', value: entraRequiredRole }
  { name: 'Vault__Auth__Entra__ResourceUri', value: resourceUri }
] : []

var healthProbes = [
  {
    type: 'Startup'
    httpGet: { path: '/health', port: targetPort, scheme: 'HTTP' }
    initialDelaySeconds: 2
    periodSeconds: 2
    timeoutSeconds: 2
    failureThreshold: 30
  }
  {
    type: 'Liveness'
    httpGet: { path: '/health', port: targetPort, scheme: 'HTTP' }
    initialDelaySeconds: 10
    periodSeconds: 10
    timeoutSeconds: 5
    failureThreshold: 3
  }
  {
    type: 'Readiness'
    httpGet: { path: '/health', port: targetPort, scheme: 'HTTP' }
    initialDelaySeconds: 3
    periodSeconds: 5
    timeoutSeconds: 2
    failureThreshold: 3
  }
]

// For the placeholder (and, until the next provision, for the real image it is swapped for): reachable on the target port.
var tcpProbes = [
  { type: 'Startup', tcpSocket: { port: targetPort }, initialDelaySeconds: 2, periodSeconds: 2, timeoutSeconds: 2, failureThreshold: 30 }
  { type: 'Liveness', tcpSocket: { port: targetPort }, initialDelaySeconds: 10, periodSeconds: 10, timeoutSeconds: 5, failureThreshold: 3 }
  { type: 'Readiness', tcpSocket: { port: targetPort }, initialDelaySeconds: 3, periodSeconds: 5, timeoutSeconds: 2, failureThreshold: 3 }
]

resource app 'Microsoft.App/containerApps@2025-07-01' = {
  name: name
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
      }
      registries: [ { server: registryLoginServer, identity: identityId } ]
    }
    template: {
      containers: [
        {
          name: containerName
          image: image
          // Vault__Path=/vault is baked into the image.
          env: authEnv
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          volumeMounts: [ { volumeName: 'vault', mountPath: mountPath } ]
          probes: isPlaceholder ? tcpProbes : healthProbes
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 } // one synced copy, one reader; the Sync sidecar of step 2 needs an always-on replica
      volumes: [ { name: 'vault', storageType: 'AzureFile', storageName: storageDefinitionName } ]
    }
  }
}

output name string = app.name
output id string = app.id
output fqdn string = app.properties.configuration.ingress.fqdn
output image string = image
