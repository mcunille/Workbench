targetScope = 'resourceGroup'

param prefix string
param sqlServerName string
param databaseName string = 'Workbench'
param storageName string
param vaultName string
param registryName string
param logsName string
param actionGroupId string
@description('Existing native Backup vault. Empty when native backup has not been configured.')
param backupVaultName string = ''
@minValue(30)
@maxValue(30)
param logRetentionDays int = 30
@description('Only tables already created by ingestion. Leave empty for first setup; the workspace default must already be 30 days.')
@allowed([
  'AzureDiagnostics'
  'AZKVAuditLogs'
  'StorageBlobLogs'
  'ContainerRegistryLoginEvents'
  'ContainerRegistryRepositoryEvents'
  'CoreAzureBackup'
  'AddonAzureBackupJobs'
  'AddonAzureBackupPolicy'
  'AddonAzureBackupProtectedInstance'
])
param retentionTableNames array = []

resource sql 'Microsoft.Sql/servers@2025-01-01' existing = { name: sqlServerName }
resource database 'Microsoft.Sql/servers/databases@2023-08-01' existing = { parent: sql, name: databaseName }
resource master 'Microsoft.Sql/servers/databases@2023-08-01' existing = { parent: sql, name: 'master' }
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = { parent: storage, name: 'default' }
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: vaultName }
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = { name: registryName }
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = { name: logsName }
resource backup 'Microsoft.DataProtection/backupVaults@2025-07-01' existing = if (!empty(backupVaultName)) { name: backupVaultName }

// Authentication events only. Batch and query text can contain recovery tokens and other secrets.
resource sqlAudit 'Microsoft.Sql/servers/auditingSettings@2023-08-01' = {
  parent: sql
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
    auditActionsAndGroups: [
      'SUCCESSFUL_DATABASE_AUTHENTICATION_GROUP'
      'FAILED_DATABASE_AUTHENTICATION_GROUP'
    ]
  }
}
resource sqlDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: database
  name: '${prefix}-security'
  properties: { workspaceId: logs.id, logs: [{ category: 'SQLSecurityAuditEvents', enabled: true }] }
}
// Server audit configuration requires master routing as well as the application database.
resource masterDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: master
  name: '${prefix}-security'
  properties: { workspaceId: logs.id, logs: [{ category: 'SQLSecurityAuditEvents', enabled: true }] }
}
resource vaultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: vault
  name: '${prefix}-security'
  properties: { workspaceId: logs.id, logAnalyticsDestinationType: 'Dedicated', logs: [{ category: 'AuditEvent', enabled: true }] }
}
resource blobDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: blobs
  name: '${prefix}-security'
  properties: {
    workspaceId: logs.id
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'StorageRead', enabled: true }
      { category: 'StorageWrite', enabled: true }
      { category: 'StorageDelete', enabled: true }
    ]
  }
}
resource registryDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: registry
  name: '${prefix}-security'
  properties: {
    workspaceId: logs.id
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'ContainerRegistryLoginEvents', enabled: true }
      { category: 'ContainerRegistryRepositoryEvents', enabled: true }
    ]
  }
}
resource backupDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(backupVaultName)) {
  scope: backup
  name: '${prefix}-security'
  properties: {
    workspaceId: logs.id
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      { category: 'CoreAzureBackup', enabled: true }
      { category: 'AddonAzureBackupJobs', enabled: true }
      { category: 'AddonAzureBackupPolicy', enabled: true }
      { category: 'AddonAzureBackupProtectedInstance', enabled: true }
    ]
  }
}
resource retention 'Microsoft.OperationalInsights/workspaces/tables@2022-10-01' = [for table in retentionTableNames: {
  parent: logs
  name: table
  properties: { retentionInDays: logRetentionDays, totalRetentionInDays: logRetentionDays }
}]
resource sqlLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: sql
  name: '${prefix}-protect-sql'
  properties: { level: 'CanNotDelete', notes: 'Remove only through an approved database retirement or recovery procedure. Does not prevent an Owner removing the lock.' }
}
resource backupLock 'Microsoft.Authorization/locks@2020-05-01' = if (!empty(backupVaultName)) {
  scope: backup
  name: '${prefix}-protect-backup'
  properties: { level: 'CanNotDelete', notes: 'Protect native recovery resources from accidental deletion. Vault immutability is a separate, explicit operation.' }
}

// Route the native service's built-in failures without relying on delayed diagnostic ingestion.
resource backupNotifications 'Microsoft.AlertsManagement/actionRules@2021-08-08' = if (!empty(backupVaultName)) {
  name: '${prefix}-backup-notifications'
  location: 'global'
  properties: {
    enabled: true
    description: 'Deliver native Backup vault alerts to the Workbench operations group.'
    scopes: [backup.id]
    actions: [{ actionType: 'AddActionGroups', actionGroupIds: [actionGroupId] }]
  }
}

// Direct Activity Log evaluation continues even if Log Analytics ingestion reaches its daily cap.
resource administrativeChanges 'Microsoft.Insights/activityLogAlerts@2020-10-01' = {
  name: '${prefix}-security-changes'
  location: 'global'
  properties: {
    enabled: true
    scopes: [subscription().id]
    description: 'Review permission, network, protection and monitoring changes. Some events are legitimate deployments; correlate with approved changes.'
    condition: {
      allOf: [
        { field: 'category', equals: 'Administrative' }
        { field: 'status', equals: 'Succeeded' }
        { anyOf: [for operation in loadJsonContent('security-operations.json'): { field: 'operationName', equals: operation }] }
      ]
    }
    actions: { actionGroups: [{ actionGroupId: actionGroupId }] }
  }
}
