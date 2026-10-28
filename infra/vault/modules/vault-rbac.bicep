// Least-privilege role assignments for the vault server's identity, and optional ones for the person who runs azd.
param principalId string
param deployerPrincipalId string
param registryName string
param storageAccountName string
param keyVaultName string
@allowed([ 'ManagedIdentity', 'AccountKey' ])
param shareAuth string

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = { name: registryName }
resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = { name: storageAccountName }
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }

// Built-in role definition ids (stable GUIDs, Microsoft Learn "Azure built-in roles").
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  storageFileDataSmbMiAdmin: 'a235d3ee-5935-4cfb-8cc5-a3303ad5995e' // the only built-in role for managed-identity SMB mounts; the mount itself is read-only
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
}

// --- The container app's identity ---------------------------------------------------------------
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, principalId, roles.acrPull)
  scope: registry
  properties: { principalId: principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull) }
}
resource smbMount 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (shareAuth == 'ManagedIdentity') {
  name: guid(storage.id, principalId, roles.storageFileDataSmbMiAdmin)
  scope: storage
  properties: { principalId: principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageFileDataSmbMiAdmin) }
}
// Step 2 (headless Sync) reads the Sync credentials and the vault password from this vault.
resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, principalId, roles.keyVaultSecretsUser)
  scope: vault
  properties: { principalId: principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser) }
}

// --- Optional deployer (the person running azd; dev only): may put the step 2 secrets into Key Vault ---------------------
resource deployerKv 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(vault.id, deployerPrincipalId, roles.keyVaultSecretsOfficer)
  scope: vault
  properties: { principalId: deployerPrincipalId, roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer) }
}
