// The headless Obsidian Sync client (ADR-0010 phase 2, step 2) as its own Container App next to the vault server: no ingress,
// one always-on replica, the vault share mounted read-write at /vault and the state share at /state. It pulls only the
// allow-listed folders (the exclusion list is derived from adr-sidekick.md by src/VaultMcp/sync/sync.mjs) and never serves
// anything. Secrets reach it as Container Apps secret references to Key Vault, resolved with the app's identity; the
// template holds no secret value. The image is set by `azd deploy sync`; the first provision runs a placeholder (see
// containerapp.bicep for the `exists` pattern).
param name string
param location string
param tags object
param environmentId string
param identityId string
param registryLoginServer string
@description('true when the app already exists (azd sets SERVICE_SYNC_RESOURCE_EXISTS); its current image is then kept.')
param exists bool
param placeholderImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp' // any image that keeps running; nothing probes it
param containerName string = 'sync'
@description('Storage definition names in the environment: the vault share read-write, and the state share.')
param vaultStorageDefinitionName string
param stateStorageDefinitionName string
@description('Key Vault URI (https://<name>.vault.azure.net/) that holds the Obsidian secrets.')
param keyVaultUri string
@description('Name or id of the remote vault on Obsidian Sync.')
param remoteVault string
@description('Comma-separated top-level folders to exclude before adr-sidekick.md has been synced; the note takes over afterwards.')
param excludeSeed string
param intervalSeconds int = 60
@description('true: the vault uses end-to-end encryption and the secret obsidian-vault-password must exist in Key Vault.')
param vaultEncrypted bool = true
@description('true: log in with the secrets obsidian-email and obsidian-password from Key Vault. false: log in once by hand with az containerapp exec.')
param loginFromKeyVault bool = false

resource existingApp 'Microsoft.App/containerApps@2025-07-01' existing = if (exists) { name: name }

var image = exists ? existingApp!.properties.template.containers[0].image : placeholderImage

// Key Vault references: the app's identity reads the secret at start; nothing is copied into the template or its outputs.
var vaultPasswordSecret = vaultEncrypted ? [ { name: 'obsidian-vault-password', keyVaultUrl: '${keyVaultUri}secrets/obsidian-vault-password', identity: identityId } ] : []
var loginSecrets = loginFromKeyVault ? [
  { name: 'obsidian-email', keyVaultUrl: '${keyVaultUri}secrets/obsidian-email', identity: identityId }
  { name: 'obsidian-password', keyVaultUrl: '${keyVaultUri}secrets/obsidian-password', identity: identityId }
] : []

var plainEnv = [
  { name: 'OB_VAULT', value: remoteVault }
  { name: 'OB_DEVICE_NAME', value: name }
  { name: 'SYNC_EXCLUDE_SEED', value: excludeSeed }
  { name: 'SYNC_INTERVAL_SECONDS', value: string(intervalSeconds) }
  { name: 'VAULT_PATH', value: '/vault' }
  { name: 'STATE_PATH', value: '/state' }
  { name: 'HOME', value: '/state' }
]
var secretEnv = concat(
  vaultEncrypted ? [ { name: 'OB_VAULT_PASSWORD', secretRef: 'obsidian-vault-password' } ] : [],
  loginFromKeyVault ? [ { name: 'OB_EMAIL', secretRef: 'obsidian-email' }, { name: 'OB_PASSWORD', secretRef: 'obsidian-password' } ] : []
)

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
      // No ingress: nothing reaches this app from outside; it only calls Obsidian Sync.
      registries: [ { server: registryLoginServer, identity: identityId } ]
      secrets: concat(vaultPasswordSecret, loginSecrets)
    }
    template: {
      containers: [
        {
          name: containerName
          image: image
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(plainEnv, secretEnv)
          volumeMounts: [
            { volumeName: 'vault', mountPath: '/vault' }
            { volumeName: 'state', mountPath: '/state' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 } // one writer of the share, always on
      volumes: [
        // uid/gid 1001 is the non-root "sync" user of the image; the share is mounted as that user so it can write.
        { name: 'vault', storageType: 'AzureFile', storageName: vaultStorageDefinitionName, mountOptions: 'uid=1001,gid=1001,dir_mode=0770,file_mode=0660' }
        { name: 'state', storageType: 'AzureFile', storageName: stateStorageDefinitionName, mountOptions: 'uid=1001,gid=1001,dir_mode=0700,file_mode=0600' }
      ]
    }
  }
}

output name string = app.name
output id string = app.id
