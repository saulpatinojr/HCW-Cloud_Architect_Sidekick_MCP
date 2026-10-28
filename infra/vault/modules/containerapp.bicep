// The vault server (VaultMcp.Http) as one Container App, external HTTPS ingress on port 8080, the file share mounted read-only at
// /vault, one replica. The image is set by `azd deploy` (src/VaultMcp/azure.yaml); this template never changes it once set.
//
// First `azd provision`: no image exists yet, so the app starts with Microsoft's ASP.NET Core sample image, which listens on the
// same port 8080, under TCP probes on that port, so the placeholder revision is healthy. `azd deploy` then swaps in the real image,
// which keeps working under the same TCP probes. Every later provision reads the image the app already runs (`exists`, fed by azd's
// SERVICE_VAULT_RESOURCE_EXISTS) and keeps it, and switches to the HTTP probes on /health, which the sample image does not have.
param name string
param location string
param tags object
param environmentId string
param identityId string
param registryLoginServer string
@description('true when a container app of this name already exists in the resource group (azd sets SERVICE_VAULT_RESOURCE_EXISTS); its current image is then kept.')
param exists bool
param placeholderImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp' // listens on 8080 like VaultMcp.Http
param containerName string = 'vault'
param targetPort int = 8080
param storageDefinitionName string
param mountPath string = '/vault'

resource existingApp 'Microsoft.App/containerApps@2025-07-01' existing = if (exists) { name: name }

var image = exists ? existingApp!.properties.template.containers[0].image : placeholderImage
var isPlaceholder = image == placeholderImage

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
          // Vault__Path=/vault is baked into the image; Vault__Auth__Mode is deliberately not set, so the host stays Locked
          // (401 on /mcp) until the sign-in layer of step 3 exists. Anonymous would be refused here anyway (CONTAINER_APP_NAME).
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
