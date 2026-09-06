param location string
param prefix string
param sqlAdminObjectId string
param sqlAdminName string
param vnetAddressPrefix string
param appsSubnetPrefix string
param endpointsSubnetPrefix string
param sqlSku string = 'S0'
param sqlTier string = 'Standard'
param sqlRetentionDays int = 7
param blobRetentionDays int = 30
param logRetentionDays int = 30
param logDailyCapGb int = 1

var suffix = uniqueString(resourceGroup().id)
resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${prefix}-vnet'
  location: location
  properties: {
    addressSpace: { addressPrefixes: [vnetAddressPrefix] }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [{ name: 'apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'endpoints'
        properties: { addressPrefix: endpointsSubnetPrefix, privateEndpointNetworkPolicies: 'Disabled' }
      }
    ]
  }
}
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: logRetentionDays
    workspaceCapping: { dailyQuotaGb: logDailyCapGb }
  }
}
resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${prefix}-environment'
  location: location
  properties: {
    vnetConfiguration: { infrastructureSubnetId: network.properties.subnets[0].id, internal: false }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
  }
}
resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${prefix}-sql-${suffix}'
  location: location
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdminName
      sid: sqlAdminObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}
resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'Workbench'
  location: location
  sku: { name: sqlSku, tier: sqlTier }
  properties: { requestedBackupStorageRedundancy: 'Local' }
}
resource retention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = {
  parent: database
  name: 'default'
  properties: { retentionDays: sqlRetentionDays }
}
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'wb${suffix}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
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
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: { enabled: true, days: blobRetentionDays }
    containerDeleteRetentionPolicy: { enabled: true, days: blobRetentionDays }
  }
}
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'workbench'
  properties: { publicAccess: 'None' }
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'wb-${suffix}'
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
  }
}
var dependencies = [
  { name: 'sql', id: sql.id, group: 'sqlServer', zone: 'privatelink${az.environment().suffixes.sqlServerHostname}' }
  { name: 'blob', id: storage.id, group: 'blob', zone: 'privatelink.blob.${az.environment().suffixes.storage}' }
  { name: 'vault', id: vault.id, group: 'vault', zone: 'privatelink.vaultcore.azure.net' }
]
resource zones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for dependency in dependencies: {
  name: dependency.zone
  location: 'global'
}]
resource links 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (dependency, i) in dependencies: {
  parent: zones[i]
  name: '${prefix}-link'
  location: 'global'
  properties: { virtualNetwork: { id: network.id }, registrationEnabled: false }
}]
resource endpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [for dependency in dependencies: {
  name: '${prefix}-${dependency.name}-pe'
  location: location
  properties: {
    subnet: { id: network.properties.subnets[1].id }
    privateLinkServiceConnections: [{
      name: dependency.name
      properties: { privateLinkServiceId: dependency.id, groupIds: [dependency.group] }
    }]
  }
}]
resource zoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [for (dependency, i) in dependencies: {
  parent: endpoints[i]
  name: 'default'
  properties: { privateDnsZoneConfigs: [{ name: dependency.name, properties: { privateDnsZoneId: zones[i].id } }] }
}]
output environmentId string = environment.id
output sqlHost string = sql.properties.fullyQualifiedDomainName
output databaseName string = database.name
output containerUri string = '${storage.properties.primaryEndpoints.blob}${container.name}'
output storageName string = storage.name
output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
output logsId string = logs.id
