targetScope = 'resourceGroup'

param location string = resourceGroup().location
param prefix string
param sourceAccountName string
param sourceSqlServerName string
param databaseName string = 'Workbench'
param installationId string
param image string
param imageDigest string
param schemaVersion string
@description('Semicolon-separated recovery key/certificate version identifiers, never secret values.')
param recoveryKeyVersions string
param registryServer string
param pullIdentityId string
param environmentName string
param endpointSubnetId string
param logsName string
param actionGroupId string
param schedule string = '0 3 * * *'
@description('Enable only after the container immutability policy is locked and a manual capture is verified.')
param enableSchedule bool = false
@description('First deployment only. Leave false on later deployments so a locked policy is not updated.')
param initializeProtection bool = false

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' existing = { name: environmentName }
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = { name: logsName }
resource source 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: sourceAccountName }
resource sourceBlobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = { parent: source, name: 'default' }
resource sourceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { parent: sourceBlobs, name: 'workbench' }
resource sql 'Microsoft.Sql/servers@2023-08-01' existing = { name: sourceSqlServerName }
resource database 'Microsoft.Sql/servers/databases@2023-08-01' existing = { parent: sql, name: databaseName }
resource zone 'Microsoft.Network/privateDnsZones@2020-06-01' existing = { name: 'privatelink.blob.${az.environment().suffixes.storage}' }

