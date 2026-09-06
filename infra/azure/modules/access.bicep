param storageName string
param vaultName string
param webPrincipalId string
param workerPrincipalId string
param migrationPrincipalId string
param previousCertificates array = []

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = { parent: storage, name: 'default' }
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { parent: blobs, name: 'workbench' }
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: vaultName }
var runtimePrincipals = [webPrincipalId, workerPrincipalId]
resource blobRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principal in runtimePrincipals: {
  name: guid(container.id, principal, 'blob-contributor')
  scope: container
  properties: {
    principalId: principal
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}]
var previousPfxNames = [for certificate in previousCertificates: certificate.secretName]
var previousPasswordNames = [for certificate in previousCertificates: certificate.passwordSecretName]
var previousSecretNames = concat(previousPfxNames, previousPasswordNames)
var secretNames = concat(['tenant-proof', 'protection-pfx', 'protection-password', 'smtp-password', 'migration-connection'], previousSecretNames)
resource secrets 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = [for name in secretNames: {
  parent: vault
  name: name
}]
var baseAssignments = [
  { principal: webPrincipalId, secretIndex: 0 }
  { principal: webPrincipalId, secretIndex: 1 }
  { principal: webPrincipalId, secretIndex: 2 }
  { principal: webPrincipalId, secretIndex: 3 }
  { principal: workerPrincipalId, secretIndex: 0 }
  { principal: workerPrincipalId, secretIndex: 1 }
  { principal: workerPrincipalId, secretIndex: 2 }
  { principal: workerPrincipalId, secretIndex: 3 }
  { principal: migrationPrincipalId, secretIndex: 4 }
]
var webPrevious = [for (name, i) in previousSecretNames: { principal: webPrincipalId, secretIndex: i + 5 }]
var workerPrevious = [for (name, i) in previousSecretNames: { principal: workerPrincipalId, secretIndex: i + 5 }]
var assignments = concat(baseAssignments, webPrevious, workerPrevious)
resource secretRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for assignment in assignments: {
  name: guid(vault.id, secretNames[assignment.secretIndex], assignment.principal, 'secret-reader')
  scope: secrets[assignment.secretIndex]
  properties: {
    principalId: assignment.principal
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}]
