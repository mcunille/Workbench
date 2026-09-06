targetScope = 'resourceGroup'
param location string = resourceGroup().location
@minLength(3)
@maxLength(12)
param prefix string
param sqlAdminObjectId string
param sqlAdminName string
param image string
param registryServer string
@description('Pre-authorized user-assigned identity with AcrPull only on the selected registry. Runtime data access uses separate system identities.')
param registryPullIdentityId string
@description('First deploy false to establish identities; populate secrets and provision SQL before setting true.')
param activate bool = false
@description('Enable after an operator creates the named Key Vault secrets, before activating workloads.')
param grantAccess bool = false
@description('Enable scheduled execution only after migrations and worker/SMTP validation; false keeps a manual job.')
param workerEnabled bool = false
@description('Keep false until isolated validation and explicit traffic/DNS authorization.')
param publishIngress bool = false
@description('Pin existing serving revision(s) on updates; a new candidate must have zero production traffic.')
param releaseTraffic array = []
param publicOrigin string
param publicHost string
@description('Existing ACA environment certificate/managed-certificate ID after authorized domain validation; no certificate bytes.')
param customDomainCertificateId string = ''
param vnetAddressPrefix string = '10.42.0.0/16'
param appsSubnetPrefix string = '10.42.0.0/23'
param endpointsSubnetPrefix string = '10.42.2.0/24'
param trustedProxyAddresses array = []
param trustedProxyNetworks array = []
param installationId string
@minValue(1)
@maxValue(10)
param maxReplicas int = 3
param workerSchedule string = '* * * * *'
param smtpHost string
param smtpPort int = 587
param smtpUsername string
param smtpSender string
param enablePublicRecovery bool = false
@description('Retained Base64 PFX and password secret-name pairs used only for decryption; pre-grant access before rotation.')
param previousCertificates array = []
param sqlSku string = 'S0'
param sqlTier string = 'Standard'
@minValue(1)
@maxValue(35)
param sqlRetentionDays int = 7
@minValue(1)
@maxValue(365)
param blobRetentionDays int = 30
@minValue(30)
param logRetentionDays int = 30
@minValue(1)
param logDailyCapGb int = 1
@minLength(1)
param alertEmails array
@description('Resource-group monthly alert budget in the billing currency; this is not a spending cap.')
@minValue(1)
param monthlyBudget int
param budgetStartDate string

module foundation 'modules/foundation.bicep' = {
  name: '${prefix}-foundation'
  params: {
    location: location
    prefix: prefix
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminName: sqlAdminName
    vnetAddressPrefix: vnetAddressPrefix
    appsSubnetPrefix: appsSubnetPrefix
    endpointsSubnetPrefix: endpointsSubnetPrefix
    sqlSku: sqlSku
    sqlTier: sqlTier
    sqlRetentionDays: sqlRetentionDays
    blobRetentionDays: blobRetentionDays
    logRetentionDays: logRetentionDays
    logDailyCapGb: logDailyCapGb
  }
}
module workloads 'modules/workloads.bicep' = {
  name: '${prefix}-workloads'
  params: {
    location: location
    prefix: prefix
    environmentId: foundation.outputs.environmentId
    sqlHost: foundation.outputs.sqlHost
    databaseName: foundation.outputs.databaseName
    containerUri: foundation.outputs.containerUri
    vaultUri: foundation.outputs.vaultUri
    image: image
    registryServer: registryServer
    registryPullIdentityId: registryPullIdentityId
    activate: activate
    workerEnabled: workerEnabled
    publishIngress: publishIngress
    releaseTraffic: releaseTraffic
    publicOrigin: publicOrigin
    publicHost: publicHost
    customDomainCertificateId: customDomainCertificateId
    trustedProxyAddresses: trustedProxyAddresses
    trustedProxyNetworks: trustedProxyNetworks
    installationId: installationId
    maxReplicas: maxReplicas
    workerSchedule: workerSchedule
    smtpHost: smtpHost
    smtpPort: smtpPort
    smtpUsername: smtpUsername
    smtpSender: smtpSender
    enablePublicRecovery: enablePublicRecovery
    previousCertificates: previousCertificates
  }
}
// In bootstrap mode create the named secret resources before granting their scoped roles.
// This avoids granting the web principal access to the migration secret at vault scope.
module access 'modules/access.bicep' = if (grantAccess) {
  name: '${prefix}-access'
  params: {
    storageName: foundation.outputs.storageName
    vaultName: foundation.outputs.vaultName
    webPrincipalId: workloads.outputs.webPrincipalId
    workerPrincipalId: workloads.outputs.workerPrincipalId
    migrationPrincipalId: workloads.outputs.migrationPrincipalId
    previousCertificates: previousCertificates
  }
}
module monitoring 'modules/monitoring.bicep' = {
  name: '${prefix}-monitoring'
  params: {
    prefix: prefix
    location: location
    logsId: foundation.outputs.logsId
    workerEnabled: activate && workerEnabled
    webEnabled: activate
    workerId: workloads.outputs.workerId
    migrationId: workloads.outputs.migrationId
    alertEmails: alertEmails
    monthlyBudget: monthlyBudget
    budgetStartDate: budgetStartDate
  }
}
output webPrincipalId string = workloads.outputs.webPrincipalId
output workerPrincipalId string = workloads.outputs.workerPrincipalId
output migrationPrincipalId string = workloads.outputs.migrationPrincipalId
output sqlHost string = foundation.outputs.sqlHost
output vaultName string = foundation.outputs.vaultName
output storageName string = foundation.outputs.storageName
output containerUri string = foundation.outputs.containerUri
output webId string = workloads.outputs.webId
output workerId string = workloads.outputs.workerId
output migrationId string = workloads.outputs.migrationId