resource archive 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'wbb${uniqueString(resourceGroup().id, prefix)}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_GRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
  }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: archive
  name: 'default'
  properties: { containerDeleteRetentionPolicy: { enabled: true, days: 45 } }
}
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'backups'
  properties: { publicAccess: 'None' }
}
// Azure requires a separate, explicitly approved lock operation. Capture refuses an unlocked policy.
resource protection 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = if (initializeProtection) {
  parent: container
  name: 'default'
  properties: { immutabilityPeriodSinceCreationInDays: 37, allowProtectedAppendWrites: false }
}
// Run-scoped copies: no object is shared by later catalogs. Catalog logical lifetime is <=37 days,
// capture lasts <=1 day, and no bytes are eligible for deletion before 45 days from creation.
resource retention 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: archive
  name: 'default'
  properties: { policy: { rules: [{
    name: 'expired-captures'
    enabled: true
    type: 'Lifecycle'
    definition: {
      filters: { blobTypes: ['blockBlob'], prefixMatch: ['backups/'] }
      actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 45 } } }
    }
  }] } }
}
resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${prefix}-backup-pe'
  location: location
  properties: {
    subnet: { id: endpointSubnetId }
    privateLinkServiceConnections: [{ name: 'backup', properties: { privateLinkServiceId: archive.id, groupIds: ['blob'] } }]
  }
}
resource dns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: endpoint
  name: 'default'
  properties: { privateDnsZoneConfigs: [{ name: 'blob', properties: { privateDnsZoneId: zone.id } }] }
}
resource job 'Microsoft.App/jobs@2025-01-01' = {
  name: '${prefix}-backup'
  location: location
  identity: { type: 'SystemAssigned, UserAssigned', userAssignedIdentities: { '${pullIdentityId}': {} } }
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: enableSchedule ? 'Schedule' : 'Manual'
      replicaTimeout: 3600
      replicaRetryLimit: 0
      manualTriggerConfig: enableSchedule ? null : { parallelism: 1, replicaCompletionCount: 1 }
      scheduleTriggerConfig: enableSchedule ? { cronExpression: schedule, parallelism: 1, replicaCompletionCount: 1 } : null
      registries: [{ server: registryServer, identity: pullIdentityId }]
    }
    template: { containers: [{
      name: 'backup'
      image: image
      command: ['dotnet', '/opt/workbench/database/Workbench.Database.dll']
      args: ['backup', 'capture']
      resources: { cpu: json('0.25'), memory: '0.5Gi' }
      env: [
        { name: 'Backup__InstallationId', value: installationId }
        { name: 'Backup__SourceContainer', value: 'https://${sourceAccountName}.blob.${az.environment().suffixes.storage}/workbench' }
        { name: 'Backup__DestinationContainer', value: 'https://${archive.name}.blob.${az.environment().suffixes.storage}/backups' }
        { name: 'Backup__SourceAccountId', value: source.id }
        { name: 'Backup__DestinationAccountId', value: archive.id }
        { name: 'Backup__SqlResourceId', value: database.id }
        { name: 'Backup__ImageDigest', value: imageDigest }
        { name: 'Backup__SchemaVersion', value: schemaVersion }
        { name: 'Backup__RecoveryKeyVersions', value: recoveryKeyVersions }
      ]
    }] }
  }
}
resource writerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'backup-object-writer')
  properties: {
    roleName: '${prefix}-backup-object-writer'
    description: 'Read/create backup blobs. Locked immutability prevents replacement; no delete or policy authority.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{ actions: [], notActions: [], dataActions: [
      'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
      'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
    ], notDataActions: [] }]
  }
}
var readerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
resource destinationAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: container
  name: guid(container.id, job.id, 'writer')
  properties: { roleDefinitionId: writerRole.id, principalId: job.identity.principalId, principalType: 'ServicePrincipal' }
}
resource sourceAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sourceContainer
  name: guid(sourceContainer.id, job.id, 'reader')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: job.identity.principalId
    principalType: 'ServicePrincipal'
  }
}
resource sourceMetadata 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: source
  name: guid(source.id, job.id, 'metadata')
  properties: { roleDefinitionId: readerRoleId, principalId: job.identity.principalId, principalType: 'ServicePrincipal' }
}
resource destinationMetadata 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: archive
  name: guid(archive.id, job.id, 'metadata')
  properties: { roleDefinitionId: readerRoleId, principalId: job.identity.principalId, principalType: 'ServicePrincipal' }
}
resource sqlMetadata 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: database
  name: guid(database.id, job.id, 'metadata')
  properties: { roleDefinitionId: readerRoleId, principalId: job.identity.principalId, principalType: 'ServicePrincipal' }
}
resource stale 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${prefix}-backup-status'
  location: location
  properties: {
    displayName: '${prefix}: online backup missing or failed'
    description: 'No integrity-checked capture in 26 hours, or a capture failed. Investigate; do not restore automatically.'
    enabled: enableSchedule
    severity: 2
    scopes: [logs.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'P2D'
    skipQueryValidation: true
    criteria: { allOf: [{
      query: replace('''
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(26h)
| where ContainerGroupName_s startswith "__BACKUP_JOB__-"
| extend s = parse_json(Log_s)
| where tostring(s.Event) == 'OnlineBackupStatus'
| summarize Success = countif(tostring(s.Outcome) == 'IntegrityChecked'), Failed = countif(tostring(s.Outcome) in ('Failed', 'Incomplete'))
| where Success == 0 or Failed > 0
''', '__BACKUP_JOB__', '${prefix}-backup')
      timeAggregation: 'Count'
      operator: 'GreaterThan'
      threshold: 0
      failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
    }] }
    actions: { actionGroups: [actionGroupId] }
  }
}
output backupAccountName string = archive.name
output backupContainerUri string = 'https://${archive.name}.blob.${az.environment().suffixes.storage}/backups'
output jobName string = job.name

resource failedJob 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-backup-execution-failed'
  location: 'global'
  properties: {
    description: 'Online backup execution failed or timed out. Investigate without initiating recovery.'
    severity: 2
    enabled: enableSchedule
    scopes: [job.id]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{
        name: 'failed'
        criterionType: 'StaticThresholdCriterion'
        metricNamespace: 'Microsoft.App/jobs'
        metricName: 'Executions'
        dimensions: [{ name: 'state', operator: 'Include', values: ['Failed'] }]
        operator: 'GreaterThan'
        threshold: 0
        timeAggregation: 'Maximum'
      }]
    }
    actions: [{ actionGroupId: actionGroupId }]
  }
}
